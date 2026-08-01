using Il2CppInterop.Runtime.Attributes;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SLKDeviceLink;

public sealed class ModBehaviour : MonoBehaviour
{
    private const float WindowWidth = 930f;
    private const float WindowHeight = 680f;

    private ConfigStore _config = null!;
    private WaveformLibrary _waveforms = null!;
    private BleManager _ble = null!;
    private EventRouter _router = null!;
    private GameEventMonitor _events = null!;
    private Rect _windowRect;
    private int _tab;
    private int _devicePage;
    private int _eventPage;
    private int _eventDeviceIndex;
    private string _selectedEvent = "game.penalty";
    private string _notice = "";
    private float _noticeUntil;
    private bool _showWindow;
    private bool _styleReady;
    private CursorLockMode _oldCursorLock;
    private bool _oldCursorVisible;
    private bool _f8WasDown;
    private bool _f12WasDown;
    private bool _leftMouseWasDown;
    private bool _nativeClickPending;
    private Vector2 _nativeMousePosition;
    private float _nativeClickExpiresAt;
    private float _lastMouseCaptureAt = -10f;
    private readonly List<EventSystem> _blockedEventSystems = new();
    private float _nextEventSystemRefresh;
    private bool _restoreInputWhenMouseReleased;

    public ModBehaviour(IntPtr pointer) : base(pointer) { }

    private void Awake()
    {
        _windowRect = new Rect(Math.Max(20, Screen.width / 2f - WindowWidth / 2f), Math.Max(20, Screen.height / 2f - WindowHeight / 2f), WindowWidth, WindowHeight);
        _config = new ConfigStore();
        _waveforms = new WaveformLibrary();
        _waveforms.Load();
        _ble = new BleManager(_waveforms, _config);
        _router = new EventRouter(_ble, _config);
        _events = new GameEventMonitor(_router, _config.Settings);
    }

    private void Update()
    {
        if (_showWindow && Time.realtimeSinceStartup >= _nextEventSystemRefresh)
        {
            BlockGameMouseInput();
            _nextEventSystemRefresh = Time.realtimeSinceStartup + .5f;
        }
        else if (!_showWindow && _restoreInputWhenMouseReleased && !IsKeyDown(0x01))
        {
            RestoreGameMouseInput();
        }

        if (_nativeClickPending && Time.realtimeSinceStartup > _nativeClickExpiresAt)
            _nativeClickPending = false;
        var foreground = IsGameForeground();
        var f8Down = foreground && IsKeyDown(0x77);
        var f12Down = foreground && IsKeyDown(0x7B);
        var leftMouseDown = foreground && IsKeyDown(0x01);
        var unityF8 = false;
        var unityF12 = false;
        var unityMouseDown = false;
        var unityMousePosition = Vector2.zero;
        try
        {
            unityF8 = Input.GetKeyDown(KeyCode.F8);
            unityF12 = Input.GetKeyDown(KeyCode.F12);
            unityMouseDown = Input.GetMouseButtonDown(0);
            var mouse = Input.mousePosition;
            unityMousePosition = new Vector2(mouse.x, Screen.height - mouse.y);
        }
        catch
        {
            // Win32 polling above remains available if the legacy Unity input API is disabled.
        }

        if (unityF8 || (f8Down && !_f8WasDown)) ToggleWindow();
        if (unityF12 || (f12Down && !_f12WasDown)) EmergencyStop();
        _f8WasDown = f8Down;
        _f12WasDown = f12Down;
        var captureUnityClick = unityMouseDown && !_leftMouseWasDown;
        var captureWindowsClick = leftMouseDown && !_leftMouseWasDown;
        if (captureUnityClick && Time.realtimeSinceStartup - _lastMouseCaptureAt > .2f)
        {
            _nativeMousePosition = unityMousePosition;
            _nativeClickPending = true;
            _nativeClickExpiresAt = Time.realtimeSinceStartup + .5f;
            _lastMouseCaptureAt = Time.realtimeSinceStartup;
            Plugin.LogSource.LogDebug($"UI mouse click captured at ({unityMousePosition.x:0},{unityMousePosition.y:0}) via Unity input");
        }
        else if (captureWindowsClick && Time.realtimeSinceStartup - _lastMouseCaptureAt > .2f && TryGetGameMousePosition(out var mousePosition))
        {
            _nativeMousePosition = mousePosition;
            _nativeClickPending = true;
            _nativeClickExpiresAt = Time.realtimeSinceStartup + .5f;
            _lastMouseCaptureAt = Time.realtimeSinceStartup;
            Plugin.LogSource.LogDebug($"UI mouse click captured at ({mousePosition.x:0},{mousePosition.y:0}) via Windows input");
        }
        _leftMouseWasDown = leftMouseDown;
        _events.Tick(Time.realtimeSinceStartup);
    }

