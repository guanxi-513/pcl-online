param([string]$Root=(Split-Path -Parent $PSScriptRoot),[string]$Executable='')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
if(-not $Executable) { $Executable=Join-Path $Root 'output/CPT-List-Lobby/CPT.exe' }
$assembly=[Reflection.Assembly]::LoadFrom($Executable)
$app=[Activator]::CreateInstance($assembly.GetType('PCL.Application'),$true);$app.InitializeComponent()
foreach($name in 'MeloongCore','MeloongCore.Wpf','Newtonsoft.Json','PCLCS') { [Reflection.Assembly]::LoadFrom((Join-Path $Root "PCLCS/bin/$name.dll")) | Out-Null }
[MeloongCore.Main]::Init('CPT');[MeloongCore.Wpf.Main]::Init()
$mock=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($assembly.GetType('PCL.FormMain'))
$flags=[Reflection.BindingFlags]'NonPublic,Instance'
$mock.GetType().GetField('_BtnExtraBack',$flags).SetValue($mock,[Activator]::CreateInstance($assembly.GetType('PCL.MyExtraButton')))
$panMsg=New-Object Windows.Controls.Grid
$mock.GetType().GetField('_PanMsg',$flags).SetValue($mock,$panMsg)
$panMsg.Background=[Windows.Media.Brushes]::Transparent
$assembly.GetType('PCL.ModMain').GetField('FrmMain').SetValue($null,$mock)
$page=[Activator]::CreateInstance($assembly.GetType('PCL.PageRooms'));$page.SetLobbyMode()
$room=[Newtonsoft.Json.Linq.JObject]::Parse('{"code":"0123456789ABCDEF","title":"Survival room","nickname":"Alex","members":2,"capacity":8,"passwordRequired":false,"locked":false}')
$details=$page.GetType().GetMethod('LobbyRoomDetails',$flags).Invoke($page,[object[]]@(,$room))
$converterType=$assembly.GetType('PCL.ModMain+MyMsgBoxConverter')
$joinLabel=-join ([char[]]@(0x52a0,0x5165,0x623f,0x95f4))
$cancelLabel=-join ([char[]]@(0x53d6,0x6d88))
$passwordLabel=-join ([char[]]@(0x623f,0x95f4,0x5bc6,0x7801))
foreach($kind in 'confirm','password') {
    $converter=[Activator]::CreateInstance($converterType)
    $converter.Title=$room['title'].ToString();$converter.Text=$details
    $converter.Button1=$joinLabel;$converter.Button2=$cancelLabel
    if($kind -eq 'password') {
        $converter.MaskInput=$true;$converter.HintText=$passwordLabel
        $converter.ValidateRules=New-Object 'System.Collections.ObjectModel.Collection[PCL.Validate]'
        $dialog=[Activator]::CreateInstance($assembly.GetType('PCL.MyMsgInput'),@($converter))
        if($dialog.FindName('SecretArea').Visibility -ne 'Visible' -or $dialog.FindName('TextArea').Visibility -ne 'Collapsed') { throw 'Room password is not masked.' }
    } else {
        $dialog=[Activator]::CreateInstance($assembly.GetType('PCL.MyMsgText'),@($converter))
    }
    if($dialog.Btn1.Text -ne $joinLabel -or $dialog.Btn2.Text -ne $cancelLabel) { throw 'Dialog actions are incorrect.' }
    $window=New-Object Windows.Window
    $window.Width=655;$window.Height=470;$window.WindowStyle='None';$window.Left=-2000;$window.Top=-2000;$window.ShowInTaskbar=$false
    $canvas=New-Object Windows.Controls.Grid
    $canvas.Background=[Windows.Media.Brushes]::WhiteSmoke
    $canvas.Children.Add($dialog) | Out-Null;$window.Content=$canvas
    try {
        $window.Show();$window.UpdateLayout()
        $dialog.Opacity=1;$dialog.RenderTransform=[Windows.Media.Transform]::Identity
        foreach($button in $dialog.Btn1,$dialog.Btn2) {
            $point=$button.TranslatePoint((New-Object Windows.Point($button.ActualWidth,$button.ActualHeight)),$canvas)
            if($point.X -gt $window.Width -or $point.Y -gt $window.Height) { throw 'Dialog action is clipped.' }
        }
        $bitmap=New-Object Windows.Media.Imaging.RenderTargetBitmap(655,470,96,96,[Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($canvas)
        $encoder=New-Object Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream=[IO.File]::Create((Join-Path $Root "output/playwright/cpt-lobby-dialog-$kind.png"))
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
    } catch { Write-Output $_.Exception.ToString(); throw } finally { $window.Close();$window.Content=$null }
}
$queue=$assembly.GetType('PCL.ModMain').GetField('WaitingMyMsgBox').GetValue($null)
foreach($kind in 'locked','full') {
    $room['locked']=($kind -eq 'locked');$room['members']=8
    $task=$page.GetType().GetMethod('OpenLobbyRoom',$flags).Invoke($page,[object[]]@(,$room))
    $task.GetAwaiter().GetResult() | Out-Null
    $converter=$null
    if(-not $queue.TryDequeue([ref]$converter) -or $converter.Button2 -ne '') { throw 'Unavailable room did not show a close-only popup.' }
}
$source=Get-Content -LiteralPath (Join-Path $Root 'Plain Craft Launcher 2/Pages/PageServer/PageRooms.xaml.vb') -Raw -Encoding UTF8
if(-not $source.Contains('If Secret Is Nothing Then Return') -or -not $source.Contains('ElseIf MyMsgBox(Details,') -or -not $source.Contains('LobbyDialogOpen = False')) { throw 'Modal cancellation or cleanup is missing.' }
Write-Output 'Lobby confirmation/password dialogs rendered, password masked, action bounds and unavailable-room popups verified.'
