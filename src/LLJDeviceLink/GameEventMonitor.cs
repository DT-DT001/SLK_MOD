using ExposureUnnoticed2.Master.AdultGoods;
using ExposureUnnoticed2.Object3D.AdultGoods;
using ExposureUnnoticed2.Object3D.NPC.Script;
using ExposureUnnoticed2.Object3D.Player.Scripts;
using ExposureUnnoticed2.Scripts.InGame;
using ExposureUnnoticed2.Scripts.Mission;

namespace LLJDeviceLink;

/// <summary>
/// Polls the game's generated IL2CPP interop types. Every stateful output is
/// renewed with a short lease by EventRouter, so scene changes and destroyed
/// Unity objects cannot leave hardware running indefinitely.
/// </summary>
public sealed class GameEventMonitor
{
    private readonly EventRouter _router;
    private readonly ModSettings _settings;
    private readonly HashSet<string> _activeItemSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, MissionSnapshot> _missions = new();
    private ActiveMissionSnapshot? _randomMission;
    private ActiveMissionSnapshot? _droneMission;
    private readonly Dictionary<string, float> _nextSectionErrorLog = new(StringComparer.OrdinalIgnoreCase);
    private float _nextPoll;
    private bool _inGame;
    private bool _gameOver;
    private int? _remainingLife;
    private int _failureCount;
    private int? _exposureStage;
    private int? _riskBand;
    private string _stage = "";

    public GameEventMonitor(EventRouter router, ModSettings settings)
    {
        _router = router;
        _settings = settings;
    }

    public int FailureCount => _failureCount;
    public string CurrentSummary { get; private set; } = "等待进入游戏";

    public void Tick(float realtimeSinceStartup)
    {
        if (realtimeSinceStartup < _nextPoll) return;
        _nextPoll = realtimeSinceStartup + 0.2f;

        TryPoll("游戏状态", PollGame, realtimeSinceStartup);
        if (!_inGame || _gameOver) return;
        TryPoll("道具", PollItems, realtimeSinceStartup);
        TryPoll("露出/警戒", PollExposureAndRisk, realtimeSinceStartup);
        TryPoll("心率/快感/精神", PollPlayerMeters, realtimeSinceStartup);
        TryPoll("任务", PollMissions, realtimeSinceStartup);
    }

    private void TryPoll(string section, Action poll, float realtimeSinceStartup)
    {
        try
        {
            poll();
        }
        catch (Exception ex)
        {
            if (_nextSectionErrorLog.TryGetValue(section, out var nextLog) && realtimeSinceStartup < nextLog) return;
            _nextSectionErrorLog[section] = realtimeSinceStartup + 10f;
            Plugin.LogSource.LogDebug($"{section}事件轮询暂不可用：{ex.Message}");
        }
    }

    private void PollGame()
    {
        var manager = InGameManager.Instance;
        var exists = InGameManager.isExist && manager is not null;
        var data = GameState.GameStateData;
        var nowInGame = exists && data is not null;

        if (!_inGame && nowInGame)
        {
            ResetJourneySnapshots();
            _router.ClearAllStates("新旅程开始");
            _router.Pulse("game.start");
        }
        else if (_inGame && !nowInGame)
        {
            _router.ClearAllStates("离开旅程");
            if (!_gameOver)
                _router.Pulse("game.journey.end", new EventContext(FailureCount: _failureCount));
        }

        _inGame = nowInGame;
        if (!nowInGame)
        {
            CurrentSummary = "未处于游戏旅程中";
            return;
        }

        var temp = manager!.TempInGameState;
        var gameOver = temp is not null && temp.IsGameOver;
        if (!_gameOver && gameOver)
        {
            _router.ClearAllStates("游戏最终失败");
            _router.Pulse("game.over", new EventContext(FailureCount: _failureCount));
        }
        _gameOver = gameOver;

        var remainingLife = Math.Max(0, data!.PlayerLife);
        if (_remainingLife is { } previousLife && remainingLife < previousLife && remainingLife > 0 && !gameOver)
        {
            _failureCount += Math.Max(1, previousLife - remainingLife);
            _router.Pulse("game.penalty",
                new EventContext(FailureCount: _failureCount, NumericValue: remainingLife,
                    Detail: $"剩余生命 {remainingLife}/{Math.Max(1, data.PlayerMaxLife)}"));
        }
        _remainingLife = remainingLife;

        var stage = data.CurrentStageType.ToString();
        if (!string.IsNullOrEmpty(_stage) && !string.Equals(_stage, stage, StringComparison.Ordinal))
            _router.Pulse("game.stage.change", new EventContext(Detail: $"{_stage} → {stage}"), stage);
        _stage = stage;

        CurrentSummary = $"旅程中 | 生命 {remainingLife}/{Math.Max(1, data.PlayerMaxLife)} | 惩罚 {_failureCount} | 场景 {stage}";
    }

