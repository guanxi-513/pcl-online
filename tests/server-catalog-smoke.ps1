param([string]$Root = (Split-Path -Parent $PSScriptRoot), [string]$Executable = '', [switch]$Online)
$ErrorActionPreference = 'Stop'
if (-not $Executable) { $Executable = Join-Path $Root 'output/CPT/CPT.exe' }
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$assembly = [Reflection.Assembly]::LoadFrom($Executable)
$app = [Activator]::CreateInstance($assembly.GetType('PCL.Application'), $true)
$app.InitializeComponent()
[Reflection.Assembly]::LoadFrom((Join-Path $Root 'PCLCS/bin/MeloongCore.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $Root 'PCLCS/bin/MeloongCore.Wpf.dll')) | Out-Null
[MeloongCore.Main]::Init('PCL')
[MeloongCore.Wpf.Main]::Init()
[Reflection.Assembly]::LoadFrom((Join-Path $Root 'PCLCS/bin/Newtonsoft.Json.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $Root 'PCLCS/bin/PCLCS.dll')) | Out-Null
$choice = [Activator]::CreateInstance($assembly.GetType('PCL.ServerVersionChoice'))
$choice.Kind = 'minecraft'
$choice.Id = '1.21.1'
$choice.Data = [Newtonsoft.Json.Linq.JObject]::Parse('{"id":"1.21.1","type":"release","url":"https://example.com/version.json","releaseTime":"2024-08-08T12:00:00Z"}')
if (-not $choice.Stable) { throw 'Release was not classified as stable.' }
$choice.Data['type'] = [Newtonsoft.Json.Linq.JValue]::new('snapshot')
if ($choice.Stable) { throw 'Snapshot was classified as stable.' }
$choice.Kind = 'Fabric'
$choice.Id = '0.16.10'
$choice.Data = [Newtonsoft.Json.Linq.JObject]::Parse('{"version":"0.16.10","stable":true}')
if (-not $choice.Stable) { throw 'Fabric stability was not retained.' }
if ($Online) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $result = [PCLCS.Server.ResourceCatalog]::Search('veinminer', 'plugins', 'Paper', '1.21.1').GetAwaiter().GetResult()
    if ($result.Count -eq 0) { throw 'No compatible server plugins found.' }
    $file = [PCLCS.Server.ResourceCatalog]::Resolve($result[0]['project_id'].ToString(), 'Paper', '1.21.1').GetAwaiter().GetResult()
    if (-not $file['url'].ToString().StartsWith('https://')) { throw 'Insecure artifact URL.' }
    if ($file['sha512'].ToString().Length -ne 128) { throw 'Artifact checksum was not returned.' }
    Write-Output "Live Modrinth search and server artifact resolution passed ($($result.Count) results)."
}
Write-Output 'PCL server catalog smoke tests passed.'
