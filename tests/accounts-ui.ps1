param([string]$Root = (Split-Path -Parent $PSScriptRoot), [string]$Executable = '', [switch]$Online, [switch]$SkipMutex)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Security
if (-not $Executable) { $Executable = Join-Path $Root 'output/CPT/CPT.exe' }
$assembly = [Reflection.Assembly]::LoadFrom($Executable)
$app = [Activator]::CreateInstance($assembly.GetType('PCL.Application'),$true)
$app.InitializeComponent()
foreach ($name in 'MeloongCore','MeloongCore.Wpf','Newtonsoft.Json','PCLCS') {
    [Reflection.Assembly]::LoadFrom((Join-Path $Root "PCLCS/bin/$name.dll")) | Out-Null
}
[MeloongCore.Main]::Init('CPT')
[MeloongCore.Wpf.Main]::Init()
$mock = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($assembly.GetType('PCL.FormMain'))
$mock.GetType().GetField('_BtnExtraBack',[Reflection.BindingFlags]'NonPublic,Instance').SetValue($mock,[Activator]::CreateInstance($assembly.GetType('PCL.MyExtraButton')))
$assembly.GetType('PCL.ModMain').GetField('FrmMain').SetValue($null,$mock)
$module = $assembly.GetType('PCL.ModAccounts')
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$accountType = $assembly.GetType('PCL.GameAccount')
$accountList = [Activator]::CreateInstance([System.Collections.Generic.List``1].MakeGenericType($accountType))
$offline = [Activator]::CreateInstance($accountType)
$offline.Label = '测试离线账号'
$offline.UserName = 'Steve'
$third = [Activator]::CreateInstance($accountType)
$third.Label = '测试第三方账号'
$third.Type = [Enum]::ToObject($assembly.GetType('PCL.ModLaunch+McLoginType'),3)
$third.UserName = 'test@example.invalid'
$third.AuthServer = 'https://auth.example.invalid/api/yggdrasil'
$third.Password = 'fake-test-secret-not-real'
$accountList.Add($offline)
$accountList.Add($third)
$oldAccounts = $module.GetField('Entries',$flags).GetValue($null)
$oldStorage = $module.GetField('StoragePath',$flags).GetValue($null)
$storageDirectory = Join-Path $env:TEMP ('craftpilot-account-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $storageDirectory | Out-Null
$storage = Join-Path $storageDirectory 'accounts.dat'
$module.GetField('Entries',$flags).SetValue($null,$accountList)
$module.GetField('StoragePath',$flags).SetValue($null,$storage)
$settings = $assembly.GetType('PCL.Settings').GetField('Entries').GetValue($null)
$oldSettings = @{}
foreach ($key in @('DefaultGameAccount','ServerGameAccounts','PlayServers','ServerProfiles','LoginType','CacheAuthAccess','CacheAuthClient','CacheAuthUuid','CacheAuthName','CacheAuthUsername','CacheAuthPass','CacheNideAccess','CacheNideClient','CacheNideUuid','CacheNideName','CacheNideUsername','CacheNidePass')) {
    $oldSettings[$key] = $settings[$key].GetCache($null)
}
$settings['DefaultGameAccount'].Set($offline.Id,$null)
$settings['ServerGameAccounts'].Set('{}',$null)
$profiles = New-Object 'System.Collections.Generic.List[PCLCS.Server.ServerProfile]'
$managed = New-Object PCLCS.Server.ServerProfile
$managed.Deployed = $true
$managed.Name = '我的测试服务器'
$managed.Host = '127.0.0.1'
$profiles.Add($managed)
$settings['ServerProfiles'].Set([Newtonsoft.Json.JsonConvert]::SerializeObject($profiles),$null)
$settings['PlayServers'].Set('[]',$null)
$folder = Join-Path $Root 'output/playwright'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
function Capture($page,$name) {
    $window = New-Object Windows.Window
    $window.Width = 640; $window.Height = 650; $window.WindowStyle = 'None'
    $window.Left = -2000; $window.Top = -2000; $window.ShowInTaskbar = $false
    $window.Content = $page
    try {
        $window.Show(); $window.UpdateLayout()
        if ($name -eq 'minecraft-third-party-account.png') {
            $card = $page.FindName('EditorCard')
            $save = $page.FindName('BtnSave')
            $bottom = $save.TranslatePoint((New-Object Windows.Point(0,$save.ActualHeight)), $card).Y
            if ($bottom -gt $card.ActualHeight + 1) { throw 'Third-party save button is clipped by its card.' }
        }
        $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap(640,650,96,96,[Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($window)
        $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream = [IO.File]::Create((Join-Path $folder $name))
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
    } finally { $window.Close(); $window.Content = $null }
}
try {
    $resolved = $module.GetMethod('Resolve').Invoke($null,@([string]''))
    if ($resolved.UserName -ne 'Steve') { throw 'Default account was not resolved.' }
    $input = $third.Input()
    if ($input.BaseUrl -ne 'https://auth.example.invalid/api/yggdrasil/authserver') { throw 'Existing third-party API base is incorrect.' }
    $settings['LoginType'].Set($third.Type,$null)
    foreach ($key in $oldSettings.Keys | Where-Object { $_ -like 'CacheAuth*' -or $_ -like 'CacheNide*' }) { $settings[$key].Set('',$null) }
    $module.GetMethod('SelectForServer').Invoke($null,@($third.Id)) | Out-Null
    $module.GetMethod('PrepareLaunch').Invoke($null,@()) | Out-Null
    $login = $module.GetMethod('LoginInput').Invoke($null,@())
    if ($login.Password -ne 'fake-test-secret-not-real' -or $login.BaseUrl -ne $input.BaseUrl) { throw 'Per-server account did not override default.' }
    $settings['LoginType'].Set($offline.Type,$null)
    $module.GetMethod('PrepareLaunch').Invoke($null,@()) | Out-Null
    if ($module.GetMethod('LoginInput').Invoke($null,@()).UserName -ne 'Steve') { throw 'Default account did not restore for subsequent launch.' }
    $persistArguments = New-Object object[] 1
    $persistArguments[0] = $accountList
    $module.GetMethod('Persist',$flags).Invoke($null,$persistArguments) | Out-Null
    $encrypted = [IO.File]::ReadAllBytes($storage)
    if ([Text.Encoding]::UTF8.GetString($encrypted).Contains('fake-test-secret-not-real')) { throw 'Account secret was stored in plain text.' }
    $decrypted = [Security.Cryptography.ProtectedData]::Unprotect($encrypted,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
    if (-not [Text.Encoding]::UTF8.GetString($decrypted).Contains('fake-test-secret-not-real')) { throw 'DPAPI round trip failed.' }
    $module.GetField('Entries',$flags).SetValue($null,$null)
    if ($module.GetMethod('Accounts').Invoke($null,@()).Count -ne 2) { throw 'Encrypted account reload failed.' }
    $editor = [Activator]::CreateInstance($assembly.GetType('PCL.PageSetupAccounts'))
    $editor.ReloadAccounts()
    $editor.GetType().GetMethod('ShowAccount',[Reflection.BindingFlags]'NonPublic,Instance').Invoke($editor,@($offline)) | Out-Null
    Capture $editor 'minecraft-accounts.png'
    $editor.GetType().GetMethod('ShowAccount',[Reflection.BindingFlags]'NonPublic,Instance').Invoke($editor,@($third)) | Out-Null
    if ($editor.FindName('ThirdPartyPanel').Visibility -ne 'Visible') { throw 'Third-party credentials UI missing.' }
    if ($editor.FindName('Secret').Password -ne '') { throw 'Stored password was exposed in editor.' }
    Capture $editor 'minecraft-third-party-account.png'
    $little = [Activator]::CreateInstance($accountType)
    $little.Type = [Enum]::ToObject($assembly.GetType('PCL.ModLaunch+McLoginType'),3)
    $little.Provider = 'LittleSkin'
    $little.Label = '测试 LittleSkin 账号'
    $little.AuthServer = 'https://untrusted.example.invalid'
    $little.UserName = 'test@example.invalid'
    $little.Password = 'fake-littleskin-secret'
    if ($little.Input().BaseUrl -ne 'https://littleskin.cn/api/yggdrasil/authserver') { throw 'LittleSkin endpoint was not pinned.' }
    $editor.GetType().GetMethod('ShowAccount',[Reflection.BindingFlags]'NonPublic,Instance').Invoke($editor,@($little)) | Out-Null
    if ($editor.FindName('TypeList').SelectedIndex -ne 2 -or -not $editor.FindName('AuthText').IsReadOnly) { throw 'LittleSkin login option is missing.' }
    if ($editor.FindName('BtnThirdLogin').Visibility -ne 'Visible') { throw 'LittleSkin login verification is missing.' }
    Capture $editor 'minecraft-littleskin-account.png'
    $baseType = $assembly.GetType('PCL.ModBase')
    $mutexName = $baseType.GetField('InstanceMutexName').GetRawConstantValue()
    if ($mutexName -eq 'PCL_SingletonMutex') { throw 'CraftPilot still shares the PCL instance mutex.' }
    if (-not $SkipMutex) {
    $pclMutex = New-Object Threading.Mutex($true,'PCL_SingletonMutex')
    try {
        $startupArguments = New-Object object[] 1
        $startupArguments[0] = $null
        $assembly.GetType('PCL.Application').GetMethod('WaitingMutex',[Reflection.BindingFlags]'NonPublic,Static').Invoke($null,$startupArguments) | Out-Null
        if (-not $assembly.GetType('PCL.Application').GetField('PclMutex',[Reflection.BindingFlags]'NonPublic,Static').GetValue($null)) { throw 'Independent CraftPilot instance mutex not created.' }
    } finally { $pclMutex.Dispose() }
    }
    if ($baseType.GetField('PathTemp').GetValue($null) -notmatch 'CraftPilot') { throw 'Default cache is not isolated.' }
    if ($assembly.GetType('PCL.ModSecret').GetField('RegFolder').GetRawConstantValue() -ne 'CraftPilot') { throw 'Registry namespace is not isolated.' }
    if ($assembly.GetType('PCL.ModCommunity').GetField('WebsiteEndpoint').GetRawConstantValue() -ne 'https://craftpilot.tolike.cn') { throw 'Website API not connected.' }
    if ($Online) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $latest = [PCLCS.Server.CommunityClient]::Latest('https://craftpilot.tolike.cn','release').GetAwaiter().GetResult()
        Write-Output ('Live website update API passed; release present: ' + ($null -ne $latest))
    }
    $official = [Activator]::CreateInstance($accountType)
    $official.Type = [Enum]::ToObject($assembly.GetType('PCL.ModLaunch+McLoginType'),5)
    $official.Label = '测试官方账号'
    $official.UserName = 'OfficialTest'
    $official.RefreshToken = 'fake-test-refresh-token'
    $editor.GetType().GetMethod('ShowAccount',[Reflection.BindingFlags]'NonPublic,Instance').Invoke($editor,@($official)) | Out-Null
    if ($editor.FindName('BtnOfficial').Visibility -ne 'Visible' -or $editor.FindName('Credentials').Visibility -ne 'Collapsed') { throw 'Official account does not use the existing OAuth flow.' }
    if ($official.Input().OAuthRefreshToken -ne 'fake-test-refresh-token') { throw 'Official input refresh token is missing.' }
    Capture $editor 'minecraft-official-account.png'
    $nide = [Activator]::CreateInstance($accountType)
    $nide.Type = [Enum]::ToObject($assembly.GetType('PCL.ModLaunch+McLoginType'),2)
    $nide.AuthServer = 'test-server-id'
    $nide.UserName = 'nide@example.invalid'
    $nide.Password = 'fake-nide-secret'
    if ($nide.Input().BaseUrl -ne 'https://auth.mc-user.com:233/test-server-id/authserver') { throw 'Unified pass API was not reused correctly.' }
    $server = New-Object PCLCS.Server.PlayServer
    $server.Name = '测试游玩服'; $server.Address = '127.0.0.1:25565'
    $detail = [Activator]::CreateInstance($assembly.GetType('PCL.PagePlayServerDetail'))
    $detail.Bind($server)
    if ($detail.FindName('AccountChoice').Items.Count -ne 3) { throw 'Server account choices incomplete.' }
    if ($detail.FindName('BtnManage').Visibility -ne 'Collapsed') { throw 'Third-party server exposes SSH management.' }
    Capture $detail 'server-account-selection.png'
    $detail.Bind($server,$managed)
    if ($detail.FindName('BtnManage').Visibility -ne 'Visible') { throw 'My Server management shortcut missing.' }
    $list = [Activator]::CreateInstance($assembly.GetType('PCL.PagePlayServers'))
    if ($list.SavedServers().Count -ne 1) { throw 'My Servers not included in server list.' }
    $settings['DefaultGameAccount'].Set('deleted-account-id',$null)
    if ($module.GetMethod('Resolve').Invoke($null,@([string]''))) { throw 'Deleted default account silently selected another identity.' }
    Write-Output 'Default account, per-server override, third-party API reuse, DPAPI persistence and account UI tests passed.'
} finally {
    foreach ($key in $oldSettings.Keys) { $settings[$key].Set($oldSettings[$key],$null) }
    $module.GetField('Entries',$flags).SetValue($null,$oldAccounts)
    $module.GetField('StoragePath',$flags).SetValue($null,$oldStorage)
    $resolvedDirectory = [IO.Path]::GetFullPath($storageDirectory)
    if ($resolvedDirectory.StartsWith([IO.Path]::GetFullPath($env:TEMP) + '\',[StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolvedDirectory -Leaf).StartsWith('craftpilot-account-tests-')) {
        Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
    }
}
