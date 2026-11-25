param([string]$Root = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
[Reflection.Assembly]::LoadFrom((Join-Path $Root 'PCLCS/bin/Newtonsoft.Json.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $Root 'PCLCS/bin/PCLCS.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $Root 'PCLCS/bin/Renci.SshNet.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $Root 'PCLCS/bin/BouncyCastle.Cryptography.dll')) | Out-Null
[PCLCS.Server.CommunityClient]::ValidateBaseUrl('http://127.0.0.1:3210') | Out-Null
try {
    [PCLCS.Server.CommunityClient]::ValidateBaseUrl('http://example.com') | Out-Null
    throw 'Expected HTTP validation failure.'
} catch [System.Management.Automation.MethodInvocationException] { }
try {
    [PCLCS.Server.CommunityClient]::ValidateDownload('https://pan.xunlei.com.evil.test/s/a') | Out-Null
    throw 'Expected share URL validation failure.'
} catch [System.Management.Automation.MethodInvocationException] { }
$profile = New-Object PCLCS.Server.ServerProfile
if ($profile.User -ne 'root') { throw 'New deployment does not default to root SSH user.' }
if ($profile.ElevationMethod -ne 'sudo') { throw 'Default Linux elevation is not sudo.' }
$rootCheck=[PCLCS.Server.ServerManager].GetMethod('RequireRoot',[Reflection.BindingFlags]'NonPublic,Static')
$rootCheck.Invoke($null,@('0')) | Out-Null
$rootCheck.Invoke($null,@("0`n")) | Out-Null
foreach($uid in '1000','','root','0 extra') {
    $rejected=$false
    try { $rootCheck.Invoke($null,@($uid)) | Out-Null } catch { $rejected=$true }
    if(-not $rejected) { throw 'Non-root deployment identity accepted.' }
}
$profile.UseSu=$true
$profile.RootPassword='not-a-real-root-password'
$serialized=[Newtonsoft.Json.JsonConvert]::SerializeObject($profile)
if($serialized.Contains('not-a-real-root-password') -or $serialized.Contains('RootPassword')) { throw 'Root password leaks into profile serialization.' }
$restored=[Newtonsoft.Json.JsonConvert]::DeserializeObject($serialized,[PCLCS.Server.ServerProfile])
if(-not $restored.UseSu -or $restored.RootPassword -ne '') { throw 'Root elevation mode/secret isolation failed.' }
$shellType=[PCLCS.Server.ServerManager].Assembly.GetType('PCLCS.Server.SuRootShell')
$quote=$shellType.GetMethod('Quote',[Reflection.BindingFlags]'NonPublic,Static')
if($quote.Invoke($null,@("a'b; x")) -ne "'a'`"'`"'b; x'") { throw 'Su shell quoting failed.' }
$parse=$shellType.GetMethod('ParseOutput',[Reflection.BindingFlags]'NonPublic,Static')
$encodedResult=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('{"ok":true}'))
$result=$parse.Invoke($null,@("`r`n$encodedResult`r`nMARK_END:0`r`n",'MARK_END'))
if($result.Item1 -ne '{"ok":true}' -or $result.Item2 -ne 0) { throw 'Su terminal output parsing failed.' }
foreach($invalid in "data`nMARK_END:AUTH`n","data`nOTHER_END:0`n",'unmarked',"not-base64!`nMARK_END:0`n") {
    $rejected=$false
    try { $parse.Invoke($null,@($invalid,'MARK_END')) | Out-Null } catch { $rejected=$true }
    if(-not $rejected) { throw 'Unverified su result accepted.' }
}
$agentParse=[PCLCS.Server.ServerManager].GetMethod('ParseAgentResponse',[Reflection.BindingFlags]'NonPublic,Static')
$agentParse.Invoke($null,@('{"ok":true,"result":{}}')) | Out-Null
$eventType=[PCLCS.Server.ServerManager].Assembly.GetType('PCLCS.Server.DeploymentEvents')
$eventParse=$eventType.GetMethod('Parse',[Reflection.BindingFlags]'NonPublic,Static')
$event=$eventParse.Invoke($null,@('{"kind":"download","message":"Java","bytes":50,"total":100,"speed":20}'))
if($event.Bytes -ne 50 -or $event.Total -ne 100) { throw 'Deployment event byte precision lost.' }
$control=[PCLCS.Server.DeploymentControl]::new()
$control.Pause()
if(-not $control.PauseRequested -or $control.IsPaused) { throw 'Pause shown before checkpoint acknowledgment.' }
$control.Acknowledge($true)
if(-not $control.IsPaused) { throw 'Pause acknowledgment missing.' }
$control.Resume()
if($control.PauseRequested -or $control.IsPaused) { throw 'Resume did not clear pause state.' }
$control.Finish();$control.Pause()
if(-not $control.IsFinished -or $control.PauseRequested) { throw 'Finished task can still pause.' }
foreach($invalid in '{"kind":"exec","message":"bad"}','{"kind":"download","bytes":-1}','not json') {
    $rejected=$false
    try { $eventParse.Invoke($null,@($invalid)) | Out-Null } catch { $rejected=$true }
    if(-not $rejected) { throw 'Malformed deployment event accepted.' }
}
foreach($invalid in '/home/user','Login banner {"ok":true}','{"ok":true} garbage','{"result":{}}') {
    $rejected=$false
    try { $agentParse.Invoke($null,@($invalid)) | Out-Null } catch {
        $rejected=$true
        if($_.Exception.Message.Contains('Error parsing comment')) { throw 'Raw JSON parser error leaked.' }
    }
    if(-not $rejected) { throw 'Invalid agent response accepted.' }
}
$profile.Host = 'mc.example.com'
$profile.User = 'minecraft'
$profile.GamePort = 25566
if ([PCLCS.Server.ServerManager]::ValidateGameAddress($profile) -ne 'mc.example.com:25566') { throw 'Invalid game address.' }
$profile.GameAddress = '[::1]:25565'
if ([PCLCS.Server.ServerManager]::ValidateGameAddress($profile) -ne '[::1]:25565') { throw 'Invalid IPv6 address.' }
$encoded = [PCLCS.Server.ServerManager]::PowerShell('Write-Output test')
if (-not $encoded.StartsWith('powershell.exe -NoProfile -NonInteractive -EncodedCommand ')) { throw 'Invalid remote PowerShell command.' }
$resource = [PCLCS.Server.ServerManager].Assembly.GetManifestResourceStream('PCLCS.Server.agent.py')
if ($null -eq $resource) { throw 'Deployment agent was not embedded.' }
$resource.Dispose()
$network = [PCLCS.Server.ServerManager].Assembly.GetManifestResourceStream('PCLCS.Server.network.py')
if ($null -eq $network) { throw 'Remote network diagnostics was not embedded.' }
$network.Dispose()
$properties = [PCLCS.Server.ServerManager].Assembly.GetManifestResourceStream('PCLCS.Server.server_properties.py')
if ($null -eq $properties) { throw 'Chinese properties panel module was not embedded.' }
$properties.Dispose()
$ssh = New-Object Renci.SshNet.SshClient('localhost', 'unused', 'unused')
$ssh.Dispose()
Write-Output 'Core runtime and embedded-resource smoke tests passed.'
