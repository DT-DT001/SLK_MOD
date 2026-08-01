using System.Collections.Concurrent;

namespace LLJDeviceLink;

public enum DeviceKind
{
    Unknown,
    YcyEmsV1,
    YcyEmsV2,
    CoyoteV3,
    YcyToy
}

public enum LinkState
{
    Discovered,
    Connecting,
    Connected,
    Disconnecting,
    Disconnected,
    Error
}

public enum ToyAggregationMode
{
    Maximum,
    SumClamped
}

public enum WaveformKind
{
    Ycy,
    Coyote,
    Toy
}

public static class DeviceLimits
{
    public static int IntensityMax(DeviceKind kind) => kind switch
    {
        DeviceKind.YcyToy => 20,
        DeviceKind.CoyoteV3 => 200,
        DeviceKind.YcyEmsV1 or DeviceKind.YcyEmsV2 => 180,
        _ => 0
    };
}

public sealed class DeviceProfile
{
    public string DisplayName = "";
    public bool AutoConnect = true;
    public int CoyoteSoftMaxA = 60;
    public int CoyoteSoftMaxB = 60;
    public int CoyoteFrequencyBalanceA;
    public int CoyoteFrequencyBalanceB;
    public int CoyoteStrengthBalanceA;
    public int CoyoteStrengthBalanceB;
}

public sealed class DeviceActionConfig
{
    public bool Enabled = true;
    public int BaseIntensity = 10;
    // -1 is the backwards-compatible value for configurations saved before
    // per-event/per-device variable intensity caps were introduced.
    public int MaxIntensity = -1;
    public int IntensityPerPenalty = 5;
    public float DurationSeconds = 3f;
    public float DurationPerPenalty = 1f;
    public string WaveformId = "";
    public bool ChannelA = true;
    public bool ChannelB;
    public bool MotorA = true;
    public bool MotorB = true;
    public bool MotorC = true;
    public bool UseGameItemLevel = true;
    public int IntensityPerGameLevel = 2;
    public bool UseEventValue;
    public float EventValueMultiplier = 1f;
    public ToyAggregationMode ToyAggregation = ToyAggregationMode.Maximum;

    public DeviceActionConfig Copy() => (DeviceActionConfig)MemberwiseClone();

    public int EffectiveMaxIntensity(DeviceKind kind)
    {
        var protocolMax = DeviceLimits.IntensityMax(kind);
        return MaxIntensity < 0 ? protocolMax : Math.Clamp(MaxIntensity, 0, protocolMax);
    }

    public bool NormalizeForDevice(DeviceKind kind)
    {
        var protocolMax = DeviceLimits.IntensityMax(kind);
        var changed = false;

        changed |= Clamp(ref BaseIntensity, 0, protocolMax);
        changed |= Clamp(ref IntensityPerPenalty, 0, protocolMax);
        changed |= Clamp(ref IntensityPerGameLevel, 0, protocolMax);
        if (MaxIntensity < 0)
        {
            MaxIntensity = protocolMax;
            changed = true;
        }
        else
        {
            changed |= Clamp(ref MaxIntensity, 0, protocolMax);
        }

        changed |= Clamp(ref DurationSeconds, 0.1f, 600f, 3f);
        changed |= Clamp(ref DurationPerPenalty, 0f, 600f, 1f);
        changed |= Clamp(ref EventValueMultiplier, 0f, 20f, 1f);
        return changed;
    }

    public float EffectiveDuration(EventContext context)
        => Math.Clamp(DurationSeconds + Math.Max(0, context.FailureCount - 1) * DurationPerPenalty, 0.1f, 600f);

    private static bool Clamp(ref int value, int min, int max)
    {
        var next = Math.Clamp(value, min, max);
        if (next == value) return false;
        value = next;
        return true;
    }

    private static bool Clamp(ref float value, float min, float max, float fallback)
    {
        var next = float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
        if (next.Equals(value)) return false;
        value = next;
        return true;
    }
}

public sealed class ModSettings
{
    public int ConfigSchemaVersion;
    // Retained for backwards-compatible config loading. Scanning is now explicitly
    // started only by the UI's 10-second "连接设备" action.
    public bool AutoScanOnStartup;
    public bool AutoConnectRecognizedDevices;
    public float HeartMediumThreshold = 0.35f;
    public float HeartHighThreshold = 0.7f;
    public float MentalLowThreshold = 0.25f;
    public int RiskMediumThreshold = 25;
    public int RiskHighThreshold = 60;
    public Dictionary<string, DeviceProfile> DeviceProfiles = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, DeviceActionConfig>> EventBindings = new(StringComparer.OrdinalIgnoreCase);

