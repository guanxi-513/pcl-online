param([string]$Root=(Split-Path -Parent $PSScriptRoot),[int]$Port,[string]$Fingerprint,[string]$Scenario)
$ErrorActionPreference='Stop'
foreach($name in 'Newtonsoft.Json','PCLCS','Renci.SshNet','BouncyCastle.Cryptography') { [Reflection.Assembly]::LoadFrom((Join-Path $Root "PCLCS/bin/$name.dll")) | Out-Null }
$profile=[PCLCS.Server.ServerProfile]::new()
$profile.Host='127.0.0.1';$profile.SshPort=$Port;$profile.User='fixture';$profile.Fingerprint=$Fingerprint
$profile.RootPassword='fixture-root-not-real'
$profile.ElevationMethod=if($Scenario.StartsWith('sudo')) { 'sudo' } elseif($Scenario.StartsWith('auto')) { 'auto' } else { 'su' }
if($Scenario -in @('missing','sudo-missing','sudo-n','auto-n')) { $profile.RootPassword='' }
try {
    $system=[PCLCS.Server.ServerManager]::CheckConnection($profile,'fixture-ssh-not-real',$true)
    if($Scenario -in @('reject','missing','sudo-reject','sudo-missing')) { throw 'Unverified root credential passed.' }
    if($system -ne 'Linux' -or $profile.UseSu -ne ($Scenario -ne 'direct')) { throw 'Root detection is incorrect.' }
    if($Scenario -eq 'sudo-success' -and $profile.ActiveElevation -ne 'sudo') { throw 'Sudo mode was not retained.' }
    if($Scenario -in @('sudo-n','auto-n') -and $profile.ActiveElevation -ne 'sudo-n') { throw 'Passwordless sudo was not selected.' }
    if($Scenario -in @('success','agent-error','corrupt','sudo-success')) {
        $client=[Renci.SshNet.SshClient]::new('127.0.0.1',$Port,'fixture','fixture-ssh-not-real')
        try {
            $client.Connect()
            $type=[PCLCS.Server.ServerManager].Assembly.GetType('PCLCS.Server.SuRootShell')
            $method=$type.GetMethod('Run',[Reflection.BindingFlags]'NonPublic,Static')
            if($Scenario -in @('agent-error','corrupt')) {
                $rejected=$false
                try { $method.Invoke($null,@($client,'fixture-command',$profile.RootPassword,$false,$false)) | Out-Null } catch {
                    $failure=$_.Exception
                    while($failure.InnerException -and $failure -isnot [IO.InvalidDataException]) { $failure=$failure.InnerException }
                    if($Scenario -eq 'agent-error' -and $failure.Message -ne 'fixture deployment blocked') { throw }
                    if($Scenario -eq 'corrupt' -and $failure -isnot [IO.InvalidDataException]) { throw }
                    if($failure.Message.Contains('fixture-root-not-real')) { throw 'Credential leaked into root error.' }
                    $rejected=$true
                }
                if(-not $rejected) { throw 'Invalid root command output was accepted.' }
                Write-Output ('Root result fixture passed: '+$Scenario)
                return
            }
            $output=$method.Invoke($null,@($client,'fixture-command',$profile.RootPassword,$false,($Scenario -eq 'sudo-success')))
            if($output -ne '{"ok":true}') { throw 'PTY result parsing failed.' }
        } finally { $client.Dispose() }
    }
} catch {
    $failure=$_.Exception
    while($failure.InnerException) { $failure=$failure.InnerException }
    if($Scenario -in @('missing','sudo-missing')) {
        if($failure -isnot [PCLCS.Server.RootPrivilegeRequiredException]) { throw }
        if($Scenario -eq 'sudo-missing' -and $failure.Method -ne 'sudo') { throw 'Wrong password type requested.' }
    } elseif($Scenario -notin @('reject','sudo-reject') -or -not $failure.Message.Contains('su') -or $failure.Message.Contains('fixture-root-not-real')) { throw }
}
Write-Output ('Root SSH fixture passed: '+$Scenario)