    private void OnGUI()
    {
        EnsureStyle();
        if (!_showWindow) DrawStatusBar();
        if (_showWindow)
        {
            _windowRect.x = Math.Clamp(_windowRect.x, 0f, Math.Max(0f, Screen.width - _windowRect.width));
            _windowRect.y = Math.Clamp(_windowRect.y, 0f, Math.Max(0f, Screen.height - _windowRect.height));
            _windowRect = GUI.Window(984213, _windowRect, (GUI.WindowFunction)DrawMainWindow, "SLK 设备联动");
            if (Event.current is { } current && (current.isMouse || current.type == EventType.ScrollWheel))
                current.Use();
        }
    }

    private void DrawStatusBar()
    {
        var sessions = _ble.Sessions;
        var connected = sessions.Count(x => x.Device.State == LinkState.Connected);
        var running = sessions.Count(x => x.HasActiveOutput);
        const float width = 350f;
        var rect = new Rect(Math.Max(0, Screen.width - width - 10), 10, width, 36);
        GUI.Box(rect, string.Empty);
        GUI.Label(new Rect(rect.x + 10, rect.y + 7, 242, 24),
            _router.Enabled ? $"设备 {connected}  输出 {running}" : "已急停（F12；F8 打开后可恢复）");
        if (ScreenButton(new Rect(rect.xMax - 94, rect.y + 4, 88, 28), _showWindow ? "关闭" : "配置 F8")) ToggleWindow();
    }

    private void DrawMainWindow(int id)
    {
        var dirty = false;

        if (Button(12, 24, 125, 32, _tab == 0 ? "● 设备连接" : "设备连接")) SwitchTab(0, "设备连接");
        if (Button(143, 24, 125, 32, _tab == 1 ? "● 事件配置" : "事件配置")) SwitchTab(1, "事件配置");
        if (Button(274, 24, 125, 32, _tab == 2 ? "● 全局设置" : "全局设置")) SwitchTab(2, "全局设置");

        var connected = _ble.Sessions.Count(x => x.Device.State == LinkState.Connected);
        Label(420, 31, 190, 24, $"已连接设备：{connected}");
        if (Button(660, 24, 142, 32, _router.Enabled ? "紧急停止 F12" : "恢复设备联动"))
        {
            if (_router.Enabled) EmergencyStop();
            else
            {
                _router.Enabled = true;
                SetNotice("设备联动已恢复；持续状态会在下一次轮询恢复输出");
            }
        }
        if (Button(810, 24, 106, 32, "关闭 F8")) ToggleWindow();

        Box(10, 64, 906, 552, "");
        switch (_tab)
        {
            case 0: dirty = DrawDevices(); break;
            case 1: dirty = DrawEvents(); break;
            case 2: dirty = DrawGlobalSettings(); break;
        }

        Label(16, 622, 898, 22, $"游戏状态：{_events.CurrentSummary}");
        var footer = Time.realtimeSinceStartup < _noticeUntil && !string.IsNullOrEmpty(_notice)
            ? _notice
            : $"最近事件：{_router.LastEventSummary}";
        Box(10, 646, 906, 25, "");
        Label(18, 650, 890, 20, footer);
        if (dirty) _config.Save();
    }

