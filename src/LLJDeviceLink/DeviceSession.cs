using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace LLJDeviceLink;

public sealed class DeviceSession : IDisposable
{
    private static readonly Guid YcyEmsWrite = Guid.Parse("0000ff31-0000-1000-8000-00805f9b34fb");
    private static readonly Guid YcyEmsNotify = Guid.Parse("0000ff32-0000-1000-8000-00805f9b34fb");
    private static readonly Guid YcyToyWrite = Guid.Parse("0000ff41-0000-1000-8000-00805f9b34fb");
    private static readonly Guid YcyToyNotify = Guid.Parse("0000ff42-0000-1000-8000-00805f9b34fb");
    private static readonly Guid CoyoteWrite = Guid.Parse("0000150a-0000-1000-8000-00805f9b34fb");
    private static readonly Guid CoyoteNotify = Guid.Parse("0000150b-0000-1000-8000-00805f9b34fb");

    private readonly object _requestLock = new();
    private readonly Dictionary<string, OutputRequest> _requests = new(StringComparer.OrdinalIgnoreCase);
    private readonly DeviceProfile _profile;
    private readonly WaveformLibrary _waveforms;
    private readonly Action<DeviceSession> _onClosed;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private BluetoothLEDevice? _bleDevice;
    private GattDeviceService? _service;
    private GattCharacteristic? _write;
    private GattCharacteristic? _notify;
    private Task? _writerTask;
    private int _frameIndex;
    private int _sequence = 1;
    private int _pendingCoyoteSequence;
    private DateTimeOffset _pendingCoyoteSince;
    private bool _lastHadOutput;
    private int _disposed;

    public DeviceSession(DiscoveredDevice device, DeviceProfile profile, WaveformLibrary waveforms, Action<DeviceSession> onClosed)
    {
        Device = device;
        _profile = profile;
        _waveforms = waveforms;
        _onClosed = onClosed;
    }

    public DiscoveredDevice Device { get; }
    public int ActualIntensityA { get; private set; }
    public int ActualIntensityB { get; private set; }
    public int BatteryPercent { get; private set; } = -1;
    public int ToyMotorCountA { get; private set; }
    public int ToyMotorCountB { get; private set; }
    public int ToyMotorCountC { get; private set; }
    public bool HasActiveOutput
    {
        get
        {
            lock (_requestLock)
                return _requests.Values.Any(request => request.Config.Enabled && TargetsOutput(request.Config) &&
                    (request.ExpiresAt is null || request.ExpiresAt > DateTimeOffset.UtcNow));
        }
    }
    public string LastError { get; private set; } = "";

    public async Task ConnectAsync()
    {
        _bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(Device.Address).AsTask(_cts.Token).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Windows 无法打开该 BLE 设备；请确认设备已开机且未被其他应用占用");
        _bleDevice.ConnectionStatusChanged += OnConnectionStatusChanged;
        if (!string.IsNullOrWhiteSpace(_bleDevice.Name)) Device.Name = _bleDevice.Name.Trim();

        var (serviceId, writeId, notifyId) = Device.Kind switch
        {
            DeviceKind.CoyoteV3 => (BleManager.CoyoteService, CoyoteWrite, CoyoteNotify),
            DeviceKind.YcyToy => (BleManager.YcyToyService, YcyToyWrite, YcyToyNotify),
            _ => (BleManager.YcyEmsService, YcyEmsWrite, YcyEmsNotify)
        };

        var services = await _bleDevice.GetGattServicesForUuidAsync(serviceId, BluetoothCacheMode.Uncached).AsTask(_cts.Token).ConfigureAwait(false);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            throw new InvalidOperationException($"找不到服务 {serviceId}（{services.Status}）");
        _service = services.Services[0];

        _write = await GetCharacteristicAsync(_service, writeId, required: true).ConfigureAwait(false);
        _notify = await GetCharacteristicAsync(_service, notifyId, required: false).ConfigureAwait(false);
        if (_notify is not null)
        {
            _notify.ValueChanged += OnNotify;
            var notifyStatus = await _notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(_cts.Token).ConfigureAwait(false);
            if (notifyStatus != GattCommunicationStatus.Success)
                Plugin.LogSource.LogWarning($"{Device.Name} 通知订阅失败：{notifyStatus}");
        }

        if (Device.Kind == DeviceKind.CoyoteV3)
            await ApplySafetyProfileAsync().ConfigureAwait(false);
        else if (Device.Kind == DeviceKind.YcyToy)
            await WriteAsync(ProtocolPackets.ToyQuery(), _cts.Token).ConfigureAwait(false);

        Device.State = LinkState.Connected;
        _writerTask = Task.Run(WriterLoopAsync);
        Plugin.LogSource.LogInfo($"已连接 {Device.KindLabel}：{Device.Name} [{Device.Id}]");
    }

