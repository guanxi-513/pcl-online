param([string]$Root = (Split-Path -Parent $PSScriptRoot), [string]$Executable = '')
$ErrorActionPreference = 'Stop'
if (-not $Executable) { $Executable = Join-Path $Root 'output/CPT/CPT.exe' }
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$assembly = [Reflection.Assembly]::LoadFrom($Executable)
$app = [Activator]::CreateInstance($assembly.GetType('PCL.Application'), $true)
$app.InitializeComponent()
foreach ($name in 'MeloongCore','MeloongCore.Wpf','Newtonsoft.Json','PCLCS') {
    [Reflection.Assembly]::LoadFrom((Join-Path $Root "PCLCS/bin/$name.dll")) | Out-Null
}
[MeloongCore.Main]::Init('CPT')
[MeloongCore.Wpf.Main]::Init()
$mock = [Runtime.Serialization.FormatterServices]::GetUninitializedObject($assembly.GetType('PCL.FormMain'))
$mock.GetType().GetField('_BtnExtraBack', [Reflection.BindingFlags]'NonPublic,Instance').SetValue($mock, [Activator]::CreateInstance($assembly.GetType('PCL.MyExtraButton')))
$assembly.GetType('PCL.ModMain').GetField('FrmMain').SetValue($null, $mock)
$profiles = @()
foreach ($name in '测试部署服','测试生存服','测试插件服') {
    $profile = New-Object PCLCS.Server.ServerProfile
    $profile.Name = $name
    $profile.Host = 'example.invalid'
    $profiles += $profile
}
$profiles[0].Core = 'Fabric'
$profiles[2].Core = 'Paper'
# Populate only the in-memory setting cache, never the user's registry.
$entries = $assembly.GetType('PCL.Settings').GetField('Entries').GetValue($null)
$oldCache = $entries['ServerProfiles'].GetCache($null)
$profileList = New-Object 'System.Collections.Generic.List[PCLCS.Server.ServerProfile]'
foreach ($profile in $profiles) { $profileList.Add($profile) }
$entries['ServerProfiles'].Set([Newtonsoft.Json.JsonConvert]::SerializeObject($profileList), $null)
$job = [Activator]::CreateInstance($assembly.GetType('PCL.ServerDeployment'))
$job.Profile = $profiles[0]
$loaderType = $assembly.GetType('PCL.ModLoader+LoaderTask`2').MakeGenericType([int], [Newtonsoft.Json.Linq.JObject])
$job.Loader = [Activator]::CreateInstance($loaderType)
$job.Loader.Name = '本地测试任务'
$stateType = $assembly.GetType('PCL.ModBase+LoadState')
$job.Loader.State = [Enum]::Parse($stateType, 'Loading')
$job.Report('1/5 正在连接服务器并检测系统……')
$job.Report('2/5 正在补全环境、下载 Java 与服务端……')
$module = $assembly.GetType('PCL.ModServerTasks')
$module.GetField('Deployments').GetValue($null)[$profiles[0].Id] = $job
$module.GetMethod('SetStatus').Invoke($null, @($profiles[1].PSObject.BaseObject, '运行中')) | Out-Null
$module.GetMethod('SetStatus').Invoke($null, @($profiles[2].PSObject.BaseObject, '已停止')) | Out-Null
$left = [Activator]::CreateInstance($assembly.GetType('PCL.PageServerSelectLeft'))
$right = [Activator]::CreateInstance($assembly.GetType('PCL.PageServerSelectRight'))
if ($right.FindName('BtnAdd')) { throw 'Removed My Servers header action is still present.' }
if (-not $right.FindName('BtnNew')) { throw 'Restored My Servers empty-state action is missing.' }
if (-not ($right.FindName('BtnAddFloating') -is $assembly.GetType('PCL.MyExtraButton'))) { throw 'Native floating deployment button is missing.' }
if ($left.FindName('PanItem').Children.Count -ne 1) { throw 'My Servers still has duplicated add or refresh menu entries.' }
if (-not ($right -is $assembly.GetType('PCL.IRefreshable'))) { throw 'My Servers does not implement the PCL F5 refresh contract.' }
$deployLeft = [Activator]::CreateInstance($assembly.GetType('PCL.PageServerDeployLeft'))
$deployRight = [Activator]::CreateInstance($assembly.GetType('PCL.PageServerDeployRight'))
$folder = Join-Path $Root 'output/playwright'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
function Capture($leftPage, $rightPage, $name, $leftWidth) {
    $window = New-Object Windows.Window
    $window.Width = 830
    $window.Height = 600
    $window.WindowStyle = 'None'
    $window.ShowInTaskbar = $false
    $window.Left = -2000
    $window.Top = -2000
    $grid = New-Object Windows.Controls.Grid
    $column = New-Object Windows.Controls.ColumnDefinition
    $column.Width = New-Object Windows.GridLength($leftWidth)
    $grid.ColumnDefinitions.Add($column)
    $grid.ColumnDefinitions.Add((New-Object Windows.Controls.ColumnDefinition))
    $grid.Children.Add($leftPage) | Out-Null
    [Windows.Controls.Grid]::SetColumn($rightPage, 1)
    $grid.Children.Add($rightPage) | Out-Null
    $window.Content = $grid
    try {
        $window.Show()
        $window.UpdateLayout()
        if ($rightPage.GetType().Name -eq 'PageServerSelectRight') {
            $floating=$rightPage.FindName('BtnAddFloating')
            $floating.Height=50;$floating.RenderTransform=[Windows.Media.ScaleTransform]::new(1,1)
            $window.UpdateLayout()
            $point=$floating.TranslatePoint((New-Object Windows.Point(0,0)),$grid)
            if($point.X -lt 0 -or $point.Y -lt 0 -or $point.X+$floating.ActualWidth -gt $window.Width -or $point.Y+$floating.ActualHeight -gt $window.Height) { throw 'Floating deployment button is clipped.' }
        }
        $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap(830,600,96,96,[Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($window)
        $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream = [IO.File]::Create((Join-Path $folder $name))
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
    } finally {
        $window.Close()
        $grid.Children.Clear()
    }
}
try {
    $entries['ServerProfiles'].Set('[]', $null)
    $right.RefreshList()
    if ($right.FindName('PanEmpty').Visibility -ne 'Visible') { throw 'Empty server list does not show restored empty state.' }
    Capture $left $right 'server-selection-empty.png' 0
    $entries['ServerProfiles'].Set([Newtonsoft.Json.JsonConvert]::SerializeObject($profileList), $null)
    $right.RefreshList()
    $initialRows=$right.GetType().GetField('Rows',[Reflection.BindingFlags]'NonPublic,Instance').GetValue($right)
    foreach($profile in $profiles) {
        $delete=@($initialRows[$profile.Id].Item2.Buttons)[-1]
        if([string]$delete.ToolTip -notmatch '删除') { throw 'Delete button missing from saved server.' }
        if($delete.IsEnabled -eq ($profile.Id -eq $profiles[0].Id)) { throw 'Deleting active deployment is not blocked.' }
    }
    if ($right.FindName('PanMain').Children.Count -ne 2) { throw 'Deployment and saved servers were not grouped.' }
    if ($right.FindName('PanEmpty').Visibility -ne 'Collapsed') { throw 'Empty state overlaps the populated server list.' }
    Capture $left $right 'server-selection.png' 0
    $deployLeft.Bind($job)
    $deployRight.Bind($job)
    $download=[PCLCS.Server.DeploymentUpdate]::new()
    $download.Kind='download';$download.Message='Java runtime';$download.Bytes=52428800;$download.Total=104857600;$download.Speed=2097152
    $job.UpdateRemote($download)
    $terminal=[PCLCS.Server.DeploymentUpdate]::new();$terminal.Kind='terminal';$terminal.Message='[Installer] Preparing server libraries'
    $job.UpdateRemote($terminal)
    $deployRight.Refresh()
    if($deployRight.FindName('DetailProgress').Value -ne 50 -or $deployRight.FindName('LabTransfer').Text -notmatch '50.0%' -or $deployRight.FindName('LabTerminal').Text -notmatch 'Preparing server libraries') { throw 'Exact download progress or terminal output missing.' }
    if($job.StageProgress -ge 1) { throw 'Download complete implies overall deployment complete.' }
    $detail=[PCLCS.Server.DeploymentUpdate]::new();$detail.Message='Installing Fabric'
    $job.UpdateRemote($detail);$deployRight.Refresh()
    if(-not $deployRight.FindName('DetailProgress').IsIndeterminate) { throw 'Unknown installation progress fabricated a percentage.' }
    $job.UpdateRemote($download);$deployRight.Refresh()
    Capture $deployLeft $deployRight 'server-deploy-loading.png' 0
    $deployRight.FindName('LogTabs').SelectedIndex=1
    Capture $deployLeft $deployRight 'server-deploy-terminal.png' 0
    $job.PauseDeployment();$job.Control.Acknowledge($true);$deployRight.Refresh()
    if($deployRight.FindName('BtnResume').Visibility -ne 'Visible' -or $deployRight.FindName('DetailProgress').IsIndeterminate) { throw 'Paused deployment view is incorrect.' }
    Capture $deployLeft $deployRight 'server-deploy-paused.png' 0
    $job.ResumeDeployment();$deployRight.Refresh()
    $deployRight.FindName('LogTabs').SelectedIndex=0
    if ($deployRight.FindName('LabLog').Text -notmatch '2/5') { throw 'Deployment log was not bound.' }
    $longError='NeoForge installer failed.'+"`n"+('org.example:dependency:1.0'+"`n")*100
    $job.Report('部署失败：'+$longError)
    $job.Loader.Error = [Exception]::new($longError)
    $job.Loader.State = [Enum]::Parse($stateType, 'Failed')
    $deployLeft.Refresh()
    $deployRight.Refresh()
    if($deployRight.FindName('LabStage').Text.Length -gt 240 -or $deployRight.FindName('LabStage').Text.Contains("`n")) { throw 'Long installer error still floods the stage header.' }
    if($deployRight.FindName('BtnRetryDeployment').Visibility -ne 'Visible') { throw 'Failed task has no continue-deployment action.' }
    if(-not ($deployRight.FindName('LabResult') -is [Windows.Controls.TextBox]) -or $deployRight.FindName('LabResult').MaxHeight -gt 160) { throw 'Detailed error is not bounded and scrollable.' }
    if(-not $job.LogText().Contains('org.example:dependency')) { throw 'Full error was discarded instead of retained in logs.' }
    if($deployRight.FindName('DetailProgress').IsIndeterminate) { throw 'Failed deployment animation did not stop.' }
    if ($deployLeft.FindName('LabTitle').Text -ne '部署失败') { throw 'Failed state was not displayed.' }
    if ($deployLeft.FindName('LabProgress').Text -match '100') { throw 'Failed deployment incorrectly shows 100% completion.' }
    Capture $deployLeft $deployRight 'server-deploy-failed.png' 0
    $job.Loader.State = [Enum]::Parse($stateType, 'Finished')
    $job.Control.Finish()
    $deployLeft.Refresh()
    $deployRight.Refresh()
    if($deployRight.FindName('BtnRetryDeployment').Visibility -ne 'Collapsed') { throw 'Completed task still offers failure continuation.' }
    if ($deployLeft.FindName('LabTitle').Text -ne '部署完成') { throw 'Finished state was not displayed.' }
    Capture $deployLeft $deployRight 'server-deploy-finished.png' 0
    $right.RefreshList()
    if ($right.FindName('PanMain').Children.Count -ne 1) { throw 'Completed task stayed in the deployment group.' }
    $management = [Activator]::CreateInstance($assembly.GetType('PCL.PageServer'))
    if (-not $management.FindName('BtnNetwork')) { throw 'Remote network check/repair action is missing.' }
    if (-not $management.FindName('BtnContinueDeployment')) { throw 'Persistent continue-deployment entry is missing.' }
    $profiles[1].Deployed = $true
    $entries['ServerProfiles'].Set([Newtonsoft.Json.JsonConvert]::SerializeObject($profileList), $null)
    $right.RefreshList()
    $managedRows = $right.GetType().GetField('Rows', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($right)
    if (-not ($managedRows[$profiles[1].Id].Item2.Buttons | Where-Object { $_.ToolTip -eq '导出客户端整合包' })) { throw 'Client export was not added to My Servers.' }
    $management.OpenProfile($profiles[1])
    if ($management.IsDeploymentMode) { throw 'Deployed server opened deployment mode.' }
    if ($management.FindName('DeploymentCard').Visibility -ne 'Collapsed') { throw 'Deployment form is visible in management.' }
    if ($management.FindName('Profiles')) { throw 'Redundant server selector is still present.' }
    if ($management.FindName('WebsiteSection')) { throw 'Removed server website settings still exist.' }
    $managementSidebar = $assembly.GetType('PCL.ModMain').GetField('FrmServerLeft').GetValue($null)
    if ($managementSidebar.PageID -ne 1) { throw 'Management does not open running status.' }
    if ($managementSidebar.FindName('PanItem').Children.Count -ne 5) { throw 'Server management still has the website/update menu.' }
    if ($management.FindName('RuntimeAdvancedPanel').Visibility -ne 'Collapsed') { throw 'Advanced Java settings are not collapsed.' }
    if ($management.FindName('ResourceSearchCard').Visibility -ne 'Collapsed') { throw 'Resource search overloads the initial resource view.' }
    if (-not $management.FindName('LogCard').IsSwapped) { throw 'Raw logs are not collapsed by default.' }
    $management.PresentStatus([Newtonsoft.Json.Linq.JObject]::Parse('{"state":"running","players":"2 / 20: Steve, Alex","pendingResources":2,"pendingRuntime":false}'))
    if ($management.FindName('BtnPower').Text -ne '停止服务器') { throw 'Running server action is not stop.' }
    if ($management.FindName('ServerPlayers').Text -notmatch 'Steve') { throw 'Player summary is missing.' }
    $management.PresentStatus([Newtonsoft.Json.Linq.JObject]::Parse('{"state":"stopped","players":"","pendingResources":0,"pendingRuntime":false}'))
    if ($management.FindName('BtnPower').Text -ne '启动服务器') { throw 'Stopped server action is not start.' }
    $management.PresentStatus([Newtonsoft.Json.Linq.JObject]::Parse('{"state":"running","players":"2 / 20: Steve, Alex","pendingResources":2,"pendingRuntime":false}'))
    Capture $managementSidebar $management 'server-management-clean.png' 0
    $applySection = $management.GetType().GetMethod('ApplySection', [Reflection.BindingFlags]'NonPublic,Instance')
    $managementSidebar.PageID = 2
    $applySection.Invoke($management, @([int]2)) | Out-Null
    Capture $managementSidebar $management 'server-resources-friendly.png' 0
    $managementSidebar.PageID = 3
    $applySection.Invoke($management, @([int]3)) | Out-Null
    if ($management.FindName('BtnSaveBasic').IsEnabled) { throw 'Basic settings can be saved before reading actual configuration.' }
    Capture $managementSidebar $management 'server-settings-friendly.png' 0
    $managementSidebar.PageID = 1
    $applySection.Invoke($management, @([int]1)) | Out-Null
    $management.FindName('Password').Password = 'test-only-memory-value'
    $save = $management.GetType().GetMethod('StoreProfile', [Reflection.BindingFlags]'NonPublic,Instance')
    # Suppress disk writes while exercising the existing persistence UI path.
    $profiles[1].Name = 'Renamed test server'
    $profileList[1].Name = $profiles[1].Name
    $entries['ServerProfiles'].Set([Newtonsoft.Json.JsonConvert]::SerializeObject($profileList), $null)
    $save.Invoke($management, @($profiles[1].PSObject.BaseObject)) | Out-Null
    if ($management.FindName('Password').Password -ne 'test-only-memory-value') { throw 'Profile refresh cleared the deployment password.' }
    if ($entries['ServerProfiles'].GetCache($null).Contains('test-only-memory-value')) { throw 'Password was serialized into saved profiles.' }
    if ($assembly.GetType('PCL.PageServerHome')) { throw 'Removed launch panel is still compiled.' }
    $other = [Activator]::CreateInstance($assembly.GetType('PCL.PageOtherLeft'))
    if ($other.FindName('PanItem').Children.Count -ne 2) { throw 'More menu still contains removed entries.' }
    $oldPlayCache = $entries['PlayServers'].GetCache($null)
    $playList = New-Object 'System.Collections.Generic.List[PCLCS.Server.PlayServer]'
    foreach ($label in '测试公开服','测试离线服') {
        $playServer = New-Object PCLCS.Server.PlayServer
        $playServer.Name = $label
        $playServer.Address = '127.0.0.1:1'
        $playList.Add($playServer)
    }
    $entries['PlayServers'].Set([Newtonsoft.Json.JsonConvert]::SerializeObject($playList), $null)
    try {
        $playPage = [Activator]::CreateInstance($assembly.GetType('PCL.PagePlayServers'))
        $playPage.RenderServers()
        $playPage.PresentStatus($playList[0], [Newtonsoft.Json.Linq.JObject]::Parse('{"online":true,"players":3,"maximum":20,"latency":45,"version":"1.21.1","motd":"Local test"}'))
        $playPage.PresentStatus($playList[1], [Newtonsoft.Json.Linq.JObject]::Parse('{"online":false,"message":"无法连接"}'))
        $playRows = $playPage.GetType().GetField('Rows', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($playPage)
        if ($playRows[$playList[0].Id].Info -notmatch '45 ms') { throw 'Third-party server latency was not displayed.' }
        if ($playRows[$playList[1].Id].Info -notmatch '无法连接') { throw 'Third-party offline state was not displayed.' }
        $playPage.PresentStatus($playList[1], [Newtonsoft.Json.Linq.JObject]::Parse('{"online":false,"reachable":true,"status":"status-unavailable","message":"端口可连接，但未取得游戏状态；仍可尝试加入"}'))
        if ($playRows[$playList[1].Id].Info -notmatch '仍可尝试加入') { throw 'Reachable server with no status was not distinguished.' }
        $playPage.PresentStatus($playList[0], [Newtonsoft.Json.Linq.JObject]::Parse('{"online":true,"players":null,"maximum":null,"latency":45,"version":"1.21.1","motd":"Hidden players"}'))
        if ($playRows[$playList[0].Id].Info -notmatch '人数未公开') { throw 'Hidden player counts incorrectly block status display.' }
        $emptyLeft = [Activator]::CreateInstance($assembly.GetType('PCL.MyPageLeft'))
        Capture $emptyLeft $playPage 'third-party-servers.png' 0
    } finally { $entries['PlayServers'].Set($oldPlayCache, $null) }
    if ($management.FindName('PickerFilter').SelectedIndex -ne 1) { throw 'Version picker does not default to release.' }
    $management.OpenProfile((New-Object PCLCS.Server.ServerProfile))
    if ($management.FindName('User').Text -ne 'root') { throw 'New deployment SSH user does not default to root.' }
    if ($management.FindName('ElevationMode').SelectedIndex -ne 1) { throw 'Deployment does not default to sudo elevation.' }
    if (-not $management.IsDeploymentMode) { throw 'Add server did not open deployment mode.' }
    if ($management.FindName('DeploymentCard').Visibility -ne 'Visible') { throw 'Add server has no deployment form.' }
    if ($managementSidebar.FindName('PanItem').Children[1].Visibility -ne 'Collapsed') { throw 'Management menu is visible on add-server page.' }
    if ($management.FindName('AdvancedConnection').Visibility -ne 'Collapsed') { throw 'Advanced connection settings are not collapsed.' }
    Capture $managementSidebar $management 'server-create-clean.png' 0
    Write-Output 'Server selection and deployment loading/failed/finished UI tests passed.'
} finally {
    $entries['ServerProfiles'].Set($oldCache, $null)
}
