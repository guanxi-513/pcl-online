param([string]$Root=(Split-Path -Parent $PSScriptRoot),[string]$Executable='')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
if(-not $Executable) { $Executable=Join-Path $Root 'output/CPT-Chinese-Config/CPT.exe' }
$assembly=[Reflection.Assembly]::LoadFrom($Executable)
$app=[Activator]::CreateInstance($assembly.GetType('PCL.Application'),$true);$app.InitializeComponent()
$app.ShutdownMode='OnExplicitShutdown'
[Windows.Application].GetField('_startupUri',[Reflection.BindingFlags]'NonPublic,Instance').SetValue($app,$null)
$startup=$app.GetType().GetMethod('Application_Startup',[Reflection.BindingFlags]'NonPublic,Instance')
$app.Remove_Startup([Delegate]::CreateDelegate([Windows.StartupEventHandler],$app,$startup))
foreach($name in 'MeloongCore','MeloongCore.Wpf','Newtonsoft.Json','PCLCS') { [Reflection.Assembly]::LoadFrom((Join-Path $Root "PCLCS/bin/$name.dll")) | Out-Null }
[MeloongCore.Main]::Init('CPT');[MeloongCore.Wpf.Main]::Init()
$mock=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($assembly.GetType('PCL.FormMain'))
$flags=[Reflection.BindingFlags]'NonPublic,Instance'
$mock.GetType().GetField('_BtnExtraBack',$flags).SetValue($mock,[Activator]::CreateInstance($assembly.GetType('PCL.MyExtraButton')))
$panMsg=New-Object Windows.Controls.Grid;$panMsg.Background=[Windows.Media.Brushes]::Transparent
$mock.GetType().GetField('_PanMsg',$flags).SetValue($mock,$panMsg)
$assembly.GetType('PCL.ModMain').GetField('FrmMain').SetValue($null,$mock)
$assembly.GetType('PCL.ModBase').GetField('UiThreadId',[Reflection.BindingFlags]'NonPublic,Static').SetValue($null,[Threading.Thread]::CurrentThread.ManagedThreadId)
$fixture=& py -3 -B (Join-Path $Root 'tests/test_server_properties.py') --fixture
if($LASTEXITCODE -ne 0) { throw 'Config fixture failed.' }
$data=[Newtonsoft.Json.Linq.JObject]::Parse(($fixture -join "`n"))
$page=[Activator]::CreateInstance($assembly.GetType('PCL.PageServer'))
$profile=[PCLCS.Server.ServerProfile]::new();$profile.Host='example.invalid';$profile.Deployed=$true
$page.OpenProfile($profile)
$type=$page.GetType()
$type.GetMethod('ApplySection',$flags).Invoke($page,@([int]3)) | Out-Null
if($page.FindName('BtnSaveBasic').IsEnabled -or $page.FindName('BtnSavePanelBottom').IsEnabled) { throw 'Config save enabled before reading actual fields.' }
$type.GetMethod('RenderConfigPanel',$flags).Invoke($page,[object[]]@(,$data)) | Out-Null
$editors=$type.GetField('ConfigEditors',$flags).GetValue($page)
$rows=$type.GetField('ConfigRows',$flags).GetValue($page)
$definitions=$type.GetField('ConfigDefinitions',$flags).GetValue($page)
$collect=$type.GetMethod('CollectConfigChanges',$flags)
function ChangeCount($value) { return $value.GetType().GetProperty('Count').GetValue($value,$null) }
$initial=$collect.Invoke($page,@())
if((ChangeCount $initial) -ne 0) { throw ('Reading fields already changes the English config: '+$initial.ToString()) }
if(-not ($editors['online-mode'] -is $assembly.GetType('PCL.MyCheckBox')) -or -not ($editors['difficulty'] -is $assembly.GetType('PCL.MyComboBox')) -or -not ($editors['server-port'] -is [Windows.Controls.TextBox])) { throw 'Controls do not match boolean/enum/input fields.' }
if($editors['rcon.password'].Text.Contains('fixture-secret')) { throw 'RCON password exposed.' }
$editors['online-mode'].Checked=$false
$editors['server-port'].Text='25567'
if(-not $page.FindName('ConfigPanelStatus').Text.Contains('2')) { throw 'Changed controls are not marked as unsaved.' }
$changes=$collect.Invoke($page,@())
if((ChangeCount $changes) -ne 2 -or $changes['online-mode'].ToString() -ne 'false' -or $changes['server-port'].ToString() -ne '25567') { throw 'Chinese controls do not produce exact English-key patch.' }
$page.FindName('ConfigSearch').Text=$definitions['online-mode']['label'].ToString()
if($rows['online-mode'].Visibility -ne 'Visible' -or $rows['server-port'].Visibility -ne 'Collapsed') { throw 'Chinese label lookup did not locate online-mode.' }
$page.FindName('ConfigSearch').Text='server-port'
if($rows['server-port'].Visibility -ne 'Visible' -or $rows['online-mode'].Visibility -ne 'Collapsed') { throw 'English key lookup did not locate the port.' }
if((ChangeCount ($collect.Invoke($page,@()))) -ne 2) { throw 'Filtering discarded edits.' }
$page.FindName('ConfigSearch').Text='does-not-exist'
if($rows.Values | Where-Object Visibility -eq 'Visible') { throw 'No-match filter leaves unrelated rows visible.' }
$page.FindName('ConfigSearch').Text=''
Add-Type -ReferencedAssemblies WindowsBase -TypeDefinition @'
using System;
using System.Windows.Threading;
public static class CptConfigPump {
    public static void Wait(int ms) {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer();timer.Interval=TimeSpan.FromMilliseconds(ms);
        timer.Tick += (sender,args) => { timer.Stop();frame.Continue=false; };timer.Start();Dispatcher.PushFrame(frame);
    }
}
'@
[Threading.SynchronizationContext]::SetSynchronizationContext([Windows.Threading.DispatcherSynchronizationContext]::new())
$save=$type.GetMethod('SaveBasicConfig',$flags)
$save.Invoke($page,[object[]]@($null,$null)) | Out-Null
if($panMsg.Children.Count -ne 1 -or [Windows.Interop.ComponentDispatcher]::IsThreadModal) { throw 'Offline-mode confirmation is missing or blocks the UI.' }
$panMsg.Children[0].GetType().GetMethod('Btn2_Click').Invoke($panMsg.Children[0],@()) | Out-Null
[CptConfigPump]::Wait(50)
if($type.GetField('Busy',$flags).GetValue($page) -or (ChangeCount ($collect.Invoke($page,@()))) -ne 2) { throw 'Cancelling offline mode submitted a save or discarded edits.' }
$panMsg.Children.Clear()
$save.Invoke($page,[object[]]@($null,$null)) | Out-Null
$panMsg.Children[0].GetType().GetMethod('Btn1_Click').Invoke($panMsg.Children[0],@()) | Out-Null
[CptConfigPump]::Wait(50)
$panMsg.Children.Clear()
$assembly.GetType('PCL.ModMain').GetMethod('MyMsgBoxTick').Invoke($null,@()) | Out-Null
if($panMsg.Children.Count -ne 1) { throw 'Save confirmation is missing after offline risk acknowledgment.' }
$panMsg.Children[0].GetType().GetMethod('Btn2_Click').Invoke($panMsg.Children[0],@()) | Out-Null
[CptConfigPump]::Wait(50)
if($type.GetField('Busy',$flags).GetValue($page) -or (ChangeCount ($collect.Invoke($page,@()))) -ne 2) { throw 'Cancelling the save submitted a remote operation or cleared edits.' }
$panMsg.Children.Clear()
$restart=$type.GetMethod('RestartServer',$flags)
$restart.Invoke($page,[object[]]@($null,$null)) | Out-Null
if($panMsg.Children.Count -ne 1) { throw 'Restart omitted offline risk confirmation for unsaved change.' }
$panMsg.Children[0].GetType().GetMethod('Btn1_Click').Invoke($panMsg.Children[0],@()) | Out-Null
[CptConfigPump]::Wait(50)
$panMsg.Children.Clear()
$assembly.GetType('PCL.ModMain').GetMethod('MyMsgBoxTick').Invoke($null,@()) | Out-Null
if($panMsg.Children.Count -ne 1) { throw 'Restart omitted save-and-restart confirmation.' }
$panMsg.Children[0].GetType().GetMethod('Btn2_Click').Invoke($panMsg.Children[0],@()) | Out-Null
[CptConfigPump]::Wait(50)
if($type.GetField('Busy',$flags).GetValue($page) -or (ChangeCount ($collect.Invoke($page,@()))) -ne 2) { throw 'Cancelled save-and-restart submitted an operation or discarded edits.' }
$panMsg.Children.Clear()
$type.GetMethod('StartServer',$flags).Invoke($page,[object[]]@($null,$null)) | Out-Null
if($type.GetField('Busy',$flags).GetValue($page) -or $page.FindName('ConfigPanelStatus').Text -ne $page.FindName('OperationStatus').Text -or (ChangeCount ($collect.Invoke($page,@()))) -ne 2) { throw 'Start silently ignored unsaved changes.' }
[Threading.SynchronizationContext]::SetSynchronizationContext($null)
function Capture($name,$width,$search) {
    $page.FindName('ConfigSearch').Text=$search
    $page.FindName('PanBack').ScrollToHome()
    $window=New-Object Windows.Window
    $window.Width=$width;$window.Height=680;$window.Left=-2000;$window.Top=-2000;$window.ShowInTaskbar=$false;$window.WindowStyle='None';$window.Content=$page
    try {
        $window.Show();[CptConfigPump]::Wait(80);$window.UpdateLayout()
        $page.FindName('ConfigPanelCard').TriggerForceResize();$page.InvalidateMeasure();$window.UpdateLayout()
        if(-not $editors['server-port'].IsEnabled -or $editors['server-port'].IsReadOnly -or -not $editors['server-port'].IsHitTestVisible) { throw 'Config input is not editable.' }
        foreach($entry in $rows.GetEnumerator()) {
            if($entry.Value.Visibility -ne 'Visible') { continue }
            $editor=$editors[$entry.Key];$point=$editor.TranslatePoint((New-Object Windows.Point($editor.ActualWidth,0)),$page)
            if($point.X -gt $width -or $editor.ActualWidth -le 0) { throw 'Config editor overflows the viewport.' }
        }
        $bitmap=New-Object Windows.Media.Imaging.RenderTargetBitmap($width,680,96,96,[Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($window)
        $encoder=New-Object Windows.Media.Imaging.PngBitmapEncoder;$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream=[IO.File]::Create((Join-Path $Root "output/playwright/$name"))
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
    } finally { $window.Close();$window.Content=$null }
}
Capture 'cpt-chinese-config.png' 655 ''
Capture 'cpt-chinese-config-port.png' 655 'server-port'
Capture 'cpt-chinese-config-online.png' 1000 $definitions['online-mode']['label'].ToString()
$raw=$page.FindName('ConfigText')
if($raw.GetType() -ne [Windows.Controls.TextBox] -or $raw.IsReadOnly -or -not $raw.IsEnabled -or -not $raw.IsHitTestVisible -or -not $raw.AcceptsReturn -or -not $raw.AcceptsTab) { throw 'Raw English configuration editor is not a standard editable multiline input.' }
$raw.Text='server-port=25565';$raw.CaretIndex=$raw.Text.Length;$raw.SelectedText="`nonline-mode=false"
if(-not $raw.Text.Contains('online-mode=false')) { throw 'Raw editor does not accept inserted text.' }
$settings=$assembly.GetType('PCL.Settings').GetField('Entries').GetValue($null)
$entry=$settings['ServerProfiles'];$oldCache=$entry.GetCache($null)
$registry=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\CraftPilot')
$hadProfiles=$registry.GetValueNames() -contains 'ServerProfiles'
$oldProfiles=$registry.GetValue('ServerProfiles');$oldKind=if($hadProfiles) { $registry.GetValueKind('ServerProfiles') } else { $null }
try {
    $sync=$type.GetMethod('SyncSavedConfigPort',$flags)
    $savedPort=[Newtonsoft.Json.Linq.JObject]::Parse('{"port":25567}')
    $sync.Invoke($page,[object[]]@(,$savedPort)) | Out-Null
    if($page.FindName('GamePort').Text -ne '25567' -or $page.FindName('RuntimePort').Text -ne '25567' -or -not $page.FindName('ManagementAddress').Text.Contains(':25567')) { throw 'Saved config port did not synchronize form/header.' }
    $entry.ValueCache.Clear()
    $persisted=@($assembly.GetType('PCL.ModServerTasks').GetMethod('Profiles').Invoke($null,@())) | Where-Object Id -eq $profile.Id
    if($persisted.Count -ne 1 -or $persisted.GamePort -ne 25567) { throw 'Saved config port did not survive settings cache reload.' }
    $reopened=[Activator]::CreateInstance($type);$reopened.OpenProfile($persisted)
    if($reopened.FindName('GamePort').Text -ne '25567' -or $reopened.FindName('RuntimePort').Text -ne '25567') { throw 'Reopened profile reverted the saved port.' }
    $actual=[Newtonsoft.Json.Linq.JObject]::Parse('{"core":"NeoForge","version":"1.21.2","loaderVersion":"21.2.1","port":25568}')
    $type.GetMethod('SyncStatusSettings',$flags).Invoke($reopened,[object[]]@(,$actual)) | Out-Null
    $read=$type.GetMethod('ReadProfile',$flags).Invoke($reopened,@())
    if($read.GamePort -ne 25568 -or $read.Core -ne 'NeoForge' -or $read.Version -ne '1.21.2' -or $read.LoaderVersion -ne '21.2.1') { throw 'Reading profile after status refresh restores stale runtime/port values.' }
    $type.GetMethod('SaveProfile',$flags).Invoke($reopened,[object[]]@($null,$null)) | Out-Null
    $entry.ValueCache.Clear()
    $persisted=@($assembly.GetType('PCL.ModServerTasks').GetMethod('Profiles').Invoke($null,@())) | Where-Object Id -eq $profile.Id
    if($persisted.GamePort -ne 25568 -or $persisted.Core -ne 'NeoForge' -or $persisted.Version -ne '1.21.2' -or $persisted.LoaderVersion -ne '21.2.1') { throw 'Connection save reverted remotely synchronized settings.' }
} finally {
    if($hadProfiles) { $registry.SetValue('ServerProfiles',$oldProfiles,$oldKind) } else { $registry.DeleteValue('ServerProfiles',$false) }
    $entry.ValueCache.Clear()
    if($null -ne $oldCache) { $entry.Set($oldCache,$null) }
    $registry.Dispose()
}
Write-Output 'Chinese config UI: bilingual search, exact changed keys, hidden credentials, cancel confirmations, retained edits, narrow/wide layout, persisted profile reload and status/save synchronization passed.'
