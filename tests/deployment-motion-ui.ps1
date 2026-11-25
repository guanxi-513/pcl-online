param([string]$Root=(Split-Path -Parent $PSScriptRoot),[string]$Executable='')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
if(-not $Executable) { $Executable=Join-Path $Root 'output/CPT-Live-Deployment/CPT.exe' }
$assembly=[Reflection.Assembly]::LoadFrom($Executable)
$app=[Activator]::CreateInstance($assembly.GetType('PCL.Application'),$true);$app.InitializeComponent()
$app.ShutdownMode='OnExplicitShutdown'
[Windows.Application].GetField('_startupUri',[Reflection.BindingFlags]'NonPublic,Instance').SetValue($app,$null)
$startup=$app.GetType().GetMethod('Application_Startup',[Reflection.BindingFlags]'NonPublic,Instance')
$app.Remove_Startup([Delegate]::CreateDelegate([Windows.StartupEventHandler],$app,$startup))
foreach($name in 'MeloongCore','MeloongCore.Wpf','Newtonsoft.Json','PCLCS') { [Reflection.Assembly]::LoadFrom((Join-Path $Root "PCLCS/bin/$name.dll")) | Out-Null }
[MeloongCore.Main]::Init('CPT');[MeloongCore.Wpf.Main]::Init()
$mock=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($assembly.GetType('PCL.FormMain'))
$mock.GetType().GetField('_BtnExtraBack',[Reflection.BindingFlags]'NonPublic,Instance').SetValue($mock,[Activator]::CreateInstance($assembly.GetType('PCL.MyExtraButton')))
$assembly.GetType('PCL.ModMain').GetField('FrmMain').SetValue($null,$mock)
Add-Type -ReferencedAssemblies WindowsBase -TypeDefinition @'
using System;
using System.Windows.Threading;
public static class CptMotionPump {
    public static void Wait(int ms) {
        var frame=new DispatcherFrame(); var timer=new DispatcherTimer();
        timer.Interval=TimeSpan.FromMilliseconds(ms);
        timer.Tick += (sender,args) => { timer.Stop();frame.Continue=false; };
        timer.Start();Dispatcher.PushFrame(frame);
    }
}
'@
$job=[Activator]::CreateInstance($assembly.GetType('PCL.ServerDeployment'))
$job.Profile=[PCLCS.Server.ServerProfile]::new();$job.Profile.Name='Motion test'
$loader=$assembly.GetType('PCL.ModLoader+LoaderTask`2').MakeGenericType([int],[Newtonsoft.Json.Linq.JObject])
$job.Loader=[Activator]::CreateInstance($loader)
$state=$assembly.GetType('PCL.ModBase+LoadState')
$job.Loader.State=[Enum]::Parse($state,'Loading');$job.Report('2/5 Preparing Java')
$page=[Activator]::CreateInstance($assembly.GetType('PCL.PageServerDeployRight'));$page.Bind($job)
$window=New-Object Windows.Window
$window.Width=830;$window.Height=600;$window.Left=-2000;$window.Top=-2000;$window.ShowInTaskbar=$false;$window.Content=$page
try {
    $window.Show();$window.UpdateLayout()
    $bar=$page.FindName('DetailProgress');$bar.ApplyTemplate() | Out-Null
    $waiting=$bar.Template.FindName('WaitingBand',$bar)
    $first=$waiting.Opacity;[CptMotionPump]::Wait(250);$second=$waiting.Opacity
    if([Windows.SystemParameters]::ClientAreaAnimation -and [Math]::Abs($first-$second) -lt 0.01) { throw 'Unknown-task motion is static.' }
    $update=[PCLCS.Server.DeploymentUpdate]::new();$update.Kind='download';$update.Message='Java';$update.Bytes=25;$update.Total=100
    $job.UpdateRemote($update);$page.Refresh();[CptMotionPump]::Wait(300)
    if([Math]::Abs($bar.Value-25) -gt 0.1 -or $bar.IsIndeterminate) { throw 'Download animation did not settle on measured progress.' }
    $update.Bytes=75;$job.UpdateRemote($update);$page.Refresh()
    [CptMotionPump]::Wait(70)
    if([Windows.SystemParameters]::ClientAreaAnimation -and ($bar.Value -le 25 -or $bar.Value -ge 75)) { throw 'Progress did not interpolate smoothly.' }
    [CptMotionPump]::Wait(300)
    if([Math]::Abs($bar.Value-75) -gt 0.1) { throw 'Animated progress is not accurate.' }
    $page.GetType().GetMethod('PauseClick',[Reflection.BindingFlags]'NonPublic,Instance').Invoke($page,@()) | Out-Null
    if(-not $job.Control.PauseRequested -or $job.Control.IsPaused -or $page.FindName('BtnResume').Visibility -ne 'Visible') { throw 'Pause request is not distinct from acknowledged pause.' }
    $job.Control.Acknowledge($true);$page.Refresh()
    if($bar.IsIndeterminate -or [Math]::Abs($bar.Value-75) -gt 0.1) { throw 'Paused progress is not frozen.' }
    $page.GetType().GetMethod('ResumeClick',[Reflection.BindingFlags]'NonPublic,Instance').Invoke($page,@()) | Out-Null
    if($job.Control.PauseRequested -or $job.Control.IsPaused -or $page.FindName('BtnPause').Visibility -ne 'Visible') { throw 'Continue did not resume the same job.' }
    $terminal=[PCLCS.Server.DeploymentUpdate]::new();$terminal.Kind='terminal';$terminal.Message='password=fixture-secret'
    $job.UpdateRemote($terminal);$page.Refresh()
    if($page.FindName('LabTerminal').Text.Contains('fixture-secret')) { throw 'Terminal secret leaked.' }
    $job.Loader.Error=[Exception]::new('fixture failure');$job.Loader.State=[Enum]::Parse($state,'Failed');$page.Refresh()
    if($bar.IsIndeterminate -or $page.FindName('StageProgress').Value -ge 100) { throw 'Failure animation/status is incorrect.' }
    if($page.FindName('BtnPause').Visibility -ne 'Collapsed' -or $page.FindName('BtnResume').Visibility -ne 'Collapsed') { throw 'Finished/failed task still offers pause/resume.' }
    Write-Output 'Deployment motion: unknown-task pulse changes, smooth interpolation settles at exact bytes, redaction and failure states verified.'
} finally { $window.Close();$window.Content=$null }
