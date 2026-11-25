param([string]$Root=(Split-Path -Parent $PSScriptRoot),[string]$Executable='')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
if(-not $Executable) { $Executable=Join-Path $Root 'output/CPT-Network-Dialog-Fix/CPT.exe' }
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
$pan=New-Object Windows.Controls.Grid;$pan.Background=[Windows.Media.Brushes]::Transparent
$mock.GetType().GetField('_PanMsg',$flags).SetValue($mock,$pan)
$main=$assembly.GetType('PCL.ModMain');$main.GetField('FrmMain').SetValue($null,$mock)
$assembly.GetType('PCL.ModBase').GetField('UiThreadId',[Reflection.BindingFlags]'NonPublic,Static').SetValue($null,[Threading.Thread]::CurrentThread.ManagedThreadId)
[Threading.SynchronizationContext]::SetSynchronizationContext([Windows.Threading.DispatcherSynchronizationContext]::new())
Add-Type -ReferencedAssemblies @('WindowsBase','PresentationFramework','PresentationCore','System.Xaml') -TypeDefinition @'
using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Controls;
public static class CptNetworkDialogTest {
    public static int Heartbeats;
    public static void Pump(Task task) {
        var timer = new DispatcherTimer(); var frame = new DispatcherFrame();
        var until = DateTime.UtcNow.AddSeconds(5);
        timer.Interval=TimeSpan.FromMilliseconds(10);
        timer.Tick += (sender,args) => { Heartbeats++; if(task.IsCompleted || DateTime.UtcNow>until) frame.Continue=false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if(!task.IsCompleted) throw new TimeoutException("Dialog task did not complete.");
        task.GetAwaiter().GetResult();
    }
    public static void Tick(Action action) {
        var timer = new DispatcherTimer();
        timer.Interval=TimeSpan.FromMilliseconds(30);
        timer.Tick += (sender,args) => { timer.Stop(); action(); };
        timer.Start();
    }
    public static int Synchronous(MethodInfo method, Grid pan, MethodInfo tick) {
        Tick(() => pan.Children[0].GetType().GetMethod("Btn2_Click").Invoke(pan.Children[0],null));
        return (int)method.Invoke(null,new object[] { "fixture", "fixture", "OK", "Cancel", "", false, true, false, null, null, null });
    }
    public static object Cancel(MethodInfo method, object[] arguments, Grid pan) {
        Tick(() => pan.Children[0].GetType().GetMethod("Btn2_Click").Invoke(pan.Children[0],null));
        return method.Invoke(null,arguments);
    }
}
'@
$async=$main.GetMethod('MyMsgBoxAsync')
$tick=$main.GetMethod('MyMsgBoxTick')
$page=[Activator]::CreateInstance($assembly.GetType('PCL.PageServer'))
$report=$page.GetType().GetMethod('HandleNetworkReport',$flags)
function CloseDialog($choice) {
    $dialog=$pan.Children[0]
    $dialog.GetType().GetMethod(('Btn'+$choice+'_Click')).Invoke($dialog,@()) | Out-Null
}
try {
    foreach($choice in 1,2,1,2) {
        $pan.Children.Clear()
        $task=$async.Invoke($null,@('fixture DNS consent','fixture','Repair','Cancel',$true))
        if($task.IsCompleted -or $pan.Children.Count -ne 1 -or [Windows.Interop.ComponentDispatcher]::IsThreadModal) { throw 'Async repair prompt blocks the dispatcher or auto-confirms.' }
        $before=[CptNetworkDialogTest]::Heartbeats
        [CptNetworkDialogTest]::Tick([Action]{ CloseDialog $choice })
        [CptNetworkDialogTest]::Pump($task)
        if($task.Result -ne $choice -or [CptNetworkDialogTest]::Heartbeats -le $before -or [Windows.Interop.ComponentDispatcher]::IsThreadModal) { throw 'Prompt completion/dispatcher responsiveness failed.' }
    }
    $pan.Children.Clear()
    $data=[Newtonsoft.Json.Linq.JObject]::Parse('{"ready":false,"canRepair":true,"message":"DNS fixture","adapter":"eth0"}')
    $task=$report.Invoke($page,[object[]]@($data,'Fabric',$false))
    if($pan.Children.Count -ne 1 -or $task.IsCompleted) { throw 'Network report did not wait for repair consent asynchronously.' }
    CloseDialog 2
    [CptNetworkDialogTest]::Pump($task)
    if($task.Result) { throw 'Cancelling DNS repair continued deployment.' }
    $pan.Children.Clear()
    $data=[Newtonsoft.Json.Linq.JObject]::Parse('{"ready":false,"canRepair":false,"message":"Offline fixture"}')
    $task=$report.Invoke($page,[object[]]@($data,'Fabric',$false))
    CloseDialog 1;[CptNetworkDialogTest]::Pump($task)
    if($task.Result) { throw 'Non-repairable network continued deployment.' }
    $pan.Children.Clear()
    $data=[Newtonsoft.Json.Linq.JObject]::Parse('{"ready":true,"canRepair":false,"message":"Ready fixture"}')
    $task=$report.Invoke($page,[object[]]@($data,'Fabric',$true))
    if($task.IsCompleted) { throw 'Network success acknowledgment did not wait asynchronously.' }
    CloseDialog 1;[CptNetworkDialogTest]::Pump($task)
    if(-not $task.Result) { throw 'Network success acknowledgment blocked the workflow.' }
    $pan.Children.Clear()
    $result=[CptNetworkDialogTest]::Synchronous($main.GetMethod('MyMsgBox'),$pan,$tick)
    if($result -ne 2 -or [Windows.Interop.ComponentDispatcher]::IsThreadModal) { throw 'Synchronous dialog modal balance regressed.' }
    $pan.Children.Clear()
    $result=[CptNetworkDialogTest]::Cancel($main.GetMethod('MyMsgBoxInput'),[object[]]@('fixture','fixture','', $null,'','OK','Cancel',$false,$true),$pan)
    if($null -ne $result -or [Windows.Interop.ComponentDispatcher]::IsThreadModal) { throw 'Password dialog cancellation left modal state active.' }
    $pan.Children.Clear()
    $choices=[Activator]::CreateInstance([System.Collections.Generic.List``1].MakeGenericType($assembly.GetType('PCL.IMyRadio')))
    $result=[CptNetworkDialogTest]::Cancel($main.GetMethod('MyMsgBoxSelect'),[object[]]@($choices,'fixture','OK','Cancel',$false),$pan)
    if($null -ne $result -or [Windows.Interop.ComponentDispatcher]::IsThreadModal) { throw 'Selection dialog cancellation left modal state active.' }
    Write-Output 'Network dialogs: async confirm/cancel and repeated prompts responsive, DNS cancellation stops workflow, offline acknowledgment completes, synchronous modal balance preserved. No remote DNS changes.'
} finally { $pan.Children.Clear();[Threading.SynchronizationContext]::SetSynchronizationContext($null) }
