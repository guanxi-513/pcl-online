param([string]$Root=(Split-Path -Parent $PSScriptRoot), [string]$Executable='')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
if (-not $Executable) { $Executable=Join-Path $Root 'output/CPT-Downloads/CPT.exe' }
$assembly=[Reflection.Assembly]::LoadFrom($Executable)
$app=[Activator]::CreateInstance($assembly.GetType('PCL.Application'),$true);$app.InitializeComponent()
foreach($name in 'MeloongCore','MeloongCore.Wpf','Newtonsoft.Json','PCLCS') { [Reflection.Assembly]::LoadFrom((Join-Path $Root "PCLCS/bin/$name.dll")) | Out-Null }
[MeloongCore.Main]::Init('CPT');[MeloongCore.Wpf.Main]::Init()
$flags=[Reflection.BindingFlags]'NonPublic,Instance'
$mock=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($assembly.GetType('PCL.FormMain'))
$mock.GetType().GetField('_BtnExtraBack',$flags).SetValue($mock,[Activator]::CreateInstance($assembly.GetType('PCL.MyExtraButton')))
$module=$assembly.GetType('PCL.ModMain');$module.GetField('FrmMain').SetValue($null,$mock)
$loader=$assembly.GetType('PCL.ModDownload').GetField('DlClientListLoader').GetValue($null)
$result=[Activator]::CreateInstance($assembly.GetType('PCL.ModDownload+DlClientListResult'))
$result.Value=[Newtonsoft.Json.Linq.JObject]::Parse('{"versions":[{"id":"1.21.1","type":"release","releaseTime":"2024-08-08T12:00:00Z","url":"https://example.invalid/release.json"},{"id":"24w33a","type":"snapshot","releaseTime":"2024-08-15T12:00:00Z","url":"https://example.invalid/snapshot.json"}]}')
$loader.Output=$result;$loader.State=[Enum]::Parse($assembly.GetType('PCL.ModBase+LoadState'),'Finished')
$left=[Activator]::CreateInstance($assembly.GetType('PCL.PageDownloadLeft'))
$module.GetField('FrmDownloadLeft').SetValue($null,$left)
$subtype=$assembly.GetType('PCL.FormMain+PageSubType')
foreach($pair in @(@(0,'PageDownloadInstall'),@(2,'PageDownloadMod'),@(3,'PageDownloadPack'),@(4,'PageDownloadDataPack'),@(5,'PageDownloadResourcePack'),@(6,'PageDownloadShader'))) {
    $page=$left.PageGet([Enum]::ToObject($subtype,[int]$pair[0]))
    if ($page.GetType().Name -ne $pair[1]) { throw "Wrong download mapping for $($pair[0]): $($page.GetType().Name)" }
}
$install=$left.PageGet([Enum]::ToObject($subtype,0))
if ($module.GetField('FrmServer').GetValue($null)) { throw 'Download page initialized server deployment.' }
$install.GetType().GetMethod('LoadMinecraft_OnFinish',$flags).Invoke($install,@()) | Out-Null
if ($install.FindName('PanMinecraft').Children.Count -lt 2) { throw 'Download version list did not render.' }
$source=New-Object Xml.XmlDocument;$source.Load((Join-Path $Root 'Plain Craft Launcher 2/FormMain.xaml'))
$ns=New-Object Xml.XmlNamespaceManager($source.NameTable);$ns.AddNamespace('w','http://schemas.microsoft.com/winfx/2006/xaml/presentation');$ns.AddNamespace('x','http://schemas.microsoft.com/winfx/2006/xaml')
if ($source.SelectSingleNode("//*[@x:Name='BtnExtraServerAdd']",$ns)) { throw 'Floating add button remains.' }
$node=$source.SelectSingleNode("//w:Grid[@x:Name='PanTitleMain']",$ns)
$node.SetAttribute('xmlns:local','clr-namespace:PCL;assembly=CPT');$node.SetAttribute('xmlns:x','http://schemas.microsoft.com/winfx/2006/xaml')
$node.SetAttribute('Width','810')
$header=[Windows.Markup.XamlReader]::Parse($node.OuterXml.Replace('clr-namespace:PCL"','clr-namespace:PCL;assembly=CPT"'))
if ($header.FindName('BtnTitleSelect1').Tag -ne '1' -or $header.FindName('BtnTitleSelect2').Tag -ne '2' -or $header.FindName('BtnTitleSelect2').Visibility -ne 'Visible') { throw 'Download and multiplayer title buttons not independent.' }
$header.Background=New-Object Windows.Media.SolidColorBrush([Windows.Media.Color]::FromRgb(11,91,203))
$header.Measure((New-Object Windows.Size(810,48)));$header.Arrange((New-Object Windows.Rect(0,0,810,48)));$header.UpdateLayout()
$logo=$header.FindName('ShapeTitleLogo');$nav=$header.FindName('PanTitleSelect')
$navLeft=$nav.TranslatePoint((New-Object Windows.Point(0,0)),$header).X
$navRight=$nav.TranslatePoint((New-Object Windows.Point($nav.ActualWidth,0)),$header).X
if ($navLeft -lt 85 -or $navRight -gt 738) { throw "Title buttons overlap logo/window tools: $navLeft,$navRight" }
function SaveImage($element,$width,$height,$name) {
    $bitmap=New-Object Windows.Media.Imaging.RenderTargetBitmap($width,$height,96,96,[Windows.Media.PixelFormats]::Pbgra32);$bitmap.Render($element)
    $encoder=New-Object Windows.Media.Imaging.PngBitmapEncoder;$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $folder=Join-Path $Root 'output/playwright';New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $stream=[IO.File]::Create((Join-Path $folder $name));try {$encoder.Save($stream)}finally{$stream.Dispose()}
}
SaveImage $header 810 48 'cpt-navigation-downloads.png'
$sidebar=New-Object Xml.XmlDocument;$sidebar.Load((Join-Path $Root 'Plain Craft Launcher 2/Pages/PageDownload/PageDownloadLeft.xaml'))
$sidebar.DocumentElement.RemoveAttribute('Class','http://schemas.microsoft.com/winfx/2006/xaml')
$sidebar.DocumentElement.SetAttribute('xmlns:local','clr-namespace:PCL;assembly=CPT')
$markup=$sidebar.OuterXml -replace ' Click="Refresh_Click"',''
$staticLeft=[Windows.Markup.XamlReader]::Parse($markup.Replace('clr-namespace:PCL"','clr-namespace:PCL;assembly=CPT"'))
$staticLeft.Measure((New-Object Windows.Size(160,470)));$staticLeft.Arrange((New-Object Windows.Rect(0,0,160,470)));$staticLeft.UpdateLayout()
SaveImage $staticLeft 160 470 'cpt-download-sidebar.png'
Write-Output 'All six download routes, client version list, independent multiplayer tab, minimum-width navigation and removed floating add action passed.'