    private bool DrawDevices()
    {
        var dirty = false;
        if (Button(20, 76, 128, 32, "连接设备")) ConnectDiscoveredDevices();
        if (_ble.IsScanning && Button(156, 76, 128, 32, "停止扫描"))
        {
            _ble.StopScan();
            SetNotice("本次蓝牙扫描已手动停止");
        }
        else if (!_ble.IsScanning) Label(164, 82, 120, 24, "每次扫描 10 秒");
        if (Button(292, 76, 145, 32, "重新加载波形"))
        {
            _waveforms.Load();
            SetNotice($"已加载 {_waveforms.All.Count} 个波形");
        }
        Label(450, 82, 450, 24, $"扫描：{(_ble.IsScanning ? $"剩余 {_ble.ScanSecondsRemaining} 秒" : "未运行")}　发现：{_ble.Discovered.Count}　连接：{_ble.Sessions.Count}");

        var devices = _ble.Discovered;
        const int pageSize = 3;
        var pages = Math.Max(1, (devices.Count + pageSize - 1) / pageSize);
        _devicePage = Math.Clamp(_devicePage, 0, pages - 1);
        var visible = devices.Skip(_devicePage * pageSize).Take(pageSize).ToList();

        if (visible.Count == 0)
        {
            Box(20, 122, 886, 110, "");
            Label(36, 142, 850, 28, _ble.IsScanning ? $"正在扫描并连接蓝牙设备，剩余约 {_ble.ScanSecondsRemaining} 秒……" : "尚未发现支持的设备，请点击“连接设备”开始一次 10 秒扫描。");
            Label(36, 176, 850, 28, "支持名称：YYC-DJ、YYC-DJ-V2、47L121000；也会按 FF30、FF40、180C 服务 UUID 识别。");
        }

        for (var index = 0; index < visible.Count; index++)
        {
            var device = visible[index];
            var session = _ble.Sessions.FirstOrDefault(x => x.Device.Id == device.Id);
            var y = 116f + index * 148f;
            Box(20, y, 886, 140, "");
            Label(32, y + 8, 590, 24, Shorten($"{device.Name}　{device.KindLabel}　[{device.Id}]　RSSI {device.Rssi} dBm", 68));
            Label(628, y + 8, 96, 24, StateLabel(device.State));

            if (session is null)
            {
                if (device.State != LinkState.Connecting && Button(730, y + 5, 78, 28, "连接"))
                {
                    _ = _ble.ConnectAsync(device, manual: true);
                    SetNotice($"正在连接 {device.Name}");
                }
            }
            else if (Button(730, y + 5, 78, 28, "断开"))
            {
                _ = _ble.DisconnectAsync(device.Id);
                SetNotice($"正在断开 {device.Name}");
            }
            if (Button(816, y + 5, 78, 28, "重试"))
            {
                _ = ReconnectAsync(device, session is not null);
                SetNotice($"正在重新连接 {device.Name}");
            }

            var profile = _config.GetProfile(device.Id, device.Name);
            Label(32, y + 44, 190, 22, "本次扫描已发现");
            if (session is not null)
            {
                Label(235, y + 44, 650, 22,
                    $"输出：{(session.HasActiveOutput ? "运行中" : "停止")}　A={session.ActualIntensityA} B={session.ActualIntensityB}　电量={(session.BatteryPercent < 0 ? "--" : session.BatteryPercent + "%")}");
                if (device.Kind == DeviceKind.YcyToy)
                    Label(235, y + 68, 650, 22, $"马达模式数：A {session.ToyMotorCountA} / B {session.ToyMotorCountB} / C {session.ToyMotorCountC}");
            }

            if (device.Kind == DeviceKind.CoyoteV3)
            {
                dirty |= IntStepper("A 软上限", ref profile.CoyoteSoftMaxA, 0, 200, 10, 32, y + 72);
                dirty |= IntStepper("B 软上限", ref profile.CoyoteSoftMaxB, 0, 200, 10, 322, y + 72);
                if (session is not null && Button(744, y + 71, 142, 27, "立即写入 BF")) _ = session.ApplySafetyProfileAsync();
                dirty |= IntStepper("A 频率平衡", ref profile.CoyoteFrequencyBalanceA, 0, 255, 10, 32, y + 104);
                dirty |= IntStepper("B 频率平衡", ref profile.CoyoteFrequencyBalanceB, 0, 255, 10, 322, y + 104);
                dirty |= IntStepper("A 强度平衡", ref profile.CoyoteStrengthBalanceA, 0, 255, 10, 612, y + 104);
            }
            else if (!string.IsNullOrEmpty(device.Error))
            {
                Label(32, y + 82, 850, 44, "错误：" + Shorten(device.Error, 92));
            }
        }

        DrawPager(20, 572, 886, _devicePage, pages, value => _devicePage = value);
        return dirty;
    }

