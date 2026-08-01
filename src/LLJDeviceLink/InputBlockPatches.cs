using ExposureUnnoticed2.Object3D.Camera;
using ExposureUnnoticed2.Object3D.Player.Scripts;
using ExposureUnnoticed2.Scripts.Base;
using HarmonyLib;
using UnityEngine;

namespace LLJDeviceLink;

internal static class InputBlockState
{
    public static bool Active;
}

// The game resolves all player commands into InputData once per frame. Clearing
// the resolved value prevents menu clicks, movement and item hotkeys from leaking
// through while the mod configuration window is open.
[HarmonyPatch(typeof(InputResolver), nameof(InputResolver.Resolve))]
internal static class BlockResolvedPlayerInput
{
    private static void Postfix(InputData __result)
    {
        if (!InputBlockState.Active || __result is null) return;
        __result.ClearSomePart();
        __result.Move = Vector3.zero;
        __result.CameraMove = Vector2.zero;
        __result.Jump = false;
        __result.LightAttack = false;
        __result.Dash = false;
        __result.Interact = false;
        __result.JustInteract = false;
        __result.JustSelect = false;
        __result.JustCancel = false;
        __result.JustSwitchVibrator = false;
        __result.JustSwitchPistonMachine = false;
        __result.HoldCommonRingMenu = false;
        __result.HoldSkillRingMenu = false;
    }
}

[HarmonyPatch(typeof(TPCameraController), nameof(TPCameraController.OnLateUpdate))]
internal static class BlockThirdPersonCamera
{
    private static bool Prefix() => !InputBlockState.Active;
}

[HarmonyPatch(typeof(FPCameraController), nameof(FPCameraController.OnLateUpdate))]
internal static class BlockFirstPersonCamera
{
    private static bool Prefix() => !InputBlockState.Active;
}

[HarmonyPatch(typeof(InteractManager), nameof(InteractManager.OnInteract))]
internal static class BlockWorldInteraction
{
    private static bool Prefix() => !InputBlockState.Active;
}
