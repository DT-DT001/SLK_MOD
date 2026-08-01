using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BepInEx;

namespace SLKDeviceLink;

public sealed class ConfigStore
{
    private readonly object _ioLock = new();
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public ModSettings Settings { get; private set; }

    public ConfigStore()
    {
        _path = Path.Combine(Paths.ConfigPath, "SLKDeviceLink", "settings.json");
        var legacyPath = Path.Combine(Paths.ConfigPath, "LLJDeviceLink", "settings.json");
        var importedLegacy = false;
        try
        {
            if (!File.Exists(_path) && File.Exists(legacyPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.Copy(legacyPath, _path);
                importedLegacy = true;
            }
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogWarning($"旧 LLJ 配置自动迁移失败，将保留原文件：{ex.Message}");
        }

        Settings = Load();
        var normalized = Settings.NormalizeGlobalSettings();
        var migrated = EventCatalog.MigrateBindings(Settings);
        if (migrated)
        {
            BackupPreMigrationConfig();
        }
        if (normalized || migrated) Save();
        if (migrated)
            Plugin.LogSource.LogInfo("事件配置已升级为当前游戏目录；旧文件已保留为 settings.pre-schema5.json");
        if (importedLegacy)
            Plugin.LogSource.LogInfo("已将旧 LLJDeviceLink 配置导入 SLKDeviceLink；原配置文件仍保留作为备份");
    }

    private void BackupPreMigrationConfig()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var backup = Path.Combine(Path.GetDirectoryName(_path)!, "settings.pre-schema5.json");
            if (!File.Exists(backup)) File.Copy(_path, backup);
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogWarning($"旧事件配置备份失败，但仍会继续升级：{ex.Message}");
        }
    }

    private ModSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<ModSettings>(File.ReadAllText(_path), _options) ?? new ModSettings();
        }
        catch (Exception ex)
        {
            Plugin.LogSource.LogError($"配置读取失败，将使用默认值：{ex}");
        }

        return new ModSettings();
    }

    public void Save()
    {
        lock (_ioLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(Settings, _options));
                File.Move(temporary, _path, true);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"配置保存失败：{ex}");
            }
        }
    }

    public DeviceProfile GetProfile(string deviceId, string displayName)
    {
        lock (_ioLock) return Settings.GetProfile(deviceId, displayName);
    }
}