    private bool DrawEvents()
    {
        var dirty = false;
        var definitions = EventCatalog.All.ToList();
        const int pageSize = 14;
        var pages = Math.Max(1, (definitions.Count + pageSize - 1) / pageSize);
        _eventPage = Math.Clamp(_eventPage, 0, pages - 1);

        Label(20, 76, 278, 24, $"游戏事件（{definitions.Count} 项）");
        var visible = definitions.Skip(_eventPage * pageSize).Take(pageSize).ToList();
        for (var index = 0; index < visible.Count; index++)
        {
            var definition = visible[index];
            var prefix = string.Equals(_selectedEvent, definition.Key, StringComparison.OrdinalIgnoreCase) ? "● " : "";
            if (Button(20, 104 + index * 31, 278, 27, Shorten(prefix + $"[{definition.Category}] {definition.Title}", 28)))
                _selectedEvent = definition.Key;
        }
        DrawPager(20, 548, 278, _eventPage, pages, value => _eventPage = value);

        Box(310, 76, 596, 510, "");
        var selected = EventCatalog.Find(_selectedEvent);
        Label(324, 88, 566, 26, selected?.Title ?? _selectedEvent);
        Label(324, 116, 566, 24, selected?.Stateful == true
            ? "持续事件：状态结束时只撤销该事件的输出。"
            : "触发事件：按配置持续时间输出；未选中的设备不会响应。");

        var sessions = _ble.Sessions.Where(x => x.Device.State == LinkState.Connected).ToList();
        if (sessions.Count == 0)
        {
            Label(324, 158, 560, 28, "尚无已连接设备。请先到“设备连接”页点击“连接设备”，连接后即可为事件选择一个或多个设备。");
            return false;
        }

        _eventDeviceIndex = Math.Clamp(_eventDeviceIndex, 0, sessions.Count - 1);
        var session = sessions[_eventDeviceIndex];
        if (Button(324, 148, 42, 28, "<")) _eventDeviceIndex = (_eventDeviceIndex - 1 + sessions.Count) % sessions.Count;
        Box(372, 148, 382, 28, "");
        Label(382, 153, 362, 22, $"设备 {_eventDeviceIndex + 1}/{sessions.Count}：{Shorten(session.Device.Name + " / " + session.Device.KindLabel, 34)}");
        if (Button(760, 148, 42, 28, ">")) _eventDeviceIndex = (_eventDeviceIndex + 1) % sessions.Count;

        session = sessions[_eventDeviceIndex];
        var bindings = _config.Settings.GetBindings(_selectedEvent);
        var selectedDevice = bindings.TryGetValue(session.Device.Id, out var action);
        if (Button(324, 184, 230, 30, selectedDevice ? "✓ 使用该设备" : "□ 使用该设备"))
        {
            dirty = true;
            if (selectedDevice)
            {
                bindings.Remove(session.Device.Id);
                session.RemoveEventRequests(_selectedEvent);
                action = null;
                Plugin.LogSource.LogInfo($"Event binding removed: {_selectedEvent} -> {session.Device.Name}");
            }
            else
            {
                action = DefaultAction(session.Device.Kind);
                bindings[session.Device.Id] = action;
                Plugin.LogSource.LogInfo($"Event binding added: {_selectedEvent} -> {session.Device.Name}");
            }
        }

        var selectedCount = bindings.Keys.Count(id => sessions.Any(s => string.Equals(s.Device.Id, id, StringComparison.OrdinalIgnoreCase)));
        Label(566, 190, 322, 22, $"当前事件已选择 {selectedCount} 个已连接设备");
        if (action is not null)
            dirty |= DrawAction(session, action, selected?.Stateful == true, 324, 224, 566);
        else
            Label(324, 236, 560, 26, "勾选“使用该设备”后，会显示该设备独立的强度、时间、通道/马达和波形设置。");

        return dirty;
    }

