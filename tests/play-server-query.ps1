param([string]$Root, [int]$Port, [string]$Address = '', [int]$DnsPort = 0)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $Root 'output/CPT-Status-Users/CPT.exe'))
$app = [Activator]::CreateInstance($assembly.GetType('PCL.Application'), $true)
$app.InitializeComponent()
foreach ($name in 'Newtonsoft.Json','System.Buffers','DnsClient','PCLCS') {
    [Reflection.Assembly]::LoadFrom((Join-Path $Root "PCLCS/bin/$name.dll")) | Out-Null
}
$server = New-Object PCLCS.Server.PlayServer
$server.Address = "127.0.0.1:$Port"
if ($Address) { $server.Address=$Address }
if ($DnsPort) {
    $resolver=[DnsClient.LookupClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback,$DnsPort))
    $result=[PCLCS.Server.PlayServer]::Query($server,$resolver)
} else { $result = [PCLCS.Server.PlayServer]::Query($server) }
Write-Output $result.ToString([Newtonsoft.Json.Formatting]::None)
