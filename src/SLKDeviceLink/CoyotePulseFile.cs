using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SLKDeviceLink;

public sealed class CoyotePulseWaveform
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public List<byte[]> Frames { get; init; } = new();
}

/// <summary>解析 DG-LAB 3.x 导出的 Dungeonlab+pulse 文本波形。</summary>
public static class CoyotePulseFile
{
    private const string Prefix = "Dungeonlab+pulse:";
    private const int MaxSections = 10;
    private const int MaxShapePoints = 500;

    private static readonly int[] FrequencySliderValues =
    {
        10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49,
        50, 52, 54, 56, 58, 60, 62, 64, 66, 68, 70, 72, 74, 76, 78, 80, 85, 90, 95,
        100, 110, 120, 130, 140, 150, 160, 170, 180, 190, 200, 233, 266, 300, 333, 366,
        400, 450, 500, 550, 600, 700, 800, 900, 1000
    };

    private static readonly double[] SectionTimeValues =
    {
        0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1,
        1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7, 1.8, 1.9, 2,
        2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 2.7, 2.8, 2.9, 3,
        3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7, 3.8, 3.9, 4,
        4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 4.7, 4.8, 4.9, 5,
        5.2, 5.4, 5.6, 5.8, 6, 6.2, 6.4, 6.6, 6.8, 7,
        7.2, 7.4, 7.6, 7.8, 8, 8.5, 9, 9.5, 10, 11,
        12, 13, 14, 15, 16, 17, 18, 19, 20, 23.4,
        26.6, 30, 33.4, 36.6, 40, 45, 50, 55, 60, 70,
        80, 90, 100, 120, 140, 160, 180, 200, 250, 300
    };

    public static CoyotePulseWaveform Load(string root, string file)
    {
        var relativePath = Path.GetRelativePath(root, file);
        var (id, name) = Identity(relativePath);
        return Parse(File.ReadAllText(file), id, name);
    }

    public static CoyotePulseWaveform Parse(string text, string id, string name)
    {
        if (text.Length > 4 * 1024 * 1024)
            throw new InvalidDataException(".pulse 文件超过 4 MB 安全限制");

        var data = text.Trim().TrimStart('\uFEFF');
        if (!data.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($".pulse 必须以 {Prefix} 开头");

        var body = data[Prefix.Length..];
        var equalIndex = body.IndexOf('=');
        var restTenths = 0;
        var speed = 1;
        string sectionBody;
        if (equalIndex >= 0)
        {
            var global = Split(body[..equalIndex], ',');
            if (global.Length != 3)
                throw new InvalidDataException(".pulse 波形描述必须包含休息时长、速度倍率和兼容参数");

            restTenths = ParseInt(global[0], "休息时长", 0, 36000);
            speed = ParseInt(global[1], "速度倍率", 1, 4);
            if (speed is not (1 or 2 or 4))
                throw new InvalidDataException(".pulse 速度倍率只能是 1、2 或 4");
            _ = ParseInt(global[2], "兼容参数", 0, int.MaxValue);
            sectionBody = body[(equalIndex + 1)..];
        }
        else if (body.Contains('/'))
        {
            // 一部分 APP 可正常导入的文件省略全局头，直接以首个小节开头；按无休息、1 倍速解释。
            sectionBody = body;
        }
        else
        {
            throw new InvalidDataException(".pulse 既没有全局头，也没有有效的小节数据");
        }

        var sectionTexts = sectionBody
            .Split(new[] { "+section+" }, StringSplitOptions.None);
        if (sectionTexts.Length is 0 or > MaxSections)
            throw new InvalidDataException($".pulse 小节数量必须在 1-{MaxSections} 之间");

        var samples = new List<(byte Frequency, byte Strength)>();
        for (var sectionIndex = 0; sectionIndex < sectionTexts.Length; sectionIndex++)
            AppendSection(samples, sectionTexts[sectionIndex], sectionIndex, speed);

        if (samples.Count == 0)
            throw new InvalidDataException(".pulse 没有启用的小节");

        for (var i = 0; i < restTenths * 4; i++)
            samples.Add((10, 0));

        return new CoyotePulseWaveform
        {
            Id = id,
            Name = name,
            Frames = PackFrames(samples)
        };
    }

    public static (string Id, string Name) Identity(string relativePath)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath).Trim();
        if (name.StartsWith("[", StringComparison.Ordinal))
        {
            var close = name.IndexOf(']');
            if (close > 1)
            {
                var explicitId = name[1..close].Trim();
                var displayName = name[(close + 1)..].Trim();
                if (displayName.Length > 0 && explicitId.All(IsIdCharacter))
                    return (explicitId, displayName);
            }
        }