    [HideFromIl2Cpp]
    private bool DrawAction(DeviceSession session, DeviceActionConfig action, bool stateful, float x, float y, float width)
    {
        var dirty = action.NormalizeForDevice(session.Device.Kind);
        dirty |= ToggleButton(x, y, 150, "启用输出", ref action.Enabled);
        var max = DeviceLimits.IntensityMax(session.Device.Kind);
        dirty |= IntStepper($"基础强度 0-{max}", ref action.BaseIntensity, 0, max, 10, x + 166, y);
        y += 36;

        if (EventCatalog.HasVariableIntensity(_selectedEvent, session.Device.Kind))
        {
            dirty |= IntStepper($"变化上限 0-{max}", ref action.MaxIntensity, 0, max, 10, x, y);
            y += 36;
        }

        if (_selectedEvent == "game.penalty")
        {
            dirty |= IntStepper("每次惩罚叠加强度", ref action.IntensityPerPenalty, 0, max, 5, x, y);
            y += 36;
        }
        if (!stateful)
        {
            dirty |= FloatStepper("持续时间（秒）", ref action.DurationSeconds, .1f, 600f, 1f, x, y);
            if (_selectedEvent == "game.penalty")
                dirty |= FloatStepper("每次惩罚叠加时间", ref action.DurationPerPenalty, 0f, 600f, 1f, x + 290, y);
            y += 36;
        }

        var numericValueLabel = EventCatalog.Find(_selectedEvent)?.NumericValueLabel ?? "";
        if (!string.IsNullOrWhiteSpace(numericValueLabel))
        {
            dirty |= ToggleButton(x, y, 260, $"按{numericValueLabel}增加强度", ref action.UseEventValue);
            if (action.UseEventValue)
                dirty |= FloatStepper("数值倍率", ref action.EventValueMultiplier, 0f, 20f, .1f, x + 270, y);
            y += 36;
        }

        if (session.Device.Kind == DeviceKind.YcyToy)
        {
            dirty |= ToggleButton(x, y, 120, "马达 A", ref action.MotorA);
            dirty |= ToggleButton(x + 126, y, 120, "马达 B", ref action.MotorB);
            dirty |= ToggleButton(x + 252, y, 120, "马达 C", ref action.MotorC);
            y += 36;
            if (_selectedEvent == "item.any.active")
            {
                dirty |= ToggleButton(x, y, 250, "按游戏道具挡位调强度", ref action.UseGameItemLevel);
                if (action.UseGameItemLevel)
                    dirty |= IntStepper("每挡增加", ref action.IntensityPerGameLevel, 0, 20, 2, x + 265, y);
                y += 36;
                if (Button(x, y, 250, 28, action.ToyAggregation == ToyAggregationMode.Maximum ? "多道具合成：取最高挡" : "多道具合成：相加并受事件上限"))
                {
                    action.ToyAggregation = action.ToyAggregation == ToyAggregationMode.Maximum ? ToyAggregationMode.SumClamped : ToyAggregationMode.Maximum;
                    dirty = true;
                }
                y += 36;
            }
        }
        else
        {
            dirty |= ToggleButton(x, y, 120, "通道 A", ref action.ChannelA);
            dirty |= ToggleButton(x + 126, y, 120, "通道 B", ref action.ChannelB);
            y += 36;
        }

        dirty |= DrawWaveformPicker(session.Device.Kind, action, x, y, width);
        y += 38;
        if (Button(x + width - 134, y, 134, 30, "测试此设备配置"))
        {
            _router.Test(session, action);
            SetNotice($"正在测试 {session.Device.Name}（最长 10 秒）");
        }
        return dirty;
    }