    private async Task<GattCharacteristic?> GetCharacteristicAsync(GattDeviceService service, Guid id, bool required)
    {
        var result = await service.GetCharacteristicsForUuidAsync(id, BluetoothCacheMode.Uncached).AsTask(_cts.Token).ConfigureAwait(false);
        if (result.Status == GattCommunicationStatus.Success && result.Characteristics.Count > 0)
            return result.Characteristics[0];
        if (required) throw new InvalidOperationException($"找不到写入特性 {id}（{result.Status}）");
        return null;
    }

    public void SetRequest(OutputRequest request)
    {
        lock (_requestLock) _requests[request.RequestId] = request;
    }

    public void RemoveRequest(string requestId)
    {
        lock (_requestLock) _requests.Remove(requestId);
    }

    public void RemoveRequestsWithPrefix(string prefix)
    {
        lock (_requestLock)
        {
            foreach (var key in _requests.Keys.Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
                _requests.Remove(key);
        }
    }

    public void RemoveEventRequests(string eventKey)
    {
        lock (_requestLock)
        {
            var pulsePrefix = $"pulse|{eventKey}|";
            var statePrefix = $"state|{eventKey}|";
            foreach (var key in _requests.Keys.Where(key =>
                         key.StartsWith(pulsePrefix, StringComparison.OrdinalIgnoreCase) ||
                         key.StartsWith(statePrefix, StringComparison.OrdinalIgnoreCase)).ToArray())
                _requests.Remove(key);
        }
    }

    public async Task ApplySafetyProfileAsync()
    {
        if (Device.Kind == DeviceKind.CoyoteV3 && _write is not null)
            await WriteAsync(ProtocolPackets.CoyoteSoftLimit(_profile), _cts.Token).ConfigureAwait(false);
    }

    private async Task WriterLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                var packet = BuildOutputPacket();
                if (packet is not null) await WriteAsync(packet, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Device.State = LinkState.Error;
            Device.Error = ex.Message;
            Plugin.LogSource.LogError($"{Device.Name} 输出循环失败：{ex}");
        }
    }

