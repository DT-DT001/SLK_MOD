using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace SLKDeviceLink;

[BepInPlugin(Guid, Name, Version)]
public sealed class Plugin : BasePlugin
{
    public const string Guid = "cn.slkmod.device-link";
    public const string Name = "SLK Device Link";
    public const string Version = "1.0.0";

    internal static ManualLogSource LogSource { get; private set; } = null!;

    public override void Load()
    {
        LogSource = Log;
        new Harmony(Guid).PatchAll();
        AddComponent<ModBehaviour>();
        Log.LogInfo($"{Name} {Version} loaded");
    }
}