    [HideFromIl2Cpp]
    private bool DrawWaveformPicker(DeviceKind kind, DeviceActionConfig action, float x, float y, float width)
    {
        var list = _waveforms.For(kind).ToList();
        if (list.Count == 0)
        {
            Label(x, y, width, 28, "未找到适用于该设备的波形");
            return false;
        }

        var index = list.FindIndex(item => string.Equals(item.Id, action.WaveformId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            index = 0;
            action.WaveformId = list[0].Id;
        }
        var dirty = false;
        Label(x, y + 4, 88, 24, kind == DeviceKind.YcyToy ? "振动波形" : "电击波形");
        if (Button(x + 92, y, 42, 28, "<"))
        {
            index = (index - 1 + list.Count) % list.Count;
            dirty = true;
        }
        Box(x + 140, y, width - 232, 28, "");
        Label(x + 150, y + 4, width - 252, 22, $"{Shorten(list[index].Name, 30)}　({index + 1}/{list.Count})");
        if (Button(x + width - 86, y, 42, 28, ">"))
        {
            index = (index + 1) % list.Count;
            dirty = true;
        }
        if (dirty) action.WaveformId = list[index].Id;
        return dirty;
    }

    private bool DrawGlobalSettings()
    {
        var dirty = false;
        var settings = _config.Settings;
        Label(24, 80, 860, 26, "连接与安全");
        Box(24, 112, 868, 36, "");
        Label(36, 120, 840, 24, "游戏启动时不扫描。每次点击“连接设备”才扫描，并在 10 秒后自动停止；同一设备每次只尝试连接一次。");

        Label(24, 158, 860, 26, "状态阈值");
        dirty |= FloatStepper("心率中阈值", ref settings.HeartMediumThreshold, 0f, 1f, .05f, 24, 190);
        dirty |= FloatStepper("心率高阈值", ref settings.HeartHighThreshold, 0f, 1f, .05f, 314, 190);
        dirty |= IntStepper("警戒中阈值", ref settings.RiskMediumThreshold, 0, 100, 5, 24, 228);
        dirty |= IntStepper("警戒高阈值", ref settings.RiskHighThreshold, 0, 100, 5, 314, 228);
        dirty |= FloatStepper("精神过低阈值", ref settings.MentalLowThreshold, 0f, 1f, .05f, 24, 266);

        dirty |= settings.NormalizeGlobalSettings();

        Box(24, 304, 868, 92, "");
        Label(36, 315, 840, 24, "飞机杯/跳蛋会合成所有仍处于开启状态的游戏道具。关闭其中一个道具不会停止其他仍开启道具的输出。");
        Label(36, 343, 840, 24, "役次元电击器 0–180；郊狼 3.0 0–200；役次元飞机杯/跳蛋 0–20；动态事件另有独立上限。");
        Label(36, 371, 840, 24, "事件配置按设备独立保存；持续事件结束时只撤销对应事件和来源，不影响同设备上的其他事件。");

        Label(24, 406, 860, 26, "事件监控诊断（最新在上）");
        var recent = _router.RecentEvents.Take(6).ToList();
        if (recent.Count == 0)
            Label(36, 440, 840, 24, "尚未捕获游戏事件；进入旅程后会显示生命、心率、露出、警戒、道具、任务和 NPC 状态变化。");
        for (var index = 0; index < recent.Count; index++)
            Label(36, 436 + index * 25, 840, 22, Shorten(recent[index], 100));
        return dirty;
    }

    private void ConnectDiscoveredDevices()
    {
        var started = _ble.StartConnectionScan(TimeSpan.FromSeconds(10));
        _devicePage = 0;
        SetNotice(started
            ? "已开始 10 秒蓝牙扫描；发现支持设备后会立即尝试连接一次"
            : "蓝牙扫描启动失败：" + _ble.LastScanError);
    }

    [HideFromIl2Cpp]
    private async Task ReconnectAsync(DiscoveredDevice device, bool disconnectFirst)
    {
        if (disconnectFirst)
        {
            await _ble.DisconnectAsync(device.Id).ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);
        }
        await _ble.ConnectAsync(device, manual: true).ConfigureAwait(false);
    }

    [HideFromIl2Cpp]
    private DeviceActionConfig DefaultAction(DeviceKind kind)
    {
        var waveform = _waveforms.For(kind).FirstOrDefault();
        return new DeviceActionConfig
        {
            BaseIntensity = kind == DeviceKind.YcyToy ? 5 : 10,
            MaxIntensity = DeviceLimits.IntensityMax(kind),
            WaveformId = waveform?.Id ?? "",
            ChannelA = true,
            ChannelB = false,
            DurationSeconds = 3f
        };
    }