    private void PollItems()
    {
        var current = new Dictionary<string, (string Name, int Level)>(StringComparer.OrdinalIgnoreCase);
        var vibrationMode = CommonVibratorController.VibrationStrength;
        var vibrationActive = vibrationMode != VibrationModeType.Off;
        var data = GameState.GameStateData;
        var vibrationLevel = Math.Clamp(Math.Max((int)vibrationMode, data?.VibeRemoconLevel + 1 ?? 1), 1, 20);

        if (vibrationActive)
        {
            AddEquipped(current, MAdultGoodsType.Vibrator, "振动棒", vibrationLevel);
            AddEquipped(current, MAdultGoodsType.TitRotor, "乳首跳蛋", vibrationLevel);
            AddEquipped(current, MAdultGoodsType.KuriRotor, "外置跳蛋", vibrationLevel);
            if (current.Count == 0) current["vibrator"] = ("振动道具", vibrationLevel);
        }

        var player = PlayerController.Instance;
        var piston = player?.Pca?.PistonMachineController;
        var pistonLevel = Math.Clamp(Math.Max(piston?.CurrentSpeedType ?? 0, data?.PistonMachineModeType ?? 0), 0, 20);
        if (pistonLevel > 0)
        {
            AddEquipped(current, MAdultGoodsType.PistonFuta, "扶她活塞", pistonLevel);
            AddEquipped(current, MAdultGoodsType.PistonAnal, "后庭活塞", pistonLevel);
            AddEquipped(current, MAdultGoodsType.PistonPussy, "阴道活塞", pistonLevel);
            if (!current.Keys.Any(key => key.StartsWith("piston-", StringComparison.OrdinalIgnoreCase)))
                current["piston"] = ("活塞道具", pistonLevel);
        }

        foreach (var item in current)
        {
            _router.SetState("item.any.active", item.Key, true,
                new EventContext(GameLevel: Math.Max(1, item.Value.Level), NumericValue: item.Value.Level, Detail: item.Value.Name));
        }

        foreach (var stopped in _activeItemSources.Except(current.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
            _router.SetState("item.any.active", stopped, false, new EventContext(Detail: stopped));

        _activeItemSources.Clear();
        foreach (var source in current.Keys) _activeItemSources.Add(source);
    }

    private static void AddEquipped(Dictionary<string, (string Name, int Level)> target, MAdultGoodsType type, string name, int level)
    {
        if (!GameStateFacade.IsEquipAdultGoods(type)) return;
        var prefix = type is MAdultGoodsType.PistonFuta or MAdultGoodsType.PistonAnal or MAdultGoodsType.PistonPussy
            ? "piston-"
            : "vibrator-";
        target[prefix + type] = (name, level);
    }

    private void PollExposureAndRisk()
    {
        var data = GameState.GameStateData;
        if (data is null) return;

        var clothesA = data.PlayerCurrentClothesAState.ToString();
        var clothesB = data.PlayerCurrentClothesBState.ToString();
        var stageA = clothesA switch
        {
            "OpenA1" => 1,
            "OpenA2" => 2,
            "OpenA3" => 3,
            "DropClothes" => 4,
            _ => 0
        };
        var stageB = clothesB == "OpenB1" ? 1 : 0;
        var exposure = stageA + stageB;
        if (_exposureStage is { } previousExposure && previousExposure != exposure)
        {
            _router.Pulse("exposure.change",
                new EventContext(GameLevel: exposure + 1, NumericValue: exposure,
                    Detail: $"露出阶段 {previousExposure} → {exposure}"), "clothes");
        }
        _exposureStage = exposure;
        _router.SetState("exposure.active", "clothes", exposure > 0,
            new EventContext(GameLevel: exposure + 1, NormalizedValue: exposure / 5f,
                NumericValue: exposure, Detail: $"露出阶段 {exposure}/5"));

        var naked = clothesA == "DropClothes";
        _router.SetState("exposure.naked", "player", naked,
            new EventContext(GameLevel: exposure + 1, NumericValue: exposure, Detail: naked ? "完全裸露" : "仍有衣物"));

        var npc = NpcManager.Instance;
        if (npc is null) return;
        var rawRisk = Math.Max(0f, npc.MaxWatchingNpcStrangeness);
        var riskPercent = ToPercent(rawRisk);
        var riskBand = riskPercent >= _settings.RiskHighThreshold ? 3 :
            riskPercent >= _settings.RiskMediumThreshold ? 2 : riskPercent > 0.01f ? 1 : 0;
        if (_riskBand is { } previousBand && previousBand != riskBand)
        {
            _router.Pulse("risk.change", new EventContext(GameLevel: Math.Max(1, riskBand),
                NormalizedValue: riskPercent / 100f, NumericValue: riskPercent,
                Detail: $"警戒等级 {previousBand} → {riskBand}"), "npc-risk");
        }
        _riskBand = riskBand;
        _router.SetState("risk.active", "npc-risk", riskBand > 0,
            new EventContext(GameLevel: Math.Max(1, riskBand), NormalizedValue: riskPercent / 100f,
                NumericValue: riskPercent, Detail: $"警戒值 {riskPercent:0.#}%"));
        _router.SetState("npc.detected", "npc", npc.IsSomeOnePlayerFound,
            new EventContext(GameLevel: npc.IsSomeOnePlayerFoundAndVisibleInCameraSomeOne ? 2 : 1,
                NormalizedValue: npc.IsSomeOnePlayerFound ? 1f : 0f,
                NumericValue: riskPercent,
                Detail: npc.IsSomeOnePlayerFound ? "玩家已被发现" : "玩家已脱离发现"));
    }

    private void PollPlayerMeters()
    {
        var data = GameState.GameStateData;
        var player = PlayerController.Instance;
        if (data is null || player is null) return;

        var heart = player.Pca?.PlayerHeartBeat;
        if (heart is not null)
        {
            var span = Math.Max(1f, PlayerHeartBeat.MaxBeat - PlayerHeartBeat.MinBeat);
            var normalized = Math.Clamp((heart.HeartRate - PlayerHeartBeat.MinBeat) / span, 0f, 1f);
            var elevated = normalized >= _settings.HeartMediumThreshold;
            var level = normalized >= _settings.HeartHighThreshold ? 2 : 1;
            _router.SetState("heart.active", "heart", elevated,
                new EventContext(GameLevel: level, NormalizedValue: normalized,
                    NumericValue: normalized * 100f,
                    Detail: $"心率 {heart.HeartRate:0}（{normalized * 100f:0}%）"));
        }

        var ecstasyPercent = ToPercent(Math.Max(0f, data.PlayerEcstasy));
        _router.SetState("pleasure.active", "pleasure", ecstasyPercent > 0.01f,
            new EventContext(GameLevel: MeterLevel(ecstasyPercent), NormalizedValue: ecstasyPercent / 100f,
                NumericValue: ecstasyPercent, Detail: $"快感 {ecstasyPercent:0.#}%"));
        _router.SetState("pleasure.climax", "pleasure", player.PlayerState.IsEcstasyMotion,
            new EventContext(GameLevel: 3, NormalizedValue: ecstasyPercent / 100f,
                NumericValue: ecstasyPercent, Detail: "高潮动作"));
        _router.SetState("pleasure.restraint", "pleasure", player.PlayerState.IsGamanBaibu,
            new EventContext(GameLevel: MeterLevel(ecstasyPercent), NormalizedValue: ecstasyPercent / 100f,
                NumericValue: ecstasyPercent, Detail: "忍耐振动"));

        var maxMental = Math.Max(1f, player.Mental?.MaxMental ?? 100f);
        var mentalNormalized = Math.Clamp(data.PlayerMental / maxMental, 0f, 1f);
        var mentalLow = mentalNormalized <= _settings.MentalLowThreshold;
        _router.SetState("mental.low", "mental", mentalLow,
            new EventContext(GameLevel: mentalNormalized <= 0.1f ? 2 : 1,
                NormalizedValue: 1f - mentalNormalized,
                NumericValue: (1f - mentalNormalized) * 100f,
                Detail: $"精神剩余 {mentalNormalized * 100f:0}%"));
    }

    private void PollMissions()
    {
        var manager = MissionManager.Instance;
        var current = new Dictionary<int, MissionSnapshot>();

        if (manager?.MissionList is not null)
        {
            for (var index = 0; index < manager.MissionList.Count; index++)
            {
                var mission = manager.MissionList[index];
                if (mission is null) continue;
                var id = mission.UniqueMissionId;
                var name = string.IsNullOrWhiteSpace(mission.MissionName) ? $"普通任务 {id}" : mission.MissionName;
                var complete = mission.IsComplete || mission.IsDoneOneCompleteProcess;
                var progress = ProgressPercent(mission.Satisfy);
                var snapshot = new MissionSnapshot(name, complete, progress);
                var hadPrevious = _missions.TryGetValue(id, out var previous);
                var source = $"regular:{id}";

                if (!hadPrevious && !complete)
                    _router.Pulse("task.start", new EventContext(Detail: name), source);
                if (hadPrevious && !previous.Complete && complete)
                    _router.Pulse("task.complete", new EventContext(NumericValue: progress, Detail: name), source);

                _router.SetState("task.active", source, !complete,
                    new EventContext(GameLevel: MeterLevel(progress), NormalizedValue: progress / 100f, NumericValue: progress,
                        Detail: $"{name}（进度 {progress:0.#}%）"));
                current[id] = snapshot;
            }
        }

        foreach (var missing in _missions.Keys.Except(current.Keys).ToArray())
        {
            var previous = _missions[missing];
            // The regular mission list is rebuilt during stage changes. Disappearing
            // from that list is not a mission failure, so only end its live state.
            _router.SetState("task.active", $"regular:{missing}", false, new EventContext(Detail: previous.Name));
        }

        _missions.Clear();
        foreach (var mission in current) _missions[mission.Key] = mission.Value;

        PollRandomMission();
        PollDroneMission();
    }

    private void PollRandomMission()
    {
        var manager = RandomMissionManager.Instance;
        if (manager is null) return;
        var mission = manager.MissionBase;
        var active = manager.IsActiveMission && mission is not null;
        UpdateActiveMission(ref _randomMission, "random", manager.currentMissionNumId.ToString(), active,
            mission?.MissionName ?? "随机任务", mission?.IsComplete ?? false,
            ProgressPercent(mission?.Progress ?? 0f));
    }

    private void PollDroneMission()
    {
        var manager = DroneManager.Instance;
        if (manager is null) return;
        var active = manager.IsActiveMission;
        var mission = active ? manager.GetActiveMission() : null;
        var id = GameState.GameStateData?.ActiveDroneMissionId ?? -1;
        UpdateActiveMission(ref _droneMission, "drone", id.ToString(), active && mission is not null,
            mission?.MissionName ?? "无人机任务", mission?.IsComplete ?? false,
            ProgressPercent(mission?.Progress ?? 0f));
    }

    private void UpdateActiveMission(ref ActiveMissionSnapshot? previous, string type, string id, bool active,
        string name, bool complete, float progress)
    {
        var source = $"{type}:{id}";
        if (!active)
        {
            if (previous is not null)
                _router.SetState("task.active", previous.Source, false, new EventContext(Detail: previous.Name));
            previous = null;
            return;
        }

        if (previous is not null && !string.Equals(previous.Source, source, StringComparison.OrdinalIgnoreCase))
            _router.SetState("task.active", previous.Source, false, new EventContext(Detail: previous.Name));

        var isNew = previous is null || !string.Equals(previous.Source, source, StringComparison.OrdinalIgnoreCase);
        if (isNew && !complete)
            _router.Pulse("task.start", new EventContext(Detail: name), source);
        if (!isNew && previous is not null && !previous.Complete && complete)
            _router.Pulse("task.complete", new EventContext(NumericValue: progress, Detail: name), source);

        _router.SetState("task.active", source, !complete,
            new EventContext(GameLevel: MeterLevel(progress), NormalizedValue: progress / 100f, NumericValue: progress,
                Detail: $"{name}（进度 {progress:0.#}%）"));
        previous = new ActiveMissionSnapshot(source, name, complete, progress);
    }

    private void ResetJourneySnapshots()
    {
        _activeItemSources.Clear();
        _missions.Clear();
        _randomMission = null;
        _droneMission = null;
        _remainingLife = null;
        _failureCount = 0;
        _exposureStage = null;
        _riskBand = null;
        _gameOver = false;
        _stage = "";
    }

    private static float ToPercent(float value) => value <= 1.01f ? value * 100f : Math.Clamp(value, 0f, 100f);
    private static float Normalize01(float value) => value <= 1.01f ? Math.Clamp(value, 0f, 1f) : Math.Clamp(value / 100f, 0f, 1f);
    private static float ProgressPercent(float value) => Normalize01(Math.Max(0f, value)) * 100f;
    private static int MeterLevel(float percent) => percent >= 75f ? 3 : percent >= 35f ? 2 : 1;

    private readonly record struct MissionSnapshot(string Name, bool Complete, float Progress);
    private sealed record ActiveMissionSnapshot(string Source, string Name, bool Complete, float Progress);
}
