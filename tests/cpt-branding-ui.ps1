param([string]$Root = (Split-Path -Parent $PSScriptRoot), [string]$Executable = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
if (-not $Executable) { $Executable = Join-Path $Root 'output/CPT/CPT.exe' }
$assembly = [Reflection.Assembly]::LoadFrom($Executable)
if ($assembly.GetName().Name -ne 'CPT') { throw 'Executable assembly name is not CPT.' }
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Executable)
if ($info.ProductName -notlike 'CPT*' -or $info.CompanyName -ne 'HUASQ') { throw 'Product metadata mismatch.' }
foreach ($resource in 'CPT.LICENCE','CPT.NOTICE') {
    $stream = $assembly.GetManifestResourceStream($resource)
    if (-not $stream) { throw "Missing embedded attribution: $resource" }
    $stream.Dispose()
}
foreach ($file in 'LICENCE','CPT-NOTICE.md') {
    if (-not (Test-Path -LiteralPath (Join-Path (Split-Path $Executable) $file))) { throw "Missing distribution file: $file" }
}
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
$secret = $assembly.GetType('PCL.ModSecret')
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$secret.GetMethod('ThemeRefresh').Invoke($null,@([int]14)) | Out-Null
if ($secret.GetField('ThemeNow').GetValue($null) -ne 0 -or $secret.GetField('ColorHue').GetValue($null) -ne 210) { throw 'Non-blue theme allowed.' }
foreach ($id in 0..14) {
    $allowed = $secret.GetMethod('ThemeCheckOne',$flags).Invoke($null,@([int]$id))
    if ($allowed -ne ($id -eq 0)) { throw "Theme permission mismatch for $id" }
}
$about = [Activator]::CreateInstance($assembly.GetType('PCL.PageOtherAbout'))
$cards = $about.FindName('PanMain').Children
if ($cards[0].Title -notlike 'PCL*' -or $cards[1].Title -notlike 'CPT*') { throw 'Attribution card order changed.' }
if ($about.FindName('ItemAboutPcl').Title -ne $info.ProductName) { throw 'About product name mismatch.' }
$folder = Join-Path $Root 'output/playwright'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
function Capture($page,$name,$width,$height) {
    $window = New-Object Windows.Window
    $window.Width = $width; $window.Height = $height; $window.WindowStyle = 'None'
    $window.Left = -2000; $window.Top = -2000; $window.ShowInTaskbar = $false
    $window.Content = $page
    try {
        $window.Show(); $window.UpdateLayout()
        if ($name -eq 'cpt-about-legal.png') {
            foreach ($index in 4,5) {
                $card = $page.FindName('PanMain').Children[$index]
                $card.IsSwapped = $false
                $card.SwapControl.Visibility = 'Visible'
                $card.TriggerForceResize()
            }
            $window.UpdateLayout()
            $page.FindName('PanBack').ScrollToVerticalOffset($page.FindName('PanMain').Children[4].TranslatePoint((New-Object Windows.Point(0,0)),$page.FindName('PanMain')).Y)
            $window.UpdateLayout()
        }
        $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap($width,$height,96,96,[Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($window)
        $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream = [IO.File]::Create((Join-Path $folder $name))
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
    } catch { throw $_.Exception.ToString() } finally { $window.Close(); $window.Content = $null }
}
Capture $about 'cpt-about-minimum.png' 655 430
Capture $about 'cpt-about-legal.png' 655 900
$setup = [Activator]::CreateInstance($assembly.GetType('PCL.PageSetupUI'))
if ($setup.FindName('PanLauncherTheme').Visibility -ne 'Collapsed' -or $setup.FindName('PanLauncherTheme').IsEnabled) { throw 'Upstream unlock UI visible or interactive.' }
$source = [IO.File]::ReadAllText((Join-Path $Root 'Plain Craft Launcher 2/Pages/PageOther/PageOtherLeft.xaml.vb'))
if ($source.Contains('https://github.com/Meloong-Git/PCL')) { throw 'CPT feedback still points to upstream.' }
$export = [IO.File]::ReadAllText((Join-Path $Root 'Plain Craft Launcher 2/Pages/PageInstance/PageInstanceExport.xaml.vb'))
if ($export.Contains('DownloadLatestPCL(') -or $export.Contains('CacheFolder & "Plain Craft Launcher.exe"')) { throw 'Modpack export still packages upstream launcher.' }
Write-Output 'CPT metadata, embedded notices, upstream attribution order, minimum-width About rendering, blue theme and feedback/export checks passed.'