    private void EmergencyStop()
    {
        _router.Enabled = false;
        _ = _ble.EmergencyStopAsync();
        SetNotice("已向全部已连接设备发送紧急停止");
    }

    private void ToggleWindow()
    {
        _showWindow = !_showWindow;
        Plugin.LogSource.LogInfo($"F8 configuration window: {(_showWindow ? "open" : "closed")}");
        if (_showWindow)
        {
            _oldCursorLock = Cursor.lockState;
            _oldCursorVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _restoreInputWhenMouseReleased = false;
            BlockGameMouseInput();
        }
        else
        {
            Cursor.lockState = _oldCursorLock;
            Cursor.visible = _oldCursorVisible;
            // Keep blocking until the click that closed the window has been released;
            // otherwise its mouse-up can land on the game UI underneath.
            _restoreInputWhenMouseReleased = true;
        }
    }

    [HideFromIl2Cpp]
    private void BlockGameMouseInput()
    {
        InputBlockState.Active = true;
        try
        {
            foreach (var eventSystem in UnityEngine.Object.FindObjectsOfType<EventSystem>(true))
            {
                var known = _blockedEventSystems.Any(item => item == eventSystem);
                if (known)
                {
                    eventSystem.enabled = false;
                    continue;
                }
                if (!eventSystem.enabled) continue;
                _blockedEventSystems.Add(eventSystem);
                eventSystem.enabled = false;
            }
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogDebug($"阻止游戏 UI 鼠标输入时遇到暂时性错误：{ex.Message}");
        }
    }

    [HideFromIl2Cpp]
    private void RestoreGameMouseInput()
    {
        _restoreInputWhenMouseReleased = false;
        InputBlockState.Active = false;
        foreach (var eventSystem in _blockedEventSystems.ToArray())
        {
            try
            {
                if (eventSystem is not null) eventSystem.enabled = true;
            }
            catch
            {
                // A scene change may have destroyed the old EventSystem.
            }
        }
        _blockedEventSystems.Clear();
    }

    private void EnsureStyle()
    {
        if (_styleReady) return;
        _styleReady = true;
        try
        {
            // The game's built-in IMGUI font already supports its Chinese UI. Creating a
            // dynamic OS font calls a constructor stripped from this IL2CPP build.
            GUI.skin.label.fontSize = 15;
            GUI.skin.button.fontSize = 15;
            GUI.skin.window.fontSize = 17;
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogWarning($"UI 样式初始化失败，将使用游戏默认样式：{ex.Message}");
        }
    }

    private void SetNotice(string message)
    {
        _notice = message;
        _noticeUntil = Time.realtimeSinceStartup + 6f;
    }

    private void SwitchTab(int tab, string label)
    {
        if (_tab == tab) return;
        _tab = tab;
        SetNotice($"已切换到“{label}”");
        Plugin.LogSource.LogInfo($"UI tab selected: {label}");
    }

    [HideFromIl2Cpp]
    private void DrawPager(float x, float y, float width, int page, int pages, Action<int> setPage)
    {
        if (pages <= 1)
        {
            Label(x, y + 4, width, 22, "第 1 / 1 页");
            return;
        }
        if (Button(x, y, 88, 28, "上一页")) setPage(Math.Max(0, page - 1));
        Box(x + 96, y, width - 192, 28, "");
        Label(x + 106, y + 4, width - 212, 22, $"第 {page + 1} / {pages} 页");
        if (Button(x + width - 88, y, 88, 28, "下一页")) setPage(Math.Min(pages - 1, page + 1));
    }

    private bool ToggleButton(float x, float y, float width, string label, ref bool value)
    {
        if (!Button(x, y, width, 28, (value ? "✓ " : "□ ") + label)) return false;
        value = !value;
        return true;
    }

