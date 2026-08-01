using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace LLJDeviceLink;

public sealed class DiscoveredDevice
{
    public ulong Address { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; set; } = "未知 BLE 设备";
    public DeviceKind Kind { get; set; }
    public short Rssi { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public LinkState State { get; set; } = LinkState.Discovered;
    public string Error { get; set; } = "";

    public string KindLabel => Kind switch
    {
        DeviceKind.YcyEmsV1 => "役次元电击器一代",
        DeviceKind.YcyEmsV2 => "役次元电击器二代",
        DeviceKind.CoyoteV3 => "郊狼 3.0 电击器",
        DeviceKind.YcyToy => "役次元飞机杯/跳蛋",
        _ => "未识别设备"
    };
}

public sealed class BleManager : IDisposable
{
    public static readonly Guid YcyEmsService = Guid.Parse("0000ff30-0000-1000-8000-00805f9b34fb");
    public static readonly Guid YcyToyService = Guid.Parse("0000ff40-0000-1000-8000-00805f9b34fb");
    public static readonly Guid CoyoteService = Guid.Parse("0000180c-0000-1000-8000-00805f9b34fb");

    private readonly ConcurrentDictionary<string, DiscoveredDevice> _discovered = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DeviceSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _connectionScanAttempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly WaveformLibrary _waveforms;
    private readonly ConfigStore _config;
    private BluetoothLEAdvertisementWatcher? _watcher;
    private CancellationTokenSource? _scanTimeout;
    private DateTimeOffset? _scanEndsAt;
    private bool _connectDuringScan;
    private int _disposed;

    public BleManager(WaveformLibrary waveforms, ConfigStore config)
    {
        _waveforms = waveforms;
        _config = config;
    }

    public IReadOnlyList<DiscoveredDevice> Discovered => _discovered.Values.OrderBy(x => x.Name).ToList();
    public IReadOnlyList<DeviceSession> Sessions => _sessions.Values.OrderBy(x => x.Device.Name).ToList();
    public bool IsScanning => _watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started;
    public string LastScanError { get; private set; } = "";
    public int ScanSecondsRemaining => !IsScanning || _scanEndsAt is null
        ? 0
        : Math.Max(0, (int)Math.Ceiling((_scanEndsAt.Value - DateTimeOffset.UtcNow).TotalSeconds));

    public bool StartConnectionScan(TimeSpan duration)
    {
        if (_disposed != 0) return false;
        StopScan();
        LastScanError = "";

        foreach (var pair in _discovered)
            if (!_sessions.ContainsKey(pair.Key)) _discovered.TryRemove(pair.Key, out _);
        _connectionScanAttempted.Clear();
        _connectDuringScan = true;
        _scanEndsAt = DateTimeOffset.UtcNow.Add(duration);
        _scanTimeout = new CancellationTokenSource();
        _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        _watcher.Received += WatcherOnReceived;
        _watcher.Stopped += WatcherOnStopped;
        try
        {
            _watcher.Start();
        }
        catch (Exception ex)
        {
            LastScanError = ex.Message;
            _watcher.Received -= WatcherOnReceived;
            _watcher.Stopped -= WatcherOnStopped;
            _watcher = null;
            _scanTimeout.Dispose();
            _scanTimeout = null;
            _scanEndsAt = null;
            _connectDuringScan = false;
            Plugin.LogSource.LogError($"BLE 扫描启动失败：{ex}");
            return false;
        }
        _ = StopScanAfterAsync(duration, _scanTimeout);
        Plugin.LogSource.LogInfo($"BLE 连接扫描已启动，将在 {duration.TotalSeconds:0} 秒后停止");
        return true;
    }

    public void StopScan()
    {
        var timeout = Interlocked.Exchange(ref _scanTimeout, null);
        if (timeout is not null)
        {
            try { timeout.Cancel(); } catch { /* ignored */ }
            timeout.Dispose();
        }

        var watcher = _watcher;
        _watcher = null;
        _scanEndsAt = null;
        _connectDuringScan = false;
        if (watcher is null) return;
        watcher.Received -= WatcherOnReceived;
        watcher.Stopped -= WatcherOnStopped;
        try { watcher.Stop(); } catch { /* ignored */ }
        Plugin.LogSource.LogInfo("BLE 连接扫描已停止");
    }

