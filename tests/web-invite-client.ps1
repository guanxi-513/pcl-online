param([string]$Root=(Split-Path -Parent $PSScriptRoot),[string]$Endpoint,[string]$Invite,[string]$Executable='')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Net.Http,System.Security
if(-not $Executable) { $Executable=Join-Path $Root 'output/CPT-Native-Refresh/CPT.exe' }
$assembly=[Reflection.Assembly]::LoadFrom($Executable)
$app=[Activator]::CreateInstance($assembly.GetType('PCL.Application'),$true);$app.InitializeComponent()
$app.ShutdownMode='OnExplicitShutdown'
[Windows.Application].GetField('_startupUri',[Reflection.BindingFlags]'NonPublic,Instance').SetValue($app,$null)
$startupMethod=$app.GetType().GetMethod('Application_Startup',[Reflection.BindingFlags]'NonPublic,Instance')
$app.Remove_Startup([Delegate]::CreateDelegate([Windows.StartupEventHandler],$app,$startupMethod))
$app.Add_DispatcherUnhandledException({ param($sender,$event) [Console]::WriteLine($event.Exception.ToString()); $event.Handled=$true })
foreach($name in 'MeloongCore','MeloongCore.Wpf','Newtonsoft.Json','PCLCS') { [Reflection.Assembly]::LoadFrom((Join-Path $Root "PCLCS/bin/$name.dll")) | Out-Null }
[MeloongCore.Main]::Init('CPT');[MeloongCore.Wpf.Main]::Init()
$mock=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($assembly.GetType('PCL.FormMain'))
$flags=[Reflection.BindingFlags]'NonPublic,Instance'
$mock.GetType().GetField('_BtnExtraBack',$flags).SetValue($mock,[Activator]::CreateInstance($assembly.GetType('PCL.MyExtraButton')))
$panMsg=New-Object Windows.Controls.Grid
$panMsg.Children.Add((New-Object Windows.Controls.Grid)) | Out-Null
$mock.GetType().GetField('_PanMsg',$flags).SetValue($mock,$panMsg)
$assembly.GetType('PCL.ModMain').GetField('FrmMain').SetValue($null,$mock)
Add-Type -ReferencedAssemblies @((Join-Path $Root 'PCLCS/bin/PCLCS.dll'),'WindowsBase') -TypeDefinition @'
using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using PCLCS.Server;
public static class CptInviteUiTest {
    public static Lazy<RoomClient> Client(RoomClient value) { return new Lazy<RoomClient>(() => value); }
    public static void Wait(Task task) {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer();
        var until = DateTime.UtcNow.AddSeconds(20);
        timer.Interval = TimeSpan.FromMilliseconds(10);
        timer.Tick += (sender,args) => { if (task.IsCompleted || DateTime.UtcNow > until) frame.Continue=false; };
        timer.Start();
        try { if (!task.IsCompleted) Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!task.IsCompleted) throw new TimeoutException("Invitation UI timed out.");
        task.GetAwaiter().GetResult();
    }
}
public sealed class CptInviteDecision : IDisposable {
    private readonly DispatcherTimer timer = new DispatcherTimer();
    public bool Seen;
    public string Text;
    public string Button;
    public bool Warn;
    public CptInviteDecision(object queue, int result) {
        timer.Interval=TimeSpan.FromMilliseconds(10);
        timer.Tick += (sender,args) => {
            var values=new object[] { null };
            if (!(bool)queue.GetType().GetMethod("TryDequeue").Invoke(queue,values)) return;
            var converter=values[0]; var type=converter.GetType();
            Text=(string)type.GetField("Text").GetValue(converter);
            Button=(string)type.GetField("Button1").GetValue(converter);
            Warn=(bool)type.GetField("IsWarn").GetValue(converter);
            type.GetField("Result").SetValue(converter,result);
            ((DispatcherFrame)type.GetField("WaitFrame").GetValue(converter)).Continue=false;
            Seen=true;timer.Stop();
        };
        timer.Start();
    }
    public void Dispose() { timer.Stop(); }
}
'@
$client=[PCLCS.Server.RoomClient]::new($Endpoint)
$roomType=$assembly.GetType('PCL.PageRooms')
$roomType.GetField('Client',[Reflection.BindingFlags]'NonPublic,Static').SetValue($null,[CptInviteUiTest]::Client($client))
$page=[Activator]::CreateInstance($roomType)
$process=$roomType.GetMethod('ProcessInvitation',$flags)
$refreshData=$roomType.GetMethod('RefreshPageData',$flags)
$queue=$assembly.GetType('PCL.ModMain').GetField('WaitingMyMsgBox').GetValue($null)
function Decide($result) {
    $decision=[CptInviteDecision]::new($queue,$result)
    try { [CptInviteUiTest]::Wait($process.Invoke($page,@())) } finally { $decision.Dispose() }
    return $decision
}
function CaptureDecision($decision,$name) {
    $converter=[Activator]::CreateInstance($assembly.GetType('PCL.ModMain+MyMsgBoxConverter'))
    $converter.Title=-join ([char[]]@(0x623f,0x95f4,0x9080,0x8bf7))
    $converter.Text=$decision.Text;$converter.Button1=$decision.Button
    $converter.Button2=-join ([char[]]@(0x53d6,0x6d88));$converter.IsWarn=$decision.Warn
    $dialog=[Activator]::CreateInstance($assembly.GetType('PCL.MyMsgText'),@($converter))
    $canvas=New-Object Windows.Controls.Grid
    $canvas.Background=[Windows.Media.Brushes]::WhiteSmoke
    $canvas.Children.Add($dialog) | Out-Null
    $window=New-Object Windows.Window
    $window.Width=655;$window.Height=470;$window.WindowStyle='None';$window.Left=-2000;$window.Top=-2000;$window.ShowInTaskbar=$false;$window.Content=$canvas
    try {
        $window.Show();$window.UpdateLayout()
        $dialog.Opacity=1;$dialog.RenderTransform=[Windows.Media.Transform]::Identity
        foreach($button in $dialog.Btn1,$dialog.Btn2) {
            $bottom=$button.TranslatePoint((New-Object Windows.Point($button.ActualWidth,$button.ActualHeight)),$canvas)
            if($bottom.X -gt $window.Width -or $bottom.Y -gt $window.Height) { throw 'Invitation dialog action is clipped.' }
        }
        $bitmap=New-Object Windows.Media.Imaging.RenderTargetBitmap(655,470,96,96,[Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($canvas)
        $encoder=New-Object Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream=[IO.File]::Create((Join-Path $Root "output/playwright/$name"))
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
    } finally { $window.Close();$window.Content=$null }
}
[Threading.SynchronizationContext]::SetSynchronizationContext([Windows.Threading.DispatcherSynchronizationContext]::new())
try {
    [CptInviteUiTest]::Wait($refreshData.Invoke($page,@()))
    if($client.InRoom) { throw 'Logged-out refresh entered a room.' }
    $page.ReceiveInvitation($Invite)
    $cancel=Decide 2
    if(-not $cancel.Seen -or $client.InRoom -or $roomType.GetField('PendingInvitation',$flags).GetValue($page)) { throw 'Cancelled logged-out invitation remained pending or joined.' }
    $page.ReceiveInvitation($Invite)
    $confirm=Decide 1
    if(-not $confirm.Seen -or $confirm.Warn -or -not $confirm.Text.Contains($client.ParseInvitation($Invite).Code)) { throw 'Initial invitation confirmation is missing.' }
    if($client.InRoom -or -not $roomType.GetField('PendingInvitation',$flags).GetValue($page)) { throw 'Logged-out invitation was not retained for login.' }
    CaptureDecision $confirm 'cpt-web-invite-confirm.png'
    $client.Login('guest@example.invalid','LocalPass123').GetAwaiter().GetResult()
    [CptInviteUiTest]::Wait($process.Invoke($page,@()))
    if(-not $client.InRoom -or $page.FindName('ActiveRoom').Visibility -ne 'Visible' -or $page.FindName('RoomActions').Visibility -ne 'Collapsed') { throw 'Invitation did not join and show current room.' }
    if($page.FindName('CurrentRoomName').Text -ne 'Web invitation test' -or -not $page.FindName('CurrentRoomSummary').Text.Contains('Host')) { throw 'Current room identity is not shown.' }
    $code=$client.Code
    $localPort=$client.LocalPort
    [CptInviteUiTest]::Wait($refreshData.Invoke($page,@()))
    if(-not $client.InRoom -or $client.Code -ne $code -or $client.LocalPort -ne $localPort) { throw 'Room refresh interrupted the active guest connection.' }
    $lobbyPage=[Activator]::CreateInstance($roomType);$lobbyPage.SetLobbyMode()
    $lobbyPage.FindName('LobbySearch').Text=$code.ToLowerInvariant()
    [CptInviteUiTest]::Wait($refreshData.Invoke($lobbyPage,@()))
    if($lobbyPage.FindName('LobbySearch').Text -ne $code.ToLowerInvariant() -or $lobbyPage.FindName('LobbyItems').Children.Count -ne 1) { throw 'Lobby refresh lost the query or failed private-room search.' }
    $roomType.GetField('Busy',$flags).SetValue($lobbyPage,$true)
    [CptInviteUiTest]::Wait($refreshData.Invoke($lobbyPage,@()))
    if($lobbyPage.FindName('LobbyItems').Children.Count -ne 1) { throw 'Busy refresh changed the current list.' }
    $roomType.GetField('Busy',$flags).SetValue($lobbyPage,$false)
    $page.ReceiveInvitation($Invite)
    [CptInviteUiTest]::Wait($process.Invoke($page,@()))
    if($client.Code -ne $code -or -not $client.InRoom) { throw 'Repeated invitation disturbed current room.' }
    $client.Leave().GetAwaiter().GetResult()
    $client.Enter($true,'',25565,'Current room',$false,'','').GetAwaiter().GetResult()
    $original=$client.Code
    $client.AllocateAddress('refresh-test').GetAwaiter().GetResult() | Out-Null
    $roomType.GetField('PrefixExpanded',$flags).SetValue($page,$true)
    [CptInviteUiTest]::Wait($refreshData.Invoke($page,@()))
    if($page.FindName('PrefixItems').Children.Count -ne 1 -or $client.Code -ne $original -or -not $client.Hosting) { throw 'Host refresh failed to update addresses without leaving.' }
    $page.ReceiveInvitation($Invite)
    $switchCancel=Decide 2
    if(-not $switchCancel.Seen -or -not $switchCancel.Warn -or -not $switchCancel.Text.Contains($original) -or -not $switchCancel.Text.Contains('Current room')) { throw 'Switch confirmation is missing current room.' }
    if(-not $client.InRoom -or $client.Code -ne $original) { throw 'Cancel switched or left the current room.' }
    CaptureDecision $switchCancel 'cpt-web-invite-switch.png'
    $page.ReceiveInvitation($Invite)
    $switchAccept=Decide 1
    if(-not $switchAccept.Seen -or -not $client.InRoom -or $client.Hosting -or $client.Code -ne $code) { throw 'Accepted room switch did not leave and join.' }
    $client.Leave().GetAwaiter().GetResult()
    $page.ReceiveInvitation($Invite)
    $loggedInCancel=Decide 2
    if(-not $loggedInCancel.Seen -or $client.InRoom) { throw 'Logged-in invitation joined despite cancellation.' }
    $page.ReceiveInvitation($Invite)
    $loggedInAccept=Decide 1
    if(-not $loggedInAccept.Seen -or -not $client.InRoom -or $client.Code -ne $code) { throw 'Logged-in invitation did not join after confirmation.' }
    $client.Leave().GetAwaiter().GetResult()
    $invalid=$Invite.Substring(0,$Invite.Length-1)+$(if($Invite.EndsWith('a')) {'b'} else {'a'})
    $page.ReceiveInvitation($invalid)
    $invalidDecision=Decide 1
    if($client.InRoom -or $page.FindName('ActiveRoom').Visibility -ne 'Collapsed') { throw 'Invalid invitation entered a room.' }
    $startup=$assembly.GetType('PCL.ModRoomInvitations').GetMethod('StartupArgument')
    $uri='cpt://join#0123456789ABCDEF.'+('a'*64)
    if($startup.Invoke($null,[object[]]@(,([string[]]@($uri)))) -ne $uri) { throw 'Cold-start invitation argument was lost.' }
    if($startup.Invoke($null,[object[]]@(,([string[]]@($uri,'--gpu'))))) { throw 'Additional activation arguments were accepted.' }
    Write-Output 'Actual UI: native refresh updates room/address/lobby data, preserves local port/search, ignores busy calls; invitation confirmation/cancel, switch and cold-start checks passed.'
} finally { $client.Dispose();[Threading.SynchronizationContext]::SetSynchronizationContext($null) }
