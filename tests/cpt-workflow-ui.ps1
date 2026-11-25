param([string]$Root = (Split-Path -Parent $PSScriptRoot), [string]$Executable = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
if (-not $Executable) { $Executable = Join-Path $Root 'output/CPT/CPT.exe' }
$assembly = [Reflection.Assembly]::LoadFrom($Executable)
$app = [Activator]::CreateInstance($assembly.GetType('PCL.Application'),$true)
$app.InitializeComponent()
$app.Add_DispatcherUnhandledException({ param($sender,$event) [Console]::WriteLine($event.Exception.ToString()); $event.Handled=$true })
foreach ($name in 'MeloongCore','MeloongCore.Wpf','Newtonsoft.Json','PCLCS') {
    [Reflection.Assembly]::LoadFrom((Join-Path $Root "PCLCS/bin/$name.dll")) | Out-Null
}
[MeloongCore.Main]::Init('CPT')
[MeloongCore.Wpf.Main]::Init()
$mock = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($assembly.GetType('PCL.FormMain'))
$mock.GetType().GetField('_BtnExtraBack',[Reflection.BindingFlags]'NonPublic,Instance').SetValue($mock,[Activator]::CreateInstance($assembly.GetType('PCL.MyExtraButton')))
$module = $assembly.GetType('PCL.ModMain')
$module.GetField('FrmMain').SetValue($null,$mock)
$folder = Join-Path $Root 'output/playwright'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
function Capture($content,$name,$width=810,$height=470) {
    $window = New-Object Windows.Window
    $window.Width=$width; $window.Height=$height; $window.WindowStyle='None'
    $window.Left=-2000; $window.Top=-2000; $window.ShowInTaskbar=$false; $window.Content=$content
    try {
        $window.Show(); $window.UpdateLayout()
        $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap($width,$height,96,96,[Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($window)
        $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream = [IO.File]::Create((Join-Path $folder $name))
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
    } finally { $window.Close(); $window.Content=$null }
}
$xml = New-Object Xml.XmlDocument
$xml.Load((Join-Path $Root 'Plain Craft Launcher 2/Pages/PageLaunch/PageLaunchLeft.xaml'))
$ns = New-Object Xml.XmlNamespaceManager($xml.NameTable)
$ns.AddNamespace('w','http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$ns.AddNamespace('x','http://schemas.microsoft.com/winfx/2006/xaml')
$node = $xml.SelectSingleNode("//w:Grid[@Name='PanInput']",$ns)
$node.SetAttribute('xmlns:local','clr-namespace:PCL;assembly=CPT')
$node.SetAttribute('xmlns:x','http://schemas.microsoft.com/winfx/2006/xaml')
$node.SetAttribute('Width','300')
$launchPanel = [Windows.Markup.XamlReader]::Parse($node.OuterXml.Replace('clr-namespace:PCL"','clr-namespace:PCL;assembly=CPT"'))
$launchPanel.Background = [Windows.Media.Brushes]::White
$launchPanel.FindName('BtnLaunch').Text=[string][char]0x542f+[char]0x52a8+[char]0x6e38+[char]0x620f
$launchPanel.FindName('BtnLaunch').IsEnabled=$true
$launchPanel.FindName('BtnVersion').IsEnabled=$true
$launchPanel.FindName('LabVersion').Text='1.21.1'
$launchPanel.FindName('BtnMore').Visibility='Visible'
$type = $assembly.GetType('PCL.GameAccount')
foreach ($kind in @(@('offline',0,''),@('microsoft',5,''),@('littleskin',3,'LittleSkin'),@('third-party',3,''),@('nide',2,''))) {
    $account = [Activator]::CreateInstance($type)
    $account.Type = [Enum]::ToObject($assembly.GetType('PCL.ModLaunch+McLoginType'),[int]$kind[1])
    $account.Provider = $kind[2]
    $account.DisplayName = 'huaSQ49sjo'; $account.UserName='test@example.invalid'
    $account.Uuid=''; $account.Label='Test account'
    $launchPanel.FindName('AccountView').Bind($account)
    $canvas = New-Object Windows.Controls.Grid
    $canvas.Background = New-Object Windows.Media.SolidColorBrush([Windows.Media.Color]::FromRgb(220,235,249))
    $launchPanel.HorizontalAlignment='Left'
    $canvas.Children.Add($launchPanel) | Out-Null
    Capture $canvas ('cpt-launch-' + $kind[0] + '.png')
    $canvas.Children.Clear()
}
$left = [Activator]::CreateInstance($assembly.GetType('PCL.PageMultiplayerLeft'))
$module.GetField('FrmMultiplayerLeft').SetValue($null,$left)
if ($left.FindName('PanItem').Children.Count -ne 3 -or $left.FindName('ItemCreate')) { throw 'Multiplayer must have only server, rooms and lobby entries.' }
$left.PageID=1
$rooms=$left.PageGet()
$grid=New-Object Windows.Controls.Grid
$grid.ColumnDefinitions.Add((New-Object Windows.Controls.ColumnDefinition -Property @{Width=(New-Object Windows.GridLength(155))}))
$grid.ColumnDefinitions.Add((New-Object Windows.Controls.ColumnDefinition))
$grid.Children.Add($left) | Out-Null
[Windows.Controls.Grid]::SetColumn($rooms,1); $grid.Children.Add($rooms) | Out-Null
Capture $grid 'cpt-multiplayer-rooms.png'
$grid.Children.Clear()
if ($rooms.GetType().Name -eq 'PageLinkMain') { throw 'Legacy multiplayer page still opens.' }
$left.PageID=2
$lobby=$left.PageGet()
if ($lobby.GetType().Name -ne 'PageRooms' -or $lobby.FindName('LobbyPanel').Visibility -ne 'Visible' -or $lobby.FindName('CreateCard').Visibility -ne 'Collapsed') { throw 'Multiplayer lobby route is missing.' }
$left.PageID=3
$mergedPage=$left.PageGet()
if ($left.PageID -ne 1 -or $mergedPage -ne $rooms -or $mergedPage.FindName('CreateCard').Visibility -ne 'Visible' -or $mergedPage.FindName('JoinCard').Visibility -ne 'Visible') { throw 'Old host route does not redirect to merged room workflow.' }
$left.PageID=1
$server = [Activator]::CreateInstance($assembly.GetType('PCL.PageServer'))
$server.OpenProfile((New-Object PCLCS.Server.ServerProfile))
$server.FindName('DeploySource').SelectedIndex=1
if ($server.FindName('PackImportPanel').Visibility -ne 'Visible') { throw 'Pack import UI missing.' }
if ($server.FindName('OfficialCorePanel').Visibility -ne 'Collapsed') { throw 'Official downloader overlaps custom import.' }
Capture $server 'cpt-custom-pack-import.png' 810 750
$source=[IO.File]::ReadAllText((Join-Path $Root 'Plain Craft Launcher 2/FormMain.xaml.vb'))
if ($source.Contains('PageChangeAnim(FrmServerSelectLeft,') -or $source.Contains('PageChangeAnim(FrmServerDeployLeft,') -or $source.Contains('New PageLinkMain')) { throw 'Removed sidebar/legacy routes remain active.' }
if ($source.Contains('BtnExtraServerAdd')) { throw 'Removed floating add action still has code.' }
Write-Output 'Five account templates without floating add action, multiplayer rooms and custom-pack UI rendered successfully.'