        var normalized = relativePath.Replace('\\', '/').ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var generatedId = $"c3-pulse-{Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant()}";
        return (generatedId, name.Length > 0 ? name : generatedId);
    }

    private static void AppendSection(List<(byte Frequency, byte Strength)> output, string text, int index, int speed)
    {
        var slashIndex = text.IndexOf('/');
        if (slashIndex < 0)
            throw new InvalidDataException($".pulse 第 {index + 1} 小节缺少 / 分隔符");

        var header = Split(text[..slashIndex], ',');
        if (header.Length != 5)
            throw new InvalidDataException($".pulse 第 {index + 1} 小节描述必须包含 5 个值");

        var startIndex = ParseInt(header[0], $"第 {index + 1} 小节频率 A", 0, FrequencySliderValues.Length - 1);
        var endIndex = ParseInt(header[1], $"第 {index + 1} 小节频率 B", 0, FrequencySliderValues.Length - 1);
        var durationIndex = ParseInt(header[2], $"第 {index + 1} 小节时长", 0, SectionTimeValues.Length - 1);
        var mode = ParseInt(header[3], $"第 {index + 1} 小节频率模式", 1, 4);
        var enabled = ParseInt(header[4], $"第 {index + 1} 小节开关", 0, 1) == 1;

        var points = Split(text[(slashIndex + 1)..], ',');
        if (points.Length is < 2 or > MaxShapePoints)
            throw new InvalidDataException($".pulse 第 {index + 1} 小节必须包含 2-{MaxShapePoints} 个强度点");

        var strengths = new byte[points.Length];
        for (var pointIndex = 0; pointIndex < points.Length; pointIndex++)
        {
            var pair = Split(points[pointIndex], '-');
            if (pair.Length != 2)
                throw new InvalidDataException($".pulse 第 {index + 1} 小节第 {pointIndex + 1} 个强度点格式无效");

            if (!double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var strength) || strength is < 0 or > 100)
                throw new InvalidDataException($".pulse 第 {index + 1} 小节强度必须在 0-100");
            _ = ParseInt(pair[1], $"第 {index + 1} 小节锚点类型", 0, 1);
            strengths[pointIndex] = (byte)Math.Round(strength, MidpointRounding.AwayFromZero);
        }

        if (!enabled) return;

        var samplesPerPoint = 4 / speed;
        var elementSamples = strengths.Length * samplesPerPoint;
        var targetSamples = (int)Math.Ceiling(SectionTimeValues[durationIndex] * 40);
        var elementCount = Math.Max(1, (int)Math.Ceiling(targetSamples / (double)elementSamples));
        var totalPoints = strengths.Length * elementCount;

        for (var elementIndex = 0; elementIndex < elementCount; elementIndex++)
        {
            for (var pointIndex = 0; pointIndex < strengths.Length; pointIndex++)
            {
                var sectionPoint = elementIndex * strengths.Length + pointIndex;
                var progress = mode switch
                {
                    2 => Progress(sectionPoint, totalPoints),
                    3 => Progress(pointIndex, strengths.Length),
                    4 => Progress(elementIndex, elementCount),
                    _ => 0d
                };
                // APP 在频率滑块索引上做渐变，再查表得到真实周期；同一强度点内的 25 ms 子采样保持相同频率。
                var sliderIndex = (int)Math.Floor(startIndex + (endIndex - startIndex) * progress + 1e-9);
                var encodedFrequency = EncodeFrequency(FrequencySliderValues[Math.Clamp(sliderIndex, 0, FrequencySliderValues.Length - 1)]);
                for (var subSample = 0; subSample < samplesPerPoint; subSample++)
                    output.Add((encodedFrequency, strengths[pointIndex]));
            }
        }
    }

    private static List<byte[]> PackFrames(IReadOnlyList<(byte Frequency, byte Strength)> samples)
    {
        var frameCount = samples.Count / GreatestCommonDivisor(samples.Count, 4);
        var frames = new List<byte[]>(frameCount);
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var frame = new byte[8];
            for (var sampleIndex = 0; sampleIndex < 4; sampleIndex++)
            {
                var sample = samples[(frameIndex * 4 + sampleIndex) % samples.Count];
                frame[sampleIndex] = sample.Frequency;
                frame[sampleIndex + 4] = sample.Strength;
            }
            frames.Add(frame);
        }
        return frames;
    }

    private static byte EncodeFrequency(int value)
    {
        value = Math.Clamp(value, 10, 1000);
        return value switch
        {
            <= 100 => (byte)value,
            <= 600 => (byte)((value - 100) / 5 + 100),
            _ => (byte)((value - 600) / 10 + 200)
        };
    }

    private static double Progress(int position, int count) => count <= 1 ? 0d : position / (double)(count - 1);

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }

    private static int ParseInt(string value, string label, int minimum, int maximum)
    {
        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum || parsed > maximum)
            throw new InvalidDataException($".pulse {label} 必须在 {minimum}-{maximum} 之间");
        return parsed;
    }

    private static string[] Split(string value, char separator) => value
        .Split(separator, StringSplitOptions.None)
        .Select(item => item.Trim())
        .Where(item => item.Length > 0)
        .ToArray();

    private static bool IsIdCharacter(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.';
}
