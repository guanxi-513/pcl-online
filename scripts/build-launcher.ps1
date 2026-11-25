param(
    [ValidateSet('Debug', 'Release', 'Snapshot')][string]$Configuration = 'Release',
    [string]$Dotnet = 'dotnet',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $root 'MeloongCore/Shared/MeloongCore.csproj'))) {
    throw 'MeloongCore is missing. For a git checkout run: git submodule update --init --recursive'
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $Dotnet build (Join-Path $root 'PCLCS/PCLCS.csproj') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Core build failed.' }
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
$references = Join-Path $nuget 'microsoft.netframework.referenceassemblies.net48/1.0.3/build/'
$sdkLines = & $Dotnet --list-sdks
$sdk = $sdkLines | Where-Object { $_ -match '^9\.' } | Select-Object -Last 1
if (-not $sdk -or $sdk -notmatch '^(\S+) \[(.+)\]$') { throw '.NET SDK 9 is required for the SDK-only build.' }
$sdkPath = Join-Path $Matches[2] $Matches[1]
$winfx = Join-Path $sdkPath 'Sdks/Microsoft.NET.Sdk.WindowsDesktop/targets/Microsoft.WinFX.targets'
$extra = @()
if ($OutputDirectory) { $extra += "-p:OutputPath=$([IO.Path]::GetFullPath($OutputDirectory))/" }
& $Dotnet msbuild (Join-Path $root 'Plain Craft Launcher 2/Plain Craft Launcher 2.vbproj') @extra `
    "-p:Configuration=$Configuration" '-p:IncludePackageReferencesDuringMarkupCompilation=false' `
    "-p:TargetFrameworkRootPath=$references" "-p:CustomAfterMicrosoftCommonTargets=$winfx" -v:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }
