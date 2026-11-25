param([string]$Root=(Split-Path -Parent $PSScriptRoot),[string]$Endpoint,[int]$TargetPort)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Net.Http,System.Security
[Reflection.Assembly]::LoadFrom((Join-Path $Root 'PCLCS/bin/Newtonsoft.Json.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $Root 'PCLCS/bin/PCLCS.dll')) | Out-Null
$client=New-Object PCLCS.Server.RoomClient($Endpoint)
try {
    $client.Login('host@example.invalid','LocalPass123').GetAwaiter().GetResult()
    $client.AllocateAddress('client-world').GetAwaiter().GetResult() | Out-Null
    $client.Enter($true,'',$TargetPort,'Protocol test',$false,'Room123','').GetAwaiter().GetResult()
    $client.PublishAddress('client-world').GetAwaiter().GetResult() | Out-Null
    Write-Output 'READY'
    [Console]::ReadLine() | Out-Null
    $client.UnpublishAddress('client-world').GetAwaiter().GetResult() | Out-Null
    $client.ReleaseAddress('client-world').GetAwaiter().GetResult()
    $client.Leave().GetAwaiter().GetResult()
} finally { $client.Dispose() }
