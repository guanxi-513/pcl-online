param([string]$Root = (Split-Path -Parent $PSScriptRoot), [string]$Executable = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
if (-not $Executable) { $Executable=Join-Path $Root 'output/CPT-Isolated/CPT.exe' }
$assembly = [Reflection.Assembly]::LoadFrom($Executable)
$app = [Activator]::CreateInstance($assembly.GetType('PCL.Application'),$true)
$app.InitializeComponent()
foreach ($name in 'MeloongCore','MeloongCore.Wpf','Newtonsoft.Json','PCLCS') { [Reflection.Assembly]::LoadFrom((Join-Path $Root "PCLCS/bin/$name.dll")) | Out-Null }
[MeloongCore.Main]::Init('CPT')
$paths = $assembly.GetType('PCL.ModDataPaths')
$base = $assembly.GetType('PCL.ModBase')
$temp = Join-Path $env:TEMP ('cpt-isolation-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$encoding = [Text.UTF8Encoding]::new($false)
try {
    $game = Join-Path $temp 'game'
    $legacy = Join-Path $game 'versions/test/PCL'
    $target = Join-Path $game 'versions/test/CPT'
    New-Item -ItemType Directory -Path $legacy -Force | Out-Null
    $original="VersionArgumentIndie:1`r`nLogo:PCL\Logo.png`r`nCustomInfo:upstream`r`n"
    [IO.File]::WriteAllText((Join-Path $legacy 'Setup.ini'),$original,$encoding)
    [IO.File]::WriteAllText((Join-Path $legacy 'config.json'),'{"keep":true}',$encoding)
    [IO.File]::WriteAllBytes((Join-Path $legacy 'Logo.png'),[byte[]](1,2,3))
    $instance = $paths.GetMethod('MigrateInstanceData')
    $instance.Invoke($null,@([string](Join-Path $game 'versions/test'))) | Out-Null
    if ([IO.File]::ReadAllText((Join-Path $legacy 'Setup.ini')) -ne $original) { throw 'PCL instance settings modified.' }
    $setup=Join-Path $target 'Setup.ini'
    if (-not [IO.File]::ReadAllText($setup).Contains('CPT\Logo.png')) { throw 'Migrated icon path not isolated.' }
    $base.GetMethod('WriteIni').Invoke($null,@([string]$setup,'CustomInfo','cpt-only')) | Out-Null
    if ([IO.File]::ReadAllText((Join-Path $legacy 'Setup.ini')) -ne $original) { throw 'CPT write reached PCL settings.' }
    $instance.Invoke($null,@([string](Join-Path $game 'versions/test'))) | Out-Null
    if (-not [IO.File]::ReadAllText($setup).Contains('cpt-only')) { throw 'CPT settings overwritten by migration.' }
    $resolvedTarget=[IO.Path]::GetFullPath($target)
    if (-not $resolvedTarget.StartsWith([IO.Path]::GetFullPath($temp)+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Test cleanup escaped temporary workspace.' }
    Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    $instance.Invoke($null,@([string](Join-Path $game 'versions/test'))) | Out-Null
    if (Test-Path -LiteralPath $setup) { throw 'Cleared instance settings reimported PCL data.' }
    $legacyFolder = Join-Path $game 'PCL.ini'
    $newFolder = Join-Path $game 'CPT.ini'
    [IO.File]::WriteAllText($legacyFolder,"Version:test`r`n",$encoding)
    $import=$paths.GetMethod('ImportLegacyIni')
    $import.Invoke($null,@([string]$newFolder)) | Out-Null
    $base.GetMethod('WriteIni').Invoke($null,@([string]$newFolder,'Version','cpt-selection')) | Out-Null
    if (-not [IO.File]::ReadAllText($legacyFolder).Contains('Version:test')) { throw 'PCL folder selection changed.' }
    Remove-Item -LiteralPath $newFolder
    $import.Invoke($null,@([string]$newFolder)) | Out-Null
    if (Test-Path -LiteralPath $newFolder) { throw 'Deleted CPT settings reimported upstream state.' }
    $clean=Join-Path $temp 'stock-pcl'
    New-Item -ItemType Directory -Path (Join-Path $clean 'PCL') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $clean 'PCL/Setup.ini'),'UiLogoText:upstream',$encoding)
    $paths.GetMethod('MigrateLauncherData').Invoke($null,@([string]$clean)) | Out-Null
    if (Test-Path -LiteralPath (Join-Path $clean 'CPT/Setup.ini')) { throw 'Stock PCL launcher settings auto-imported.' }
    $previous=Join-Path $temp 'previous-cpt'
    New-Item -ItemType Directory -Path (Join-Path $previous 'PCL'),(Join-Path $previous 'CPT') -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $previous 'PCL/accounts.dat'),[byte[]](4,5,6))
    [IO.File]::WriteAllText((Join-Path $previous 'PCL/Setup.ini'),'UiLogoText:old',$encoding)
    [IO.File]::WriteAllText((Join-Path $previous 'CPT/Setup.ini'),'UiLogoText:new',$encoding)
    $paths.GetMethod('MigrateLauncherData').Invoke($null,@([string]$previous)) | Out-Null
    if ([IO.File]::ReadAllText((Join-Path $previous 'CPT/Setup.ini')) -ne 'UiLogoText:new') { throw 'Existing CPT launcher settings overwritten.' }
    if (-not (Test-Path -LiteralPath (Join-Path $previous 'PCL/accounts.dat')) -or -not (Test-Path -LiteralPath (Join-Path $previous 'CPT/accounts.dat'))) { throw 'Account migration deleted or lost legacy data.' }
    if ([MeloongCore.Paths]::BaseThenName -notmatch 'CPT\\$' -or [MeloongCore.Paths]::AppDataThenName -notmatch 'CPT\\$') { throw 'Core paths still use PCL.' }
    $accounts=$assembly.GetType('PCL.ModAccounts').GetField('StoragePath',[Reflection.BindingFlags]'NonPublic,Static').GetValue($null)
    if ($accounts -notmatch 'CPT\\accounts.dat$') { throw 'Account storage path not isolated.' }
    $text = [IO.File]::ReadAllText((Join-Path $Root 'Plain Craft Launcher 2/Pages/PageSelectLeft.xaml.vb'))
    if ($text.Contains('Path.Combine(Instance, "PCL")') -or $text.Contains('Delete(Target.Location & "PCL.ini")')) { throw 'CPT cleanup can delete PCL settings.' }
    Write-Output 'CPT local/instance settings, logs, account paths and non-destructive migration isolation checks passed.'
} finally {
    $resolved=[IO.Path]::GetFullPath($temp)
    if ($resolved.StartsWith([IO.Path]::GetFullPath($env:TEMP)+'\',[StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolved -Leaf).StartsWith('cpt-isolation-tests-')) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