    public bool NormalizeGlobalSettings()
    {
        var changed = false;
        changed |= Clamp(ref HeartMediumThreshold, 0f, 1f, 0.35f);
        changed |= Clamp(ref HeartHighThreshold, 0f, 1f, 0.7f);
        changed |= Clamp(ref MentalLowThreshold, 0f, 1f, 0.25f);
        changed |= Clamp(ref RiskMediumThreshold, 0, 100);
        changed |= Clamp(ref RiskHighThreshold, 0, 100);
        if (HeartMediumThreshold > HeartHighThreshold)
        {
            HeartMediumThreshold = HeartHighThreshold;
            changed = true;
        }
        if (RiskMediumThreshold > RiskHighThreshold)
        {
            RiskMediumThreshold = RiskHighThreshold;
            changed = true;
        }
        return changed;
    }

    public DeviceProfile GetProfile(string deviceId, string displayName)
    {
        if (!DeviceProfiles.TryGetValue(deviceId, out var profile))
        {
            profile = new DeviceProfile { DisplayName = displayName };
            DeviceProfiles[deviceId] = profile;
        }
        else if (!string.IsNullOrWhiteSpace(displayName))
        {
            profile.DisplayName = displayName;
        }

        return profile;
    }

    public Dictionary<string, DeviceActionConfig> GetBindings(string eventKey)
    {
        if (!EventBindings.TryGetValue(eventKey, out var bindings))
        {
            bindings = new Dictionary<string, DeviceActionConfig>(StringComparer.OrdinalIgnoreCase);
            EventBindings[eventKey] = bindings;
        }

        return bindings;
    }

    private static bool Clamp(ref int value, int min, int max)
    {
        var next = Math.Clamp(value, min, max);
        if (next == value) return false;
        value = next;
        return true;
    }

    private static bool Clamp(ref float value, float min, float max, float fallback)
    {
        var next = float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
        if (next.Equals(value)) return false;
        value = next;
        return true;
    }
}

public sealed record EventDefinition(string Key, string Category, string Title, bool Stateful, string NumericValueLabel = "");

public static class EventCatalog
{
    private static readonly ConcurrentDictionary<string, EventDefinition> Definitions = new(StringComparer.OrdinalIgnoreCase);

    static EventCatalog()
    {
        Add("game.start", "游戏", "游戏/旅程开始", false);
        Add("game.penalty", "游戏", "失败并进入惩罚（扣除生命）", false);
        Add("game.over", "游戏", "游戏最终失败", false);
        Add("game.journey.end", "游戏", "旅程结束", false);
        Add("game.stage.change", "游戏", "场景/区域切换", false);
        Add("item.any.active", "道具", "快感道具运行（持续；自动跟随挡位）", true);
        Add("exposure.change", "露出", "衣物露出程度变化", false, "露出阶段");
        Add("exposure.active", "露出", "处于露出状态（持续）", true, "露出阶段");
        Add("exposure.naked", "露出", "完全裸露（持续）", true);
        Add("risk.change", "警戒", "NPC 警戒等级变化", false, "警戒百分比");
        Add("risk.active", "警戒", "NPC 正在产生警戒（持续）", true, "警戒百分比");
        Add("heart.active", "心率", "心率升高（持续；恢复后停止）", true, "心率百分比");
        Add("pleasure.active", "快感", "快感值存在（持续）", true, "快感百分比");
        Add("pleasure.climax", "快感", "高潮（持续）", true, "快感百分比");
        Add("pleasure.restraint", "快感", "忍耐/克制（持续）", true, "快感百分比");
        Add("mental.low", "精神", "精神值过低（持续）", true, "精神缺失百分比");
        Add("task.start", "任务", "任务开始", false);
        Add("task.active", "任务", "任务进行中（持续）", true, "任务进度百分比");
        Add("task.complete", "任务", "任务完成", false);
        Add("npc.detected", "发现", "NPC 警戒/发现玩家（持续；躲开后停止）", true);
    }

    public static IEnumerable<EventDefinition> All => Definitions.Values.OrderBy(x => x.Category).ThenBy(x => x.Title);

    public static EventDefinition Add(string key, string category, string title, bool stateful, string numericValueLabel = "")
        => Definitions.GetOrAdd(key, _ => new EventDefinition(key, category, title, stateful, numericValueLabel));

    public static EventDefinition? Find(string key) => Definitions.TryGetValue(key, out var value) ? value : null;

    public static bool HasVariableIntensity(string eventKey, DeviceKind kind)
        => string.Equals(eventKey, "game.penalty", StringComparison.OrdinalIgnoreCase) ||
           kind == DeviceKind.YcyToy && string.Equals(eventKey, "item.any.active", StringComparison.OrdinalIgnoreCase) ||
           !string.IsNullOrWhiteSpace(Find(eventKey)?.NumericValueLabel);

    public const int CurrentConfigSchemaVersion = 5;

    public static bool MigrateBindings(ModSettings settings)
    {
        if (settings.ConfigSchemaVersion >= CurrentConfigSchemaVersion) return false;

        var old = settings.EventBindings ?? new Dictionary<string, Dictionary<string, DeviceActionConfig>>(StringComparer.OrdinalIgnoreCase);
        var migrated = new Dictionary<string, Dictionary<string, DeviceActionConfig>>(StringComparer.OrdinalIgnoreCase);

        void Merge(string target, Func<string, bool> sourceFilter, params string[] priority)
        {
            var targetBindings = new Dictionary<string, DeviceActionConfig>(StringComparer.OrdinalIgnoreCase);
            var priorityLookup = priority.Select((key, index) => (key, index))
                .ToDictionary(item => item.key, item => item.index, StringComparer.OrdinalIgnoreCase);
            foreach (var source in old.Where(pair => sourceFilter(pair.Key))
                         .OrderBy(pair => priorityLookup.TryGetValue(pair.Key, out var index) ? index : int.MaxValue)
                         .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var binding in source.Value)
                    targetBindings.TryAdd(binding.Key, binding.Value);
            }
            if (targetBindings.Count > 0) migrated[target] = targetBindings;
        }

        static bool IsOneOf(string value, params string[] candidates)
            => candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));

        foreach (var definition in All)
            Merge(definition.Key, key => string.Equals(key, definition.Key, StringComparison.OrdinalIgnoreCase), definition.Key);

        Merge("item.any.active",
            key => string.Equals(key, "item.any.active", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "item.any.level", StringComparison.OrdinalIgnoreCase) ||
                   key.StartsWith("item.", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".active", StringComparison.OrdinalIgnoreCase),
            "item.any.active", "item.any.level");
        Merge("exposure.change",
            key => string.Equals(key, "exposure.change", StringComparison.OrdinalIgnoreCase) ||
                   key.StartsWith("exposure.", StringComparison.OrdinalIgnoreCase) &&
                   (key.EndsWith(".increase", StringComparison.OrdinalIgnoreCase) || key.EndsWith(".decrease", StringComparison.OrdinalIgnoreCase)),
            "exposure.change", "exposure.increase", "exposure.decrease");
        Merge("risk.change", key => IsOneOf(key, "risk.change", "risk.high", "risk.medium", "risk.low"),
            "risk.change", "risk.high", "risk.medium", "risk.low");
        Merge("heart.active", key => IsOneOf(key, "heart.active", "heart.high", "heart.medium", "heart.low"),
            "heart.active", "heart.high", "heart.medium", "heart.low");
        Merge("task.start", key => string.Equals(key, "task.start", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("quest.", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".activate", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("challenge.", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".activate", StringComparison.OrdinalIgnoreCase), "task.start", "quest.activate", "challenge.activate");
        Merge("task.complete", key => string.Equals(key, "task.complete", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("quest.", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".complete", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("challenge.", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".achieved", StringComparison.OrdinalIgnoreCase), "task.complete", "quest.complete", "challenge.achieved");
        Merge("npc.detected", key => IsOneOf(key, "npc.detected", "npc.notice", "npc.alert"),
            "npc.detected", "npc.notice", "npc.alert");

        settings.EventBindings = migrated;
        settings.ConfigSchemaVersion = CurrentConfigSchemaVersion;
        return true;
    }
}

public readonly record struct EventContext(int FailureCount = 0, int GameLevel = 1, float NormalizedValue = 0f, string Detail = "", float NumericValue = 0f);

public sealed class OutputRequest
{
    public string RequestId { get; init; } = "";
    public string EventKey { get; init; } = "";
    public DeviceActionConfig Config { get; init; } = new();
    public EventContext Context { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }

    public int Intensity(DeviceKind kind)
    {
        var protocolMax = DeviceLimits.IntensityMax(kind);
        if (protocolMax <= 0) return 0;

        var value = Math.Clamp(Config.BaseIntensity, 0, protocolMax);
        if (Context.FailureCount > 1)
        {
            var increment = Math.Clamp(Config.IntensityPerPenalty, 0, protocolMax);
            var addition = (long)(Context.FailureCount - 1) * increment;
            value = (int)Math.Min(protocolMax, value + addition);
        }
        if (kind == DeviceKind.YcyToy && Config.UseGameItemLevel &&
            string.Equals(EventKey, "item.any.active", StringComparison.OrdinalIgnoreCase) && Context.GameLevel > 1)
        {
            var increment = Math.Clamp(Config.IntensityPerGameLevel, 0, protocolMax);
            var addition = (long)(Context.GameLevel - 1) * increment;
            value = (int)Math.Min(protocolMax, value + addition);
        }
        if (Config.UseEventValue && Context.NumericValue > 0f)
        {
            var multiplier = float.IsFinite(Config.EventValueMultiplier)
                ? Math.Clamp(Config.EventValueMultiplier, 0f, 20f)
                : 0f;
            var addition = (double)Context.NumericValue * multiplier;
            value = !double.IsFinite(addition) || addition >= protocolMax
                ? protocolMax
                : Math.Min(protocolMax, value + (int)MathF.Round((float)addition));
        }

        return Math.Min(value, Config.EffectiveMaxIntensity(kind));
    }
}
