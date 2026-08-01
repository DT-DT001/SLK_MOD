using System.Text.Json;
using BepInEx;

namespace SLKDeviceLink;

public sealed class WaveformDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public WaveformKind Kind { get; init; }
    public int? FixedMode { get; init; }
    public List<(byte Frequency, byte Pulse)> YcySamples { get; init; } = new();
    public List<byte[]> CoyoteFrames { get; init; } = new();
    public List<float> ToyEnvelope { get; init; } = new();
}

public sealed class WaveformLibrary
{
    private readonly object _gate = new();
    private readonly List<WaveformDefinition> _all = new();

    public int LastFailedFileCount { get; private set; }

    public IReadOnlyList<WaveformDefinition> All
    {
        get { lock (_gate) return _all.ToList(); }
    }

    public IEnumerable<WaveformDefinition> For(DeviceKind kind)
    {
        lock (_gate) return _all.Where(x => x.Kind == KindFor(kind)).ToList();
    }

    public void Load()
    {
        lock (_gate)
        {
            _all.Clear();
            LastFailedFileCount = 0;
            var root = Path.Combine(Paths.GameRootPath, "waveforms");
            Directory.CreateDirectory(root);

            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(file => Path.GetExtension(file).Equals(".pulse", StringComparison.OrdinalIgnoreCase) ||
                               Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => Path.GetExtension(file).Equals(".pulse", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(file => file, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                try
                {
                    var waveform = Path.GetExtension(file).Equals(".pulse", StringComparison.OrdinalIgnoreCase)
                        ? LoadPulse(root, file)
                        : LoadJson(file);
                    if (_all.Any(item => string.Equals(item.Id, waveform.Id, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException($"波形 id 重复：{waveform.Id}");
                    _all.Add(waveform);
                }
                catch (Exception ex)
                {
                    LastFailedFileCount++;
                    Plugin.LogSource.LogWarning($"忽略无效波形 {file}: {ex.Message}");
                }
            }

            AddFallbacks();
            _all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
            Plugin.LogSource.LogInfo($"已从 {root} 加载 {_all.Count} 个波形，失败 {LastFailedFileCount} 个");
        }
    }

    public WaveformDefinition Get(DeviceKind kind, string? id)
    {
        lock (_gate)
        {
            var targetKind = KindFor(kind);
            return _all.FirstOrDefault(x => x.Kind == targetKind && string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
                   ?? _all.First(x => x.Kind == targetKind);
        }
    }

    private static WaveformKind KindFor(DeviceKind kind) => kind switch
    {
        DeviceKind.CoyoteV3 => WaveformKind.Coyote,
        DeviceKind.YcyToy => WaveformKind.Toy,
        _ => WaveformKind.Ycy
    };

    private static void ValidateCoyote(byte[] bytes)
    {
        for (var i = 0; i < 4; i++)
            if (bytes[i] is < 10 or > 240) throw new InvalidDataException("郊狼频率必须在 10-240");
        for (var i = 4; i < 8; i++)
            if (bytes[i] > 100) throw new InvalidDataException("郊狼波形强度必须在 0-100");
    }

    private static WaveformDefinition LoadPulse(string root, string file)
    {
        var pulse = CoyotePulseFile.Load(root, file);
        var waveform = new WaveformDefinition { Id = pulse.Id, Name = pulse.Name, Kind = WaveformKind.Coyote };
        waveform.CoyoteFrames.AddRange(pulse.Frames);
        return waveform;
    }

    private static WaveformDefinition LoadJson(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var json = document.RootElement;
        var id = json.GetProperty("id").GetString() ?? Path.GetFileNameWithoutExtension(file);
        var name = json.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? id : id;

        if (json.TryGetProperty("mode", out var modeNode))
            return new WaveformDefinition { Id = id, Name = name, Kind = WaveformKind.Ycy, FixedMode = Math.Clamp(modeNode.GetInt32(), 1, 16) };

        if (json.TryGetProperty("ycyData", out var ycyNode))
        {
            var waveform = new WaveformDefinition { Id = id, Name = name, Kind = WaveformKind.Ycy };
            foreach (var pair in ycyNode.EnumerateArray())
                waveform.YcySamples.Add(((byte)Math.Clamp(pair[0].GetInt32(), 1, 100), (byte)Math.Clamp(pair[1].GetInt32(), 0, 100)));
            if (waveform.YcySamples.Count == 0) throw new InvalidDataException("ycyData 不能为空");
            return waveform;
        }

        // 兼容 1.0.0 早期测试版；新的郊狼文件应使用官方 .pulse 文本格式。
        if (json.TryGetProperty("pulseData", out var pulseNode))
        {
            var waveform = new WaveformDefinition { Id = id, Name = name, Kind = WaveformKind.Coyote };
            foreach (var value in pulseNode.EnumerateArray())
            {
                var bytes = Convert.FromHexString(value.GetString() ?? "");
                if (bytes.Length != 8) throw new InvalidDataException("pulseData 每帧必须是 8 字节（16 个十六进制字符）");
                ValidateCoyote(bytes);
                waveform.CoyoteFrames.Add(bytes);
            }
            if (waveform.CoyoteFrames.Count == 0) throw new InvalidDataException("pulseData 不能为空");
            return waveform;
        }

        if (json.TryGetProperty("toyData", out var toyNode))
        {
            var waveform = new WaveformDefinition { Id = id, Name = name, Kind = WaveformKind.Toy };
            foreach (var value in toyNode.EnumerateArray())
                waveform.ToyEnvelope.Add(Math.Clamp(value.GetSingle(), 0f, 1f));
            if (waveform.ToyEnvelope.Count == 0) throw new InvalidDataException("toyData 不能为空");
            return waveform;
        }

        throw new InvalidDataException("JSON 不包含受支持的役次元或玩具波形字段");
    }

    private void AddFallbacks()
    {
        if (_all.All(x => x.Kind != WaveformKind.Ycy))
            _all.Add(new WaveformDefinition { Id = "ycy-mode-01", Name = "固定模式 01", Kind = WaveformKind.Ycy, FixedMode = 1 });
        if (_all.All(x => x.Kind != WaveformKind.Coyote))
            _all.Add(new WaveformDefinition { Id = "c3-safe", Name = "柔和持续", Kind = WaveformKind.Coyote, CoyoteFrames = { new byte[] { 10, 10, 10, 10, 20, 20, 20, 20 } } });
        if (_all.All(x => x.Kind != WaveformKind.Toy))
            _all.Add(new WaveformDefinition { Id = "toy-steady", Name = "持续", Kind = WaveformKind.Toy, ToyEnvelope = { 1f } });
    }
}
