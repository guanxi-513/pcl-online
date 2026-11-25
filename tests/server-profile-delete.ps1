param([string]$Root=(Split-Path -Parent $PSScriptRoot),[string]$Executable='')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
if(-not $Executable) { $Executable=Join-Path $Root 'output/CPT-Server-List-Actions/CPT.exe' }
$assembly=[Reflection.Assembly]::LoadFrom($Executable)
$app=[Activator]::CreateInstance($assembly.GetType('PCL.Application'),$true);$app.InitializeComponent()
foreach($name in 'MeloongCore','MeloongCore.Wpf','Newtonsoft.Json','PCLCS') { [Reflection.Assembly]::LoadFrom((Join-Path $Root "PCLCS/bin/$name.dll")) | Out-Null }
[MeloongCore.Main]::Init('CPT');[MeloongCore.Wpf.Main]::Init()
$settings=$assembly.GetType('PCL.Settings').GetField('Entries').GetValue($null)
$module=$assembly.GetType('PCL.ModServerTasks')
$registry=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\CraftPilot')
$backup=@{};$cache=@{}
foreach($name in 'ServerProfiles','ServerGameAccounts') {
    $cache[$name]=$settings[$name].GetCache($null)
    if($registry.GetValueNames() -contains $name) { $backup[$name]=@($registry.GetValue($name),$registry.GetValueKind($name)) }
}
$deployments=$module.GetField('Deployments').GetValue($null)
$profile=[PCLCS.Server.ServerProfile]::new();$profile.Name='Delete test';$profile.Host='example.invalid'
$keep=[PCLCS.Server.ServerProfile]::new();$keep.Name='Keep test';$keep.Host='example.invalid'
$selected=$assembly.GetType('PCL.ModServerClient').GetField('SelectedServerId')
$oldSelected=$selected.GetValue($null)
$job=[Activator]::CreateInstance($assembly.GetType('PCL.ServerDeployment'));$job.Profile=$profile
$loaderType=$assembly.GetType('PCL.ModLoader+LoaderTask`2').MakeGenericType([int],[Newtonsoft.Json.Linq.JObject])
$job.Loader=[Activator]::CreateInstance($loaderType)
$state=$assembly.GetType('PCL.ModBase+LoadState')
$job.Loader.State=[Enum]::Parse($state,'Loading')
$deployments[$profile.Id]=$job
try {
    $profiles=[Newtonsoft.Json.JsonConvert]::SerializeObject([PCLCS.Server.ServerProfile[]]@($profile,$keep))
    $settings['ServerProfiles'].Set($profiles,$null)
    $bindings=[Newtonsoft.Json.Linq.JObject]::Parse('{}')
    $bindings['managed:'+$profile.Id]=[Newtonsoft.Json.Linq.JValue]::new('account-to-unbind');$bindings['managed:'+$keep.Id]=[Newtonsoft.Json.Linq.JValue]::new('account-to-keep')
    $settings['ServerGameAccounts'].Set($bindings.ToString(),$null)
    $selected.SetValue($null,$profile.Id)
    $rejected=$false
    try { $module.GetMethod('DeleteProfile').Invoke($null,@($profile.Id)) | Out-Null } catch { $rejected=$true }
    if(-not $rejected -or $module.GetMethod('Profiles').Invoke($null,@()).Count -ne 2) { throw 'Active deployment deletion was not blocked.' }
    $job.Loader.State=[Enum]::Parse($state,'Finished')
    $module.GetMethod('DeleteProfile').Invoke($null,@($profile.Id)) | Out-Null
    $remaining=$module.GetMethod('Profiles').Invoke($null,@())
    if($remaining.Count -ne 1 -or $remaining[0].Id -ne $keep.Id -or $deployments.ContainsKey($profile.Id)) { throw 'Profile deletion did not preserve unrelated records/clear completed task.' }
    $saved=[Newtonsoft.Json.Linq.JObject]::Parse($settings['ServerGameAccounts'].GetCache($null))
    if($saved['managed:'+$profile.Id] -or $saved['managed:'+$keep.Id].ToString() -ne 'account-to-keep' -or $selected.GetValue($null)) { throw 'Deleted server binding/selection was not cleared correctly.' }
    if($registry.GetValue('ServerProfiles') -notmatch $keep.Id -or $registry.GetValue('ServerProfiles') -match $profile.Id) { throw 'Deletion was not persisted.' }
    $module.GetMethod('DeleteProfile').Invoke($null,@($keep.Id)) | Out-Null
    if($module.GetMethod('Profiles').Invoke($null,@()).Count -ne 0) { throw 'Last profile deletion did not yield empty state.' }
    Write-Output 'Delete profile: active deployment refused, local record persisted, unrelated record/binding retained, task and selection cleared, last record removed. No SSH or filesystem deletion performed.'
} finally {
    $removed=$null;$deployments.TryRemove($profile.Id,[ref]$removed) | Out-Null
    foreach($name in 'ServerProfiles','ServerGameAccounts') {
        if($backup.ContainsKey($name)) { $registry.SetValue($name,$backup[$name][0],$backup[$name][1]) } else { $registry.DeleteValue($name,$false) }
        $settings[$name].Set($cache[$name],$null)
    }
    $registry.Dispose();$selected.SetValue($null,$oldSelected)
}
