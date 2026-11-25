param([string]$Root = (Split-Path -Parent $PSScriptRoot), [string]$Executable = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
if (-not $Executable) { $Executable = Join-Path $Root 'Plain Craft Launcher 2/bin/CPT.exe' }
$assembly = [Reflection.Assembly]::LoadFrom($Executable)
$app = [Activator]::CreateInstance($assembly.GetType('PCL.Application'), $true)
$app.InitializeComponent()
[Reflection.Assembly]::LoadFrom((Join-Path $Root 'PCLCS/bin/MeloongCore.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom((Join-Path $Root 'PCLCS/bin/MeloongCore.Wpf.dll')) | Out-Null
[MeloongCore.Main]::Init('CPT')
[MeloongCore.Wpf.Main]::Init()
$mockWindow = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($assembly.GetType('PCL.FormMain'))
$extraButton = [Activator]::CreateInstance($assembly.GetType('PCL.MyExtraButton'))
$assembly.GetType('PCL.FormMain').GetField('_BtnExtraBack', [Reflection.BindingFlags]'NonPublic,Instance').SetValue($mockWindow, $extraButton)
$assembly.GetType('PCL.ModMain').GetField('FrmMain').SetValue($null, $mockWindow)
$page = [Activator]::CreateInstance($assembly.GetType('PCL.PageServer'))
$window = New-Object Windows.Window
$window.Width = 830
$window.Height = 600
$container = New-Object Windows.Controls.Grid
$leftColumn = New-Object Windows.Controls.ColumnDefinition
$leftColumn.Width = New-Object Windows.GridLength(155)
$container.ColumnDefinitions.Add($leftColumn)
$container.ColumnDefinitions.Add((New-Object Windows.Controls.ColumnDefinition))
$sidebar = [Activator]::CreateInstance($assembly.GetType('PCL.PageServerLeft'))
$profile = New-Object PCLCS.Server.ServerProfile
$profile.Deployed = $true
$profile.Host = 'example.invalid'
$profile.User = 'minecraft'
$assembly.GetType('PCL.ModMain').GetField('FrmServerLeft').SetValue($null, $sidebar)
$page.OpenProfile($profile)
$assembly.GetType('PCL.ModMain').GetField('FrmServer').SetValue($null, $page)
$container.Children.Add($sidebar) | Out-Null
[Windows.Controls.Grid]::SetColumn($page, 1)
$container.Children.Add($page) | Out-Null
$window.Content = $container
$window.WindowStyle = 'None'
$window.ShowInTaskbar = $false
$window.Left = -2000
$window.Top = -2000
$window.Show()
$window.UpdateLayout()
$folder = Join-Path $Root 'output/playwright'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
foreach ($tab in 0..4) {
    $sidebar.FindName('PanItem').Children[$tab].SetChecked($true, $false, $false)
    $window.UpdateLayout()
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap(830,600,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($window)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create((Join-Path $folder "server-tab-$tab.png"))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}
$window.Close()
$source = New-Object Xml.XmlDocument
$source.Load((Join-Path $Root 'Plain Craft Launcher 2/FormMain.xaml'))
$namespaces = New-Object Xml.XmlNamespaceManager($source.NameTable)
$namespaces.AddNamespace('w', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$namespaces.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$node = $source.SelectSingleNode("//w:Grid[@x:Name='PanTitleMain']", $namespaces)
$node.SetAttribute('xmlns:local', 'clr-namespace:PCL;assembly=CPT')
$node.SetAttribute('xmlns:x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$node.SetAttribute('Width', '810')
$markup = $node.OuterXml.Replace('clr-namespace:PCL"', 'clr-namespace:PCL;assembly=CPT"')
$main = [Windows.Markup.XamlReader]::Parse($markup)
$main.Measure((New-Object Windows.Size(810,48)))
$main.Arrange((New-Object Windows.Rect(0,0,810,48)))
$main.UpdateLayout()
$main.Background = New-Object Windows.Media.SolidColorBrush([Windows.Media.Color]::FromRgb(11,91,203))
$main.UpdateLayout()
$bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap(810,48,96,96,[Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($main)
$encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$stream = [IO.File]::Create((Join-Path $folder 'launcher-navigation.png'))
try { $encoder.Save($stream) } finally { $stream.Dispose() }
Write-Output 'Rendered all five PCL server management sections and navigation.'
