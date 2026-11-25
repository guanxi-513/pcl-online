param([string]$Root=(Split-Path -Parent $PSScriptRoot),[string]$Endpoint)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Net.Http,System.Security
[Reflection.Assembly]::LoadFrom((Join-Path $Root 'PCLCS/bin/Newtonsoft.Json.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $Root 'PCLCS/bin/PCLCS.dll')) | Out-Null
Add-Type -TypeDefinition @'
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
public sealed class CptEcho : IDisposable {
    private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
    public CptEcho() {
        listener.Start();
        Task.Run(async () => {
            try {
                using (var client = await listener.AcceptTcpClientAsync()) {
                    var stream = client.GetStream(); var buffer = new byte[1024];
                    int count;
                    while ((count = await stream.ReadAsync(buffer,0,buffer.Length)) > 0) await stream.WriteAsync(buffer,0,count);
                }
            } catch { }
        });
    }
    public void Dispose() { listener.Stop(); }
}
'@
$echo=New-Object CptEcho
$hostClient=New-Object PCLCS.Server.RoomClient($Endpoint)
$guestClient=New-Object PCLCS.Server.RoomClient($Endpoint)
$restored=New-Object PCLCS.Server.RoomClient($Endpoint)
$session=Join-Path $env:TEMP ('cpt-room-session-test-'+[Guid]::NewGuid().ToString('N')+'.dat')
try {
    $hostClient.Login('host@example.invalid','LocalPass123').GetAwaiter().GetResult()
    $guestClient.Login('guest@example.invalid','LocalPass123').GetAwaiter().GetResult()
    $guestClient.SaveSession($session)
    if ([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($session)).Contains($guestClient.Token)) { throw 'Session token not encrypted.' }
    $restored.Restore($session).GetAwaiter().GetResult()
    if (-not $restored.User) { throw 'Saved login could not restore.' }
    $hostClient.Enter($true,'',0,'Client test room',$true,'Room123',('127.0.0.1:'+$echo.Port)).GetAwaiter().GetResult()
    $hostCode=$hostClient.Code
    $hostTarget=$hostClient.TargetAddress
    $hostClient.RefreshRoom().GetAwaiter().GetResult()
    if(-not $hostClient.InRoom -or $hostClient.Code -ne $hostCode -or $hostClient.TargetAddress -ne $hostTarget) { throw 'Refreshing host room disrupted the connection.' }
    $lobby=$guestClient.Lobby('').GetAwaiter().GetResult()
    if ($lobby['rooms'].Count -ne 1) { throw 'Public room missing from lobby.' }
    $hostClient.UpdateRoom('Client test room',$false,$null,$false).GetAwaiter().GetResult()
    if ($guestClient.Lobby('').GetAwaiter().GetResult()['rooms'].Count -ne 0) { throw 'Private room leaked into public list.' }
    $found=$guestClient.Lobby((' '+$hostClient.Code.ToLowerInvariant()+' ')).GetAwaiter().GetResult()
    $searchResult=$found.ToString() | ConvertFrom-Json
    if ($searchResult.rooms.Count -ne 1 -or $searchResult.rooms[0].code -ne $hostClient.Code) { throw ('Private room search failed: '+$found.ToString()) }
    if ($guestClient.InRoom) { throw 'Search joined the room before confirmation.' }
    $missing=if($hostClient.Code.StartsWith('0')) { '1'+$hostClient.Code.Substring(1) } else { '0'+$hostClient.Code.Substring(1) }
    if ($guestClient.Lobby($missing).GetAwaiter().GetResult()['rooms'].Count -ne 0) { throw 'Unknown room search should be empty.' }
    $rejected=$false
    try { $guestClient.Lobby('bad').GetAwaiter().GetResult() | Out-Null } catch { $rejected=$true }
    if (-not $rejected) { throw 'Invalid room number was accepted.' }
    $hostClient.UpdateRoom('Client test room',$true,$null,$false).GetAwaiter().GetResult()
    if ($guestClient.Lobby('').GetAwaiter().GetResult()['rooms'].Count -ne 1) { throw 'Cleared room search did not restore public list.' }
    $share=$hostClient.ShareLink
    if (-not $share.Contains('/room#')) { throw 'Invite link missing.' }
    $hostClient.UpdateRoom('Client test room',$true,$null,$true).GetAwaiter().GetResult()
    if ($hostClient.ShareLink -eq $share) { throw 'Invite rotation failed.' }
    $testInvite=$hostClient.ShareLink.Replace('http://127.0.0.1:3210',$Endpoint)
    $guestClient.Enter($false,$testInvite,0,'',$false,'','').GetAwaiter().GetResult()
    if (-not $guestClient.InRoom -or $guestClient.LocalPort -le 0) { throw 'Guest did not bind loopback.' }
    $guestPort=$guestClient.LocalPort
    $hostClient.UpdateRoom('Refreshed room title',$true,$null,$true).GetAwaiter().GetResult()
    $guestClient.RefreshRoom().GetAwaiter().GetResult()
    $hostClient.RefreshRoom().GetAwaiter().GetResult()
    if($guestClient.RoomInfo['title'].ToString() -ne 'Refreshed room title' -or $guestClient.ShareLink -ne '' -or $guestClient.LocalPort -ne $guestPort -or -not $guestClient.InRoom) { throw 'Guest refresh did not update metadata transparently.' }
    if($hostClient.ShareLink -ne $hostClient.RoomInfo['shareLink'].ToString()) { throw 'Host refresh did not update invitation.' }
    $tcp=New-Object Net.Sockets.TcpClient
    try {
        $tcp.Connect('127.0.0.1',$guestClient.LocalPort)
        $tcp.ReceiveTimeout=10000
        $stream=$tcp.GetStream()
        $payload=[Text.Encoding]::UTF8.GetBytes('minecraft-protocol-roundtrip')
        $stream.Write($payload,0,$payload.Length)
        $buffer=New-Object byte[] $payload.Length
        $received=0
        while ($received -lt $buffer.Length) { $count=$stream.Read($buffer,$received,$buffer.Length-$received); if ($count -eq 0) { throw 'Relay closed before echo.' }; $received+=$count }
        if ([Text.Encoding]::UTF8.GetString($buffer) -ne 'minecraft-protocol-roundtrip') { throw 'Relay corrupted data.' }
    } finally { $tcp.Dispose() }
    $guestClient.Leave().GetAwaiter().GetResult()
    $hostClient.Leave().GetAwaiter().GetResult()
    if ($guestClient.InRoom -or $hostClient.InRoom) { throw 'Room not stopped.' }
    $guestClient.Logout().GetAwaiter().GetResult()
    Write-Output 'Actual .NET public/private room search, normalization, invalid/unknown search, public-list reset, invitation rotation, invite join, TCP echo and encrypted session checks passed.'
} finally {
    $hostClient.Dispose();$guestClient.Dispose();$restored.Dispose();$echo.Dispose()
    if (Test-Path -LiteralPath $session) { Remove-Item -LiteralPath $session -Force }
}
