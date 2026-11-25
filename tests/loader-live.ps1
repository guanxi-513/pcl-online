param([string]$Root = (Split-Path -Parent $PSScriptRoot), [string]$Executable = '', [string]$Core = 'Fabric')
$ErrorActionPreference = 'Stop'
if (-not $Executable) { $Executable = Join-Path $Root 'output/CPT/CPT.exe' }
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$assembly = [Reflection.Assembly]::LoadFrom($Executable)
$app = [Activator]::CreateInstance($assembly.GetType('PCL.Application'), $true)
$app.InitializeComponent()
foreach ($name in 'MeloongCore','MeloongCore.Wpf','Newtonsoft.Json','PCLCS') {
    [Reflection.Assembly]::LoadFrom((Join-Path $Root "PCLCS/bin/$name.dll")) | Out-Null
}
[MeloongCore.Main]::Init('PCL')
[MeloongCore.Wpf.Main]::Init()
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$entries = $assembly.GetType('PCL.Settings').GetField('Entries').GetValue($null)
$entries['ToolDownloadVersion'].Set(1, $null)
$catalog = $assembly.GetType('PCL.ModServerCatalog')
$method = $catalog.GetMethod('LoaderVersions')
Add-Type -ReferencedAssemblies System.dll,System.Core.dll,WindowsBase -TypeDefinition @'
using System;
using System.Reflection;
using System.Threading.Tasks;
public static class CatalogProbe {
  public static Task<object> Run(MethodInfo method, string core) {
    return Task.Run(() => method.Invoke(null, new object[] {core, "1.21.1", false}));
  }
  public static void Pump() {
    var frame = new System.Windows.Threading.DispatcherFrame();
    System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
      System.Windows.Threading.DispatcherPriority.Background, new Action(() => frame.Continue = false));
    System.Windows.Threading.Dispatcher.PushFrame(frame);
  }
}
'@
$task = [CatalogProbe]::Run($method, $Core)
$deadline = [DateTime]::UtcNow.AddSeconds(150)
try {
    while (-not $task.IsCompleted) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Loader timed out.' }
        Start-Sleep -Milliseconds 20
    }
    $result = $task.GetAwaiter().GetResult()
    if ($result.Count -eq 0) { throw 'Loader returned an empty list.' }
    Write-Output "$Core PCL loader returned $($result.Count) entries."
    $result | Select-Object -First 3 -Property Id
} finally { }
