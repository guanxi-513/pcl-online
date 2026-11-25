param([string]$Root=(Split-Path -Parent $PSScriptRoot),[string]$Executable='')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
if(-not $Executable) { $Executable=Join-Path $Root 'output/CPT-Microsoft-Config/CPT.exe' }
$assembly=[Reflection.Assembly]::LoadFrom($Executable)
$app=[Activator]::CreateInstance($assembly.GetType('PCL.Application'),$true);$app.InitializeComponent()
foreach($name in 'MeloongCore','MeloongCore.Wpf','Newtonsoft.Json','PCLCS') { [Reflection.Assembly]::LoadFrom((Join-Path $Root "PCLCS/bin/$name.dll")) | Out-Null }
[MeloongCore.Main]::Init('CPT');[MeloongCore.Wpf.Main]::Init()
$secret=$assembly.GetType('PCL.ModSecret')
$field=$secret.GetField('OAuthClientId')
$original=$field.GetValue($null)
$flags=[Reflection.BindingFlags]'NonPublic,Static'
$require=$secret.GetMethod('RequireOAuthClientId',$flags)
try {
    foreach($value in @($null,'','   ')) {
        $field.SetValue($null,$value)
        foreach($method in @($require,$assembly.GetType('PCL.ModLaunch').GetMethod('MsLoginStep1New',$flags),$assembly.GetType('PCL.ModLaunch').GetMethod('MsLoginStep1Refresh',$flags))) {
            $arguments=New-Object object[] ($method.GetParameters().Length)
            $rejected=$false
            try { $method.Invoke($null,$arguments) | Out-Null } catch {
                $failure=$_.Exception
                while($failure.InnerException) { $failure=$failure.InnerException }
                if($failure -isnot [InvalidOperationException] -or -not $failure.Message.Contains('PCL_MS_CLIENT_ID')) { throw }
                $rejected=$true
            }
            if(-not $rejected) { throw 'Missing Microsoft application ID was not rejected before the request.' }
        }
    }
    $field.SetValue($null,' 11111111-2222-3333-4444-555555555555 ')
    if($require.Invoke($null,@()) -ne '11111111-2222-3333-4444-555555555555') { throw 'Configured application ID was not preserved.' }
    Write-Output 'Microsoft config: empty IDs rejected before device-code and refresh requests; configured ID preserved. No real OAuth requests were made.'
} finally { $field.SetValue($null,$original) }
