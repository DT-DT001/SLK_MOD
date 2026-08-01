using System.Collections.Concurrent;

namespace LLJDeviceLink;

public sealed class EventRouter
{
    private static readonly TimeSpan StateLease = TimeSpan.FromSeconds(1.2);
    private readonly BleManager _ble;
    private readonly ConfigStore _config;
    private readonly ConcurrentDictionary<string, bool> _observedStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _recentEvents = new();
    private string _lastEventSummary = "等待游戏事件";

    public EventRouter(BleManager ble, ConfigStore config)
    {
        _ble = ble;
        _config = config;
    }

    public bool Enabled { get; set; } = true;
    public string LastEventSummary => _lastEventSummary;
    public IReadOnlyList<string> RecentEvents => _recentEvents.Reverse().ToList();

    public void Pulse(string eventKey, EventContext context = default, string sourceId = "event")
    {
        Record(eventKey, sourceId, "触发", context);
        if (!Enabled) return;
        if (!_config.Settings.EventBindings.TryGetValue(eventKey, out var bindings)) return;
        foreach (var session in _ble.Sessions)
        {
            if (!bindings.TryGetValue(session.Device.Id, out var action) || !action.Enabled) continue;
            session.SetRequest(new OutputRequest
            {
                RequestId = $"pulse|{eventKey}|{sourceId}",
                EventKey = eventKey,
                Config = action.Copy(),
                Context = context,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(action.EffectiveDuration(context))
            });
        }
    }

    public void SetState(string eventKey, string sourceId, bool active, EventContext context = default)
    {
        var requestId = $"state|{eventKey}|{sourceId}";
        var hadPrevious = _observedStates.TryGetValue(requestId, out var previous);
        _observedStates[requestId] = active;
        if ((!hadPrevious && active) || (hadPrevious && previous != active))
            Record(eventKey, sourceId, active ? "开始" : "结束", context);

        foreach (var session in _ble.Sessions)
        {
            if (!active)
            {
                session.RemoveRequest(requestId);
                continue;
            }

            if (!Enabled) continue;

            if (_config.Settings.EventBindings.TryGetValue(eventKey, out var bindings) && bindings.TryGetValue(session.Device.Id, out var action) && action.Enabled)
            {
                session.SetRequest(new OutputRequest
                {
                    RequestId = requestId,
                    EventKey = eventKey,
                    Config = action.Copy(),
                    Context = context,
                    // Every live game state is polled five times per second. A short
                    // renewable lease guarantees that a failed poll, despawned source
                    // or scene transition cannot leave a device running forever.
                    ExpiresAt = DateTimeOffset.UtcNow.Add(StateLease)
                });
            }
        }
    }

    public void ClearStatePrefix(string eventPrefix, string sourceId)
    {
        foreach (var definition in EventCatalog.All.Where(x => x.Key.StartsWith(eventPrefix, StringComparison.OrdinalIgnoreCase)))
            SetState(definition.Key, sourceId, false);
    }

    public void ClearAllStates(string reason)
    {
        foreach (var session in _ble.Sessions)
        {
            session.RemoveRequestsWithPrefix("state|");
            session.RemoveRequestsWithPrefix("pulse|");
        }
        _observedStates.Clear();
        var summary = $"{DateTime.Now:HH:mm:ss}　清除持续状态：{reason}";
        _lastEventSummary = summary;
        _recentEvents.Enqueue(summary);
        TrimRecentEvents();
    }

    public void Test(DeviceSession session, DeviceActionConfig config)
    {
        session.SetRequest(new OutputRequest
        {
            RequestId = "test|ui",
            EventKey = "test",
            Config = config.Copy(),
            Context = new EventContext(GameLevel: 1),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(config.DurationSeconds, 0.5f, 10f))
        });
    }

    private void Record(string eventKey, string sourceId, string action, EventContext context)
    {
        var title = EventCatalog.Find(eventKey)?.Title ?? eventKey;
        var detail = string.IsNullOrWhiteSpace(context.Detail) ? sourceId : context.Detail;
        var suffix = context.FailureCount > 0 ? $"，惩罚次数 {context.FailureCount}" : context.GameLevel > 1 ? $"，挡位 {context.GameLevel}" : "";
        var summary = $"{DateTime.Now:HH:mm:ss}　{action}：{title}　[{detail}{suffix}]";
        _lastEventSummary = summary;
        _recentEvents.Enqueue(summary);
        TrimRecentEvents();
        Plugin.LogSource.LogInfo("游戏事件：" + summary);
    }

    private void TrimRecentEvents()
    {
        while (_recentEvents.Count > 20) _recentEvents.TryDequeue(out _);
    }
}
