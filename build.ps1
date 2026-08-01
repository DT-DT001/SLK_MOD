param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$localAppData = Join-Path $projectRoot '.appdata'
New-Item -ItemType Directory -Force (Join-Path $localAppData 'NuGet') | Out-Null
$env:APPDATA = $localAppData
$env:DOTNET_CLI_HOME = $localAppData
$env:NUGET_PACKAGES = Join-Path $localAppData 'packages'

dotnet restore (Join-Path $projectRoot 'src\SLKDeviceLink\SLKDeviceLink.csproj') --configfile (Join-Path $projectRoot 'NuGet.Config')
if (-not $SkipTests) {
    dotnet restore (Join-Path $projectRoot 'tests\ProtocolSelfTest\ProtocolSelfTest.csproj') --configfile (Join-Path $projectRoot 'NuGet.Config')
    dotnet run --no-restore --project (Join-Path $projectRoot 'tests\ProtocolSelfTest\ProtocolSelfTest.csproj')
}
dotnet build (Join-Path $projectRoot 'src\SLKDeviceLink\SLKDeviceLink.csproj') -c Release --no-restore

$build = Join-Path $projectRoot 'src\SLKDeviceLink\bin\Release\net6.0-windows10.0.19041.0'
foreach ($rootName in @('mod_out', 'Game')) {
    $root = Join-Path $projectRoot $rootName
    $legacyPlugins = Join-Path $root 'BepInEx\plugins\LLJDeviceLink'
    if (Test-Path -LiteralPath $legacyPlugins) {
        Remove-Item -LiteralPath $legacyPlugins -Recurse -Force
    }
    $plugins = Join-Path $root 'BepInEx\plugins\SLKDeviceLink'
    New-Item -ItemType Directory -Force $plugins | Out-Null
    Copy-Item -LiteralPath (Join-Path $build 'SLKDeviceLink.dll'), (Join-Path $build 'Microsoft.Windows.SDK.NET.dll'), (Join-Path $build 'WinRT.Runtime.dll') -Destination $plugins -Force
    $waveforms = Join-Path $root 'waveforms'
    New-Item -ItemType Directory -Force $waveforms | Out-Null
    $waveformSource = Join-Path $projectRoot 'waveforms'
    foreach ($pulseFile in Get-ChildItem -LiteralPath $waveformSource -Filter '*.pulse' -Recurse) {
        $closeBracket = $pulseFile.BaseName.IndexOf(']')
        if ($pulseFile.BaseName.StartsWith('[') -and $closeBracket -gt 0) {
            $legacyName = $pulseFile.BaseName.Substring($closeBracket + 1) + '.json'
            $relativeDirectory = $pulseFile.DirectoryName.Substring($waveformSource.Length).TrimStart([IO.Path]::DirectorySeparatorChar)
            $legacyFile = Join-Path (Join-Path $waveforms $relativeDirectory) $legacyName
            if (Test-Path -LiteralPath $legacyFile) {
                Remove-Item -LiteralPath $legacyFile -Force
            }
        }
    }
    Copy-Item -Path (Join-Path $projectRoot 'waveforms\*') -Destination $waveforms -Recurse -Force
}

Copy-Item -LiteralPath (Join-Path $projectRoot 'USER_GUIDE.md') -Destination (Join-Path $projectRoot 'mod_out\SLKDeviceLink-README.zh-CN.md') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY.md') -Destination (Join-Path $projectRoot 'mod_out\THIRD_PARTY.md') -Force

Write-Host 'Build and deployment complete.'