    private byte[]? BuildOutputPacket()
    {
        List<OutputRequest> active;
        lock (_requestLock)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var expired in _requests.Where(x => x.Value.ExpiresAt is { } expiry && expiry <= now).Select(x => x.Key).ToArray())
                _requests.Remove(expired);
            active = _requests.Values.Where(x => x.Config.Enabled && TargetsOutput(x.Config)).ToList();
        }

        if (active.Count == 0)
        {
            if (!_lastHadOutput) return null;
            _lastHadOutput = false;
            return BuildStopPacket();
        }

        _lastHadOutput = true;
        return Device.Kind switch
        {
            DeviceKind.YcyEmsV1 => BuildYcyV1(active),
            DeviceKind.YcyEmsV2 => BuildYcyV2(active),
            DeviceKind.CoyoteV3 => BuildCoyote(active),
            DeviceKind.YcyToy => BuildToy(active),
            _ => null
        };
    }

    private byte[] BuildYcyV1(List<OutputRequest> active)
    {
        var a = active.Where(request => request.Config.ChannelA).OrderByDescending(request => request.Intensity(Device.Kind)).FirstOrDefault();
        var b = active.Where(request => request.Config.ChannelB).OrderByDescending(request => request.Intensity(Device.Kind)).FirstOrDefault();
        if (a is null && b is null) return ProtocolPackets.YcyV1Fixed(true, true, 0, 1);
        var sendA = a is not null;
        var sendB = b is not null;
        OutputRequest winner;
        if (a is not null && b is not null && !ReferenceEquals(a, b))
        {
            // V1 has one strength/mode field for the selected channel. Alternate
            // independent A/B winners so a stronger A request cannot starve B.
            var chooseA = (_frameIndex & 1) == 0;
            winner = chooseA ? a : b;
            sendA = chooseA;
            sendB = !chooseA;
        }
        else
        {
            winner = a ?? b!;
        }
        var intensity = winner.Intensity(Device.Kind);
        var waveform = _waveforms.Get(Device.Kind, winner.Config.WaveformId);
        if (waveform.FixedMode is { } mode)
        {
            _frameIndex++;
            return ProtocolPackets.YcyV1Fixed(sendA, sendB, intensity, mode);
        }
        var sample = waveform.YcySamples[_frameIndex++ % waveform.YcySamples.Count];
        return ProtocolPackets.YcyV1Realtime(sendA, sendB, intensity, sample.Frequency, sample.Pulse);
    }

    private byte[] BuildYcyV2(List<OutputRequest> active)
    {
        var a = active.Where(x => x.Config.ChannelA).OrderByDescending(x => x.Intensity(Device.Kind)).FirstOrDefault();
        var b = active.Where(x => x.Config.ChannelB).OrderByDescending(x => x.Intensity(Device.Kind)).FirstOrDefault();
        var wa = _waveforms.Get(Device.Kind, a?.Config.WaveformId);
        var wb = _waveforms.Get(Device.Kind, b?.Config.WaveformId);
        if (wa.FixedMode is { } modeA && wb.FixedMode is { } modeB)
            return ProtocolPackets.YcyV2Fixed(a?.Intensity(Device.Kind) ?? 0, modeA, b?.Intensity(Device.Kind) ?? 0, modeB);

        var sampleA = YcySample(wa, _frameIndex);
        var sampleB = YcySample(wb, _frameIndex++);
        return ProtocolPackets.YcyV2Realtime(a?.Intensity(Device.Kind) ?? 0, sampleA.Frequency, sampleA.Pulse, b?.Intensity(Device.Kind) ?? 0, sampleB.Frequency, sampleB.Pulse);
    }

    private static (byte Frequency, byte Pulse) YcySample(WaveformDefinition waveform, int index)
        => waveform.YcySamples.Count > 0 ? waveform.YcySamples[index % waveform.YcySamples.Count] : ((byte)10, (byte)30);

    private byte[] BuildCoyote(List<OutputRequest> active)
    {
        var a = active.Where(x => x.Config.ChannelA).OrderByDescending(x => x.Intensity(Device.Kind)).FirstOrDefault();
        var b = active.Where(x => x.Config.ChannelB).OrderByDescending(x => x.Intensity(Device.Kind)).FirstOrDefault();
        var frameA = CoyoteFrame(a, _frameIndex);
        var frameB = CoyoteFrame(b, _frameIndex++);
        var desiredA = Math.Min(a?.Intensity(Device.Kind) ?? 0, Math.Clamp(_profile.CoyoteSoftMaxA, 0, 200));
        var desiredB = Math.Min(b?.Intensity(Device.Kind) ?? 0, Math.Clamp(_profile.CoyoteSoftMaxB, 0, 200));
        if (_pendingCoyoteSequence != 0 && DateTimeOffset.UtcNow - _pendingCoyoteSince > TimeSpan.FromSeconds(1))
            _pendingCoyoteSequence = 0; // 通知丢失时允许重试，避免永远卡住。

        if (_pendingCoyoteSequence == 0 && (desiredA != ActualIntensityA || desiredB != ActualIntensityB))
        {
            var sequence = _sequence++;
            if (_sequence > 15) _sequence = 1;
            _pendingCoyoteSequence = sequence;
            _pendingCoyoteSince = DateTimeOffset.UtcNow;
            return ProtocolPackets.CoyoteFrame(sequence, desiredA, desiredB, frameA, frameB, true, true);
        }

        // 等待同序号 B1 时只发波形，不再次修改强度，符合协议的 100 ms 调度建议。
        return ProtocolPackets.CoyoteFrame(0, 0, 0, frameA, frameB, false, false);
    }

    private byte[] CoyoteFrame(OutputRequest? request, int index)
    {
        if (request is null) return new byte[] { 10, 10, 10, 10, 0, 0, 0, 101 }; // 协议要求用一个非法强度禁用该通道整组波形。
        var waveform = _waveforms.Get(Device.Kind, request.Config.WaveformId);
        return waveform.CoyoteFrames[index % waveform.CoyoteFrames.Count];
    }

    private byte[] BuildToy(List<OutputRequest> active)
    {
        int EventCombined(IEnumerable<OutputRequest> eventRequests, Func<DeviceActionConfig, bool> motor)
        {
            var requests = eventRequests.Where(x => motor(x.Config)).ToList();
            if (requests.Count == 0) return 0;
            var values = requests.Select(x => x.Intensity(Device.Kind)).ToList();
            var eventCap = requests.Min(x => x.Config.EffectiveMaxIntensity(Device.Kind));
            var combined = requests.Any(x => x.Config.ToyAggregation == ToyAggregationMode.SumClamped)
                ? values.Sum()
                : values.Max();
            return Math.Clamp(combined, 0, eventCap);
        }

        var groups = active.GroupBy(x => x.EventKey, StringComparer.OrdinalIgnoreCase).ToList();
        int Combined(Func<DeviceActionConfig, bool> motor)
            // Sum mode combines sources of the same event, then applies that
            // event's per-device cap. Different events compete by maximum.
            => groups.Max(group => EventCombined(group, motor));

        var winnerGroup = groups.OrderByDescending(group => Math.Max(
            EventCombined(group, x => x.MotorA),
            Math.Max(EventCombined(group, x => x.MotorB), EventCombined(group, x => x.MotorC)))).First();
        var winner = winnerGroup.OrderByDescending(x => x.Intensity(Device.Kind)).First();
        var waveform = _waveforms.Get(Device.Kind, winner.Config.WaveformId);
        var scale = waveform.ToyEnvelope[_frameIndex++ % waveform.ToyEnvelope.Count];
        return ProtocolPackets.ToyRate((int)MathF.Round(Combined(x => x.MotorA) * scale), (int)MathF.Round(Combined(x => x.MotorB) * scale), (int)MathF.Round(Combined(x => x.MotorC) * scale));
    }

    private bool TargetsOutput(DeviceActionConfig config) => Device.Kind switch
    {
        DeviceKind.YcyToy => config.MotorA || config.MotorB || config.MotorC,
        DeviceKind.YcyEmsV1 or DeviceKind.YcyEmsV2 or DeviceKind.CoyoteV3 => config.ChannelA || config.ChannelB,
        _ => false
    };

    private byte[] BuildStopPacket() => Device.Kind switch
    {
        DeviceKind.YcyEmsV1 => ProtocolPackets.YcyV1Fixed(true, true, 0, 1),
        DeviceKind.YcyEmsV2 => ProtocolPackets.YcyV2Fixed(0, 1, 0, 1),
        DeviceKind.CoyoteV3 => ProtocolPackets.CoyoteFrame(1, 0, 0, new byte[] { 10, 10, 10, 10, 0, 0, 0, 101 }, new byte[] { 10, 10, 10, 10, 0, 0, 0, 101 }),
        DeviceKind.YcyToy => ProtocolPackets.ToyRate(0, 0, 0),
        _ => Array.Empty<byte>()
    };

    private async Task WriteAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (_write is null || packet.Length == 0) return;
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var writer = new DataWriter();
            writer.WriteBytes(packet);
            var result = await _write.WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse).AsTask(cancellationToken).ConfigureAwait(false);
            if (result.Status != GattCommunicationStatus.Success)
                throw new IOException($"GATT 写入失败：{result.Status}/{result.ProtocolError}");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void OnNotify(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            using var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var bytes = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);
            if (Device.Kind == DeviceKind.CoyoteV3 && bytes.Length >= 4 && bytes[0] == 0xB1)
            {
                ActualIntensityA = bytes[2];
                ActualIntensityB = bytes[3];
                if (bytes[1] == _pendingCoyoteSequence) _pendingCoyoteSequence = 0;
            }
            else if ((Device.Kind is DeviceKind.YcyEmsV1 or DeviceKind.YcyEmsV2) &&
                     bytes.Length >= 9 && bytes[0] == 0x35 && bytes[1] == 0x71 && bytes[2] is 0x01 or 0x02)
            {
                var intensity = (bytes[5] << 8) | bytes[6];
                if (bytes[2] == 0x01) ActualIntensityA = intensity;
                else ActualIntensityB = intensity;
            }
            else if (Device.Kind == DeviceKind.YcyToy && bytes.Length >= 10 && bytes[0] == 0x35 && bytes[1] == 0x10)
            {
                ToyMotorCountA = bytes[4];
                ToyMotorCountB = bytes[5];
                ToyMotorCountC = bytes[6];
            }
            else if (bytes.Length >= 5 && bytes[0] == 0x35 && bytes[1] == 0x71 && bytes[2] == 0x04)
            {
                BatteryPercent = bytes[3];
            }
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogDebug($"解析 {Device.Name} 通知失败：{ex.Message}");
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected || _disposed != 0) return;
        Device.State = LinkState.Disconnected;
        _onClosed(this);
        Dispose();
    }

    public async Task EmergencyStopAsync()
    {
        lock (_requestLock) _requests.Clear();
        _lastHadOutput = false;
        try { await WriteAsync(BuildStopPacket(), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Plugin.LogSource.LogWarning($"{Device.Name} 急停写入失败：{ex.Message}"); }
    }

    public async Task DisconnectAsync()
    {
        Device.State = LinkState.Disconnecting;
        await EmergencyStopAsync().ConfigureAwait(false);
        Dispose();
        Device.State = LinkState.Disconnected;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        if (_notify is not null) _notify.ValueChanged -= OnNotify;
        if (_bleDevice is not null) _bleDevice.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _service?.Dispose();
        _bleDevice?.Dispose();
        _writeLock.Dispose();
        _cts.Dispose();
    }
}