    private bool IntStepper(string label, ref int value, int min, int max, int coarse, float x, float y)
    {
        Label(x, y + 4, 108, 22, label);
        var next = value;
        if (Button(x + 110, y, 32, 28, $"-{coarse}")) next -= coarse;
        if (Button(x + 144, y, 26, 28, "-")) next--;
        Box(x + 172, y, 48, 28, "");
        Label(x + 178, y + 4, 38, 22, value.ToString());
        if (Button(x + 222, y, 26, 28, "+")) next++;
        if (Button(x + 250, y, 32, 28, $"+{coarse}")) next += coarse;
        next = Math.Clamp(next, min, max);
        if (next == value) return false;
        value = next;
        return true;
    }

    private bool FloatStepper(string label, ref float value, float min, float max, float step, float x, float y)
    {
        Label(x, y + 4, 118, 22, label);
        var next = value;
        if (Button(x + 120, y, 34, 28, "-")) next -= step;
        Box(x + 158, y, 58, 28, "");
        Label(x + 164, y + 4, 48, 22, value.ToString("0.##"));
        if (Button(x + 220, y, 34, 28, "+")) next += step;
        next = Math.Clamp(next, min, max);
        next = MathF.Round(next, 2);
        if (Math.Abs(next - value) < .0001f) return false;
        value = next;
        return true;
    }

    private static void Box(float x, float y, float width, float height, string text)
        => GUI.Box(new Rect(x, y, width, height), text);

    private static void Label(float x, float y, float width, float height, string text)
        => GUI.Label(new Rect(x, y, width, height), text);

    private bool Button(float x, float y, float width, float height, string text)
    {
        GUI.Button(new Rect(x, y, width, height), text); // draw only; this IL2CPP build reports clicks inconsistently
        var clickedNatively = ConsumeNativeClick(new Rect(_windowRect.x + x, _windowRect.y + y, width, height));
        return clickedNatively;
    }

    private bool ScreenButton(Rect rect, string text)
    {
        GUI.Button(rect, text); // draw only; click handling is unified below
        var clickedNatively = ConsumeNativeClick(rect);
        return clickedNatively;
    }

    private bool ConsumeNativeClick(Rect rect)
    {
        if (!_nativeClickPending) return false;
        var x = _nativeMousePosition.x;
        var y = _nativeMousePosition.y;
        if (x < rect.x || x > rect.xMax || y < rect.y || y > rect.yMax) return false;
        _nativeClickPending = false;
        Plugin.LogSource.LogDebug($"Native UI click at ({x:0},{y:0})");
        return true;
    }

    private static string Shorten(string value, int length)
        => value.Length <= length ? value : value[..Math.Max(1, length - 1)] + "…";

    private static string StateLabel(LinkState state) => state switch
    {
        LinkState.Connecting => "连接中",
        LinkState.Connected => "已连接",
        LinkState.Disconnecting => "断开中",
        LinkState.Disconnected => "已断开",
        LinkState.Error => "连接失败",
        _ => "已发现"
    };

    [HideFromIl2Cpp]
    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [HideFromIl2Cpp]
    private static bool IsGameForeground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    [HideFromIl2Cpp]
    private static bool TryGetGameMousePosition(out Vector2 position)
    {
        position = default;
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out var processId);
        if (processId != (uint)Environment.ProcessId || !GetCursorPos(out var point) || !ScreenToClient(window, ref point) || !GetClientRect(window, out var client))
            return false;
        var clientWidth = client.Right - client.Left;
        var clientHeight = client.Bottom - client.Top;
        if (clientWidth <= 0 || clientHeight <= 0 || point.X < 0 || point.Y < 0 || point.X >= clientWidth || point.Y >= clientHeight)
            return false;
        position = new Vector2(point.X * (float)Screen.width / clientWidth, point.Y * (float)Screen.height / clientHeight);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [HideFromIl2Cpp]
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [HideFromIl2Cpp]
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [HideFromIl2Cpp]
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [HideFromIl2Cpp]
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [HideFromIl2Cpp]
    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);

    [HideFromIl2Cpp]
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    private void OnDestroy()
    {
        RestoreGameMouseInput();
        try { _ble.EmergencyStopAsync().Wait(TimeSpan.FromSeconds(2)); } catch { /* ignored */ }
        _config.Save();
        _ble.Dispose();
    }
}
