# Third-party runtime

`mod_out` includes the official BepInEx Unity IL2CPP Windows x64 bleeding-edge build `6.0.0-be.755+3fab71a`, downloaded from the official BepInEx build service:

`https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.755%2B3fab71a.zip`

Downloaded archive SHA-256: `3616D6A67F5F595973EC4AA7BD7EDAF7F799D5BB9926F7146A6DCC7B4ABF478F`.

BepInEx is licensed under LGPL-2.1. The official project and license are at `https://github.com/BepInEx/BepInEx`.

The plugin directory also contains `Microsoft.Windows.SDK.NET.dll` and `WinRT.Runtime.dll`, supplied by the Microsoft Windows SDK .NET targeting/runtime packages so the Windows BLE APIs resolve inside BepInEx's bundled .NET 6 runtime.