    private async Task StopScanAfterAsync(TimeSpan duration, CancellationTokenSource owner)
    {
        try
        {
            await Task.Delay(duration, owner.Token).ConfigureAwait(false);
            if (ReferenceEquals(_scanTimeout, owner)) StopScan();
        }
        catch (OperationCanceledException) { }
    }

    private void WatcherOnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (args.Error != BluetoothError.Success)
            Plugin.LogSource.LogWarning($"BLE 扫描停止：{args.Error}");
    }

    private void WatcherOnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var name = (args.Advertisement.LocalName ?? "").Trim();
        var kind = Classify(name, args.Advertisement.ServiceUuids);
        if (kind == DeviceKind.Unknown) return;

        var id = args.BluetoothAddress.ToString("X12");
        var device = _discovered.GetOrAdd(id, _ => new DiscoveredDevice
        {
            Address = args.BluetoothAddress,
            Id = id,
            Name = string.IsNullOrWhiteSpace(name) ? KindName(kind) : name,
            Kind = kind
        });
        device.Rssi = args.RawSignalStrengthInDBm;
        device.LastSeen = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(name)) device.Name = name;
        device.Kind = kind;

        if (_connectDuringScan && _connectionScanAttempted.TryAdd(id, 0) && !_sessions.ContainsKey(id))
            _ = ConnectAsync(device, manual: true);
    }

    public async Task ConnectAsync(DiscoveredDevice device, bool manual = false)
    {
        if (_disposed != 0 || device.Kind == DeviceKind.Unknown || _sessions.ContainsKey(device.Id)) return;

        var profile = _config.GetProfile(device.Id, device.Name);
        var session = new DeviceSession(device, profile, _waveforms, OnSessionClosed);
        if (!_sessions.TryAdd(device.Id, session))
        {
            session.Dispose();
            return;
        }
        device.State = LinkState.Connecting;
        device.Error = "";

        try
        {
            await session.ConnectAsync().ConfigureAwait(false);
            _config.Save();
        }
        catch (Exception ex)
        {
            _sessions.TryRemove(device.Id, out _);
            device.State = LinkState.Error;
            device.Error = ex.Message;
            session.Dispose();
            Plugin.LogSource.LogError($"连接 {device.Name} 失败：{ex}");
        }
    }

    public async Task DisconnectAsync(string deviceId)
    {
        if (_sessions.TryRemove(deviceId, out var session))
            await session.DisconnectAsync().ConfigureAwait(false);
    }

    public async Task EmergencyStopAsync()
    {
        var tasks = _sessions.Values.Select(x => x.EmergencyStopAsync()).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private void OnSessionClosed(DeviceSession session)
    {
        _sessions.TryRemove(session.Device.Id, out _);
    }

    public static DeviceKind Classify(string name, IEnumerable<Guid> advertisedServices)
    {
        name = name.Trim();
        if (name.Equals("YYC-DJ-V2", StringComparison.OrdinalIgnoreCase)) return DeviceKind.YcyEmsV2;
        if (name.Equals("YYC-DJ", StringComparison.OrdinalIgnoreCase)) return DeviceKind.YcyEmsV1;
        if (name.Equals("47L121000", StringComparison.OrdinalIgnoreCase)) return DeviceKind.CoyoteV3;
        if (advertisedServices.Contains(YcyToyService)) return DeviceKind.YcyToy;
        if (advertisedServices.Contains(YcyEmsService)) return name.Contains("V2", StringComparison.OrdinalIgnoreCase) ? DeviceKind.YcyEmsV2 : DeviceKind.YcyEmsV1;
        if (advertisedServices.Contains(CoyoteService)) return DeviceKind.CoyoteV3;
        return DeviceKind.Unknown;
    }

    private static string KindName(DeviceKind kind) => kind switch
    {
        DeviceKind.YcyEmsV1 => "YYC-DJ",
        DeviceKind.YcyEmsV2 => "YYC-DJ-V2",
        DeviceKind.CoyoteV3 => "47L121000",
        DeviceKind.YcyToy => "役次元玩具",
        _ => "未知设备"
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopScan();
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        _connectionScanAttempted.Clear();
    }
}
