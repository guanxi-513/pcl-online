param([string]$Root=(Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
[Reflection.Assembly]::LoadFrom((Join-Path $Root 'PCLCS/bin/Newtonsoft.Json.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $Root 'PCLCS/bin/PCLCS.dll')) | Out-Null
$website='https://craftpilot.tolike.cn'
$value='cpt://join#0123456789abcdef.'+('a'*64)
$expected=$website+'/room#0123456789ABCDEF.'+('a'*64)
if([PCLCS.Server.RoomInvitationActivation]::Parse($value,$website) -ne $expected) { throw 'Activation normalization failed.' }
if([PCLCS.Server.RoomInvitationActivation]::Parse($value.Replace('join#','join/#'),$website) -ne $expected) { throw 'Browser slash variation failed.' }
foreach($bad in @('https://other.invalid/room#0123456789ABCDEF.'+('a'*64),'cpt://evil#0123456789ABCDEF.'+('a'*64),$value+' --gpu x',$value+'?url=http://other.invalid','cpt://join#123.'+('a'*64),$value.Substring(0,$value.Length-1))) {
    $rejected=$false
    try { [PCLCS.Server.RoomInvitationActivation]::Parse($bad,$website) | Out-Null } catch { $rejected=$true }
    if(-not $rejected) { throw 'Unsafe activation was accepted.' }
}
Add-Type -ReferencedAssemblies (Join-Path $Root 'PCLCS/bin/PCLCS.dll') -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using PCLCS.Server;
public sealed class CptActivationTest : IDisposable {
    public readonly ConcurrentQueue<string> Received = new ConcurrentQueue<string>();
    public readonly RoomInvitationActivation Receiver;
    public CptActivationTest(string name, string website) {
        Receiver = new RoomInvitationActivation(name);
        Receiver.Start(website, link => Received.Enqueue(link));
    }
    public void Dispose() { Receiver.Dispose(); }
}
'@
$name='CPT.Activation.Test.'+[Guid]::NewGuid().ToString('N')
$receiver=[CptActivationTest]::new($name,$website)
$sender=[PCLCS.Server.RoomInvitationActivation]::new($name)
try {
    for($i=0;$i -lt 3;$i++) {
        if(-not $sender.Send($value,$website)) { throw 'Activation was not acknowledged by existing instance.' }
        $received=$null
        if(-not $receiver.Received.TryDequeue([ref]$received) -or $received -ne $expected) { throw 'Activation IPC corrupted the invitation.' }
    }
} finally { $sender.Dispose();$receiver.Dispose() }
Write-Output 'Strict activation parsing, browser URI normalization and three acknowledged same-user pipe deliveries passed.'
