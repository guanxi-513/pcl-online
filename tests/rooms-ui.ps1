param([string]$Root=(Split-Path -Parent $PSScriptRoot),[string]$Executable='')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
if (-not $Executable) {$Executable=Join-Path $Root 'output/CPT-Lobby/CPT.exe'}
$assembly=[Reflection.Assembly]::LoadFrom($Executable)
$app=[Activator]::CreateInstance($assembly.GetType('PCL.Application'),$true)
$app.InitializeComponent()
foreach($name in 'MeloongCore','MeloongCore.Wpf','Newtonsoft.Json','PCLCS') { [Reflection.Assembly]::LoadFrom((Join-Path $Root "PCLCS/bin/$name.dll")) | Out-Null }
[MeloongCore.Main]::Init('CPT');[MeloongCore.Wpf.Main]::Init()
$mock=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($assembly.GetType('PCL.FormMain'))
$mock.GetType().GetField('_BtnExtraBack',[Reflection.BindingFlags]'NonPublic,Instance').SetValue($mock,[Activator]::CreateInstance($assembly.GetType('PCL.MyExtraButton')))
$assembly.GetType('PCL.ModMain').GetField('FrmMain').SetValue($null,$mock)
$flags=[Reflection.BindingFlags]'NonPublic,Instance'
$roomType=$assembly.GetType('PCL.PageRooms')
$joinPage=[Activator]::CreateInstance($roomType)
$lobbyPage=[Activator]::CreateInstance($roomType);$lobbyPage.SetLobbyMode()
$client=$roomType.GetField('Client',[Reflection.BindingFlags]'NonPublic,Static').GetValue($null).Value
$user=[Newtonsoft.Json.Linq.JObject]::Parse('{"nickname":"UI test","email":"test@example.invalid"}')
$client.GetType().GetProperty('User').SetValue($client,$user)
$refresh=$roomType.GetMethod('Refresh',$flags)
foreach($view in $joinPage,$lobbyPage) {
    $refresh.Invoke($view,@()) | Out-Null
    if($view.FindName('DirectServer') -or $view.FindName('BtnDirectJoin')) { throw 'Removed direct-connect controls remain.' }
}
if($joinPage.FindName('CreateCard').Visibility -ne 'Visible' -or $joinPage.FindName('PrefixCard').Visibility -ne 'Collapsed' -or $joinPage.FindName('JoinCard').Visibility -ne 'Visible') { throw 'Room page does not contain both join and create actions.' }
if($joinPage.FindName('JoinPasswordPanel').Visibility -ne 'Collapsed') { throw 'Password is not optional in default join view.' }
if($lobbyPage.FindName('JoinCard').Visibility -ne 'Collapsed' -or $lobbyPage.FindName('CreateCard').Visibility -ne 'Collapsed' -or $lobbyPage.FindName('PrefixCard').Visibility -ne 'Collapsed') { throw 'Lobby contains unrelated forms.' }
if(-not ($lobbyPage.FindName('LobbySearch') -is $assembly.GetType('PCL.MySearchBox')) -or $lobbyPage.FindName('BtnSearchRoom') -or -not ($lobbyPage.FindName('BtnLobbyRefresh') -is $assembly.GetType('PCL.MyIconButton'))) { throw 'Lobby does not use native PCL search and refresh controls.' }
if(-not ($joinPage.FindName('BtnRefreshPrefixes') -is $assembly.GetType('PCL.MyIconButton'))) { throw 'Address refresh does not use the native icon control.' }
if($lobbyPage.FindName('AccountPanel').Visibility -ne 'Collapsed' -or $lobbyPage.FindName('Status').Visibility -ne 'Collapsed') { throw 'Lobby is not list-only.' }
if(-not ($lobbyPage -is $assembly.GetType('PCL.IRefreshable'))) { throw 'Lobby cannot refresh through the application refresh contract.' }
$lobbyRooms=[Newtonsoft.Json.Linq.JArray]::Parse('[{"code":"0123456789ABCDEF","title":"Survival room","nickname":"Alex","members":2,"capacity":8,"passwordRequired":false,"locked":false},{"code":"1234567890ABCDEF","title":"Modded room","nickname":"Steve","members":4,"capacity":8,"passwordRequired":true,"locked":false},{"code":"2345678901ABCDEF","title":"Full room","nickname":"Builder","members":8,"capacity":8,"passwordRequired":false,"locked":false},{"code":"3456789012ABCDEF","title":"Locked room","nickname":"Owner","members":1,"capacity":8,"passwordRequired":false,"locked":true}]')
$render=$roomType.GetMethod('RenderLobby',$flags)
$render.Invoke($lobbyPage,[object[]]@(,$lobbyRooms)) | Out-Null
$readSearch=$roomType.GetMethod('ReadLobbySearchCode',$flags)
$lobbyPage.FindName('LobbySearch').Text=' 0123456789abcdef '
if($readSearch.Invoke($lobbyPage,@()) -ne '0123456789ABCDEF') { throw 'Room search does not normalize case/whitespace.' }
foreach($invalid in '123','0123456789ABCDEG','0123456789ABCDEF0') {
    $lobbyPage.FindName('LobbySearch').Text=$invalid
    $rejected=$false
    try { $readSearch.Invoke($lobbyPage,@()) | Out-Null } catch { $rejected=$true }
    if(-not $rejected) { throw 'Invalid room search is accepted.' }
}
$lobbyPage.FindName('LobbySearch').Text='0123456789ABCDEF'
$roomType.GetField('LobbySearchCode',$flags).SetValue($lobbyPage,'0123456789ABCDEF')
$render.Invoke($lobbyPage,[object[]]@(,([Newtonsoft.Json.Linq.JArray]::new($lobbyRooms[0])))) | Out-Null
if($lobbyPage.FindName('LobbyItems').Children.Count -ne 1 -or $lobbyPage.FindName('LobbyListCard').Title.EndsWith('(4)')) { throw 'Search results did not replace the public list.' }
$searchTitle=$lobbyPage.FindName('LobbyListCard').Title
$render.Invoke($lobbyPage,[object[]]@(,([Newtonsoft.Json.Linq.JArray]::new()))) | Out-Null
$searchEmpty=$lobbyPage.FindName('LobbyStatus').Text
$lobbyPage.FindName('LobbySearch').Text=''
$roomType.GetField('LobbySearchCode',$flags).SetValue($lobbyPage,'')
if($readSearch.Invoke($lobbyPage,@()) -ne '') { throw 'Clearing search does not restore public query.' }
$render.Invoke($lobbyPage,[object[]]@(,([Newtonsoft.Json.Linq.JArray]::new()))) | Out-Null
if($lobbyPage.FindName('LobbyStatus').Text -eq $searchEmpty) { throw 'Search empty state is not distinct from public empty state.' }
$render.Invoke($lobbyPage,[object[]]@(,$lobbyRooms)) | Out-Null
if($lobbyPage.FindName('LobbyItems').Children.Count -ne 4 -or $lobbyPage.FindName('LobbyStatus').Text -ne '' -or $lobbyPage.FindName('LobbyListCard').Visibility -ne 'Visible') { throw 'Lobby list rendering failed.' }
if($lobbyPage.FindName('LobbyItems').Parent -ne $lobbyPage.FindName('LobbyListCard') -or -not $lobbyPage.FindName('LobbyListCard').Title.EndsWith('(4)')) { throw 'Lobby does not use the native PCL titled list container.' }
foreach($item in $lobbyPage.FindName('LobbyItems').Children) {
    if($item.Info.Contains('ABCDEF') -or $item.Height -ne 42 -or $item.FontSize -ne 14 -or -not $item.SnapsToDevicePixels) { throw 'Lobby rows do not match native PCL list metrics.' }
}
$details=$roomType.GetMethod('LobbyRoomDetails',$flags).Invoke($lobbyPage,[object[]]@(,$lobbyRooms[0]))
if(-not $details.Contains('0123456789ABCDEF') -or -not $details.Contains('Alex')) { throw 'Room details are missing from popup.' }
$render.Invoke($lobbyPage,[object[]]@(,([Newtonsoft.Json.Linq.JArray]::new()))) | Out-Null
if($lobbyPage.FindName('LobbyItems').Children.Count -ne 0 -or -not $lobbyPage.FindName('LobbyStatus').Text -or $lobbyPage.FindName('LobbyListCard').Visibility -ne 'Collapsed') { throw 'Lobby empty state is missing.' }
$render.Invoke($lobbyPage,[object[]]@(,$lobbyRooms)) | Out-Null
$sidebar=[Activator]::CreateInstance($assembly.GetType('PCL.PageMultiplayerLeft'))
if($sidebar.FindName('ItemCreate') -or $sidebar.FindName('PanItem').Children.Count -ne 3) { throw 'Removed create-room sidebar remains.' }
foreach($entry in $sidebar.FindName('PanItem').Children) {
    if(@($entry.Buttons).Count -ne 1 -or -not (@($entry.Buttons)[0] -is $assembly.GetType('PCL.MyIconButton'))) { throw 'Multiplayer sidebar refresh entry is missing.' }
}
$sidebar.PageID=1
$roomPage=$sidebar.PageGet()
if($roomPage.FindName('CreateCard').Visibility -ne 'Visible') { throw 'Merged create-room workflow is inaccessible.' }
$sidebar.PageID=3
if($sidebar.PageGet() -ne $roomPage -or $sidebar.PageID -ne 1) { throw 'Old host route does not redirect to rooms.' }
$client.GetType().GetProperty('Code').SetValue($client,'0123456789ABCDEF')
$client.GetType().GetProperty('RoomInfo').SetValue($client,[Newtonsoft.Json.Linq.JObject]::Parse('{"title":"Test room","nickname":"Test host","members":2,"capacity":8,"public":true,"passwordRequired":false}'))
$client.GetType().GetProperty('TargetAddress').SetValue($client,'127.0.0.1:25565')
$sourceCancel=New-Object Threading.CancellationTokenSource
$client.GetType().GetField('roomCancel',$flags).SetValue($client,$sourceCancel)
foreach($hosting in $true,$false) {
    $client.GetType().GetProperty('Hosting').SetValue($client,$hosting)
    $refresh.Invoke($joinPage,@()) | Out-Null
    $refresh.Invoke($lobbyPage,@()) | Out-Null
    if($joinPage.FindName('ActiveRoom').Visibility -ne 'Visible' -or $joinPage.FindName('RoomActions').Visibility -ne 'Collapsed' -or $joinPage.FindName('PrefixCard').Visibility -ne 'Collapsed' -or $joinPage.FindName('AccountPanel').Visibility -ne 'Collapsed') { throw 'Room page does not show only the current room after entering.' }
    if($lobbyPage.FindName('ActiveRoom').Visibility -ne 'Collapsed' -or $lobbyPage.FindName('RoomActions').Visibility -ne 'Visible' -or $lobbyPage.FindName('PrefixCard').Visibility -ne 'Collapsed') { throw 'Current room leaks into lobby.' }
    if($joinPage.FindName('CurrentRoomName').Text -ne 'Test room' -or -not $joinPage.FindName('CurrentRoomSummary').Text.Contains('Test host')) { throw 'Current room identity is missing.' }
}
$client.GetType().GetProperty('Hosting').SetValue($client,$true)
$roomType.GetField('PrefixExpanded',$flags).SetValue($joinPage,$true)
$refresh.Invoke($joinPage,@()) | Out-Null
if($joinPage.FindName('PrefixCard').Visibility -ne 'Visible' -or $joinPage.FindName('PrefixCard').Parent.Parent -ne $joinPage.FindName('ActiveRoom')) { throw 'Address management is not inside current room.' }
$client.StopRoom();$sourceCancel.Dispose()
$refresh.Invoke($joinPage,@()) | Out-Null
if($joinPage.FindName('RoomActions').Visibility -ne 'Visible' -or $joinPage.FindName('ActiveRoom').Visibility -ne 'Collapsed' -or $joinPage.FindName('PrefixCard').Visibility -ne 'Collapsed') { throw 'Leaving does not restore room actions.' }
$source=Get-Content -LiteralPath (Join-Path $Root 'Plain Craft Launcher 2/Pages/PageServer/PageRooms.xaml.vb') -Raw -Encoding UTF8
if($source.Contains('Private Sub DirectJoin') -or $source.Contains('RoomNumber.Text = Entry')) { throw 'Obsolete two-step lobby/direct-connect action remains.' }
if(-not $source.Contains('CType(Me, IRefreshable).Refresh()') -or -not $source.Contains('Await Client.Value.RefreshRoom()')) { throw 'Manual refresh controls do not share the PCL refresh contract.' }
$client.GetType().GetProperty('User').SetValue($client,$null)
$xml=New-Object Xml.XmlDocument
$xml.Load((Join-Path $Root 'Plain Craft Launcher 2/Pages/PageServer/PageRooms.xaml'))
$ns=New-Object Xml.XmlNamespaceManager($xml.NameTable)
$ns.AddNamespace('w','http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$node=$xml.SelectSingleNode('//w:MyScrollViewer',$ns)
if (-not $node) { $node=$xml.DocumentElement.FirstChild }
$node.SetAttribute('xmlns:local','clr-namespace:PCL;assembly=CPT')
$node.SetAttribute('xmlns:x','http://schemas.microsoft.com/winfx/2006/xaml')
$page=[Windows.Markup.XamlReader]::Parse($node.OuterXml.Replace('clr-namespace:PCL"','clr-namespace:PCL;assembly=CPT"'))
$page.FindName('BtnLobbyRefresh').Logo=$lobbyPage.FindName('BtnLobbyRefresh').Logo
$page.FindName('BtnRefreshPrefixes').Logo=$joinPage.FindName('BtnRefreshPrefixes').Logo
function Capture($name,$width=655,$height=650) {
    $animation=$assembly.GetType('PCL.ModAnimation').GetProperty('AniControlEnabled')
    $oldAnimation=$animation.GetValue($null,$null)
    $animation.SetValue($null,$oldAnimation+1,$null)
    $window=New-Object Windows.Window
    $window.Width=$width;$window.Height=$height;$window.WindowStyle='None';$window.Left=-2000;$window.Top=-2000;$window.ShowInTaskbar=$false;$window.Content=$page
    try {
        $window.Show();$window.UpdateLayout()
        foreach($cardName in 'JoinCard','CreateCard','ActiveRoom','LobbyListCard') {
            $card=$page.FindName($cardName)
            if($card.Visibility -eq 'Visible') { $card.TriggerForceResize();$card.InvalidateMeasure() }
        }
        $page.InvalidateMeasure();$window.UpdateLayout()
        if($name -like 'cpt-room-lobby-*.png') {
            $card=$page.FindName('LobbyListCard')
            foreach($item in $page.FindName('LobbyItems').Children) {
                $bottom=$item.TranslatePoint((New-Object Windows.Point($item.ActualWidth,$item.ActualHeight)),$card)
                if($bottom.Y -gt $card.ActualHeight -or $bottom.X -gt $card.ActualWidth) { throw 'Native lobby list row is clipped.' }
            }
        }
        if($name -eq 'cpt-room-create-bottom.png' -or $name -eq 'cpt-game-prefixes.png') {
            $page.ScrollToEnd();$window.UpdateLayout()
        }
        if($name -eq 'cpt-room-create-bottom.png') {
            $card=$page.FindName('CreateCard');$button=$page.FindName('BtnCreate')
            $bottom=$button.TranslatePoint((New-Object Windows.Point(0,$button.ActualHeight)),$card).Y
            if($bottom -gt $card.ActualHeight) { throw 'Create button is clipped within its form.' }
        }
        if ($name -like 'cpt-room-join-*.png') {
            $card=$page.FindName('JoinCard')
            $card.TriggerForceResize();$card.InvalidateMeasure();$page.InvalidateMeasure();$window.UpdateLayout()
            $button=$page.FindName('BtnJoin')
            $bottom=$button.TranslatePoint((New-Object Windows.Point(0,$button.ActualHeight)),$card).Y
            if ($bottom -gt $card.ActualHeight) { throw 'Join button is clipped after password expansion.' }
        }
        if ($name -eq 'cpt-room-register.png') {
            $card=$page.FindName('LoginCard')
            $card.TriggerForceResize();$card.InvalidateMeasure();$page.InvalidateMeasure();$window.UpdateLayout()
            $bottom=$page.FindName('BtnAuth').TranslatePoint((New-Object Windows.Point(0,$page.FindName('BtnAuth').ActualHeight)),$card).Y
            Write-Output "Register card height=$($card.ActualHeight), code visibility=$($page.FindName('CodePanel').Visibility), submit bottom=$bottom"
            if ($bottom -gt $card.ActualHeight) { throw 'Registration submit button is clipped.' }
        }
        $bitmap=New-Object Windows.Media.Imaging.RenderTargetBitmap($width,$height,96,96,[Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($window)
        $encoder=New-Object Windows.Media.Imaging.PngBitmapEncoder
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream=[IO.File]::Create((Join-Path $Root "output/playwright/$name"))
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
    } finally { $window.Close();$window.Content=$null;$animation.SetValue($null,$oldAnimation,$null) }
}
Capture 'cpt-room-login.png'
$page.FindName('CodePanel').Visibility='Visible';$page.FindName('NicknamePanel').Visibility='Visible'
$page.FindName('AuthMode').SelectedIndex=1
$page.FindName('LoginCard').TriggerForceResize()
Capture 'cpt-room-register.png'
$page.FindName('LoginCard').Visibility='Collapsed';$page.FindName('AccountPanel').Visibility='Visible';$page.FindName('RoomActions').Visibility='Visible'
$page.FindName('UserSummary').Text='CPT Test User'
if($page.FindName('CreateCard').Visibility -ne 'Visible') { throw 'Create form is missing from rooms.' }
Capture 'cpt-room-join-create.png'
Capture 'cpt-room-create-bottom.png'
$page.ScrollToHome()
$page.FindName('JoinHasPassword').Checked=$true
$page.FindName('JoinPasswordPanel').Visibility='Visible'
$page.FindName('JoinCard').TriggerForceResize()
Capture 'cpt-room-join-password.png'
$page.FindName('JoinCard').Visibility='Collapsed'
$page.FindName('CreateCard').Visibility='Visible'
$page.FindName('CreateCard').TriggerForceResize()
Capture 'cpt-room-create.png'
$page.FindName('TargetMode').SelectedIndex=1
$page.FindName('LanTargetPanel').Visibility='Collapsed';$page.FindName('ServerTargetPanel').Visibility='Visible'
$page.FindName('CreateCard').TriggerForceResize()
$page.ScrollToVerticalOffset(250)
Capture 'cpt-room-create-server.png'
$page.FindName('CreateCard').Visibility='Collapsed';$page.FindName('LobbyPanel').Visibility='Visible'
$page.FindName('AccountPanel').Visibility='Collapsed'
$page.FindName('Status').Visibility='Collapsed'
$items=$page.FindName('LobbyItems')
$page.FindName('LobbyListCard').Visibility='Visible'
$page.FindName('LobbyListCard').Title=$lobbyPage.FindName('LobbyListCard').Title
foreach($item in @($lobbyPage.FindName('LobbyItems').Children)) {
    $lobbyPage.FindName('LobbyItems').Children.Remove($item)
    $items.Children.Add($item) | Out-Null
}
$page.ScrollToHome()
Capture 'cpt-room-lobby-list.png'
Capture 'cpt-room-lobby-wide.png' 1000 650
$page.FindName('LobbySearch').Text='0123456789ABCDEF'
$page.FindName('LobbyListCard').Title=$searchTitle
while($items.Children.Count -gt 1) { $items.Children.RemoveAt($items.Children.Count-1) }
Capture 'cpt-room-lobby-search.png'
$page.FindName('LobbySearch').Text=''
$page.FindName('AccountPanel').Visibility='Visible'
$page.FindName('Status').Visibility='Visible'
$page.FindName('RoomActions').Visibility='Collapsed';$page.FindName('ActiveRoom').Visibility='Visible';$page.FindName('OwnerSettings').Visibility='Visible'
$page.FindName('AccountPanel').Visibility='Collapsed'
$page.FindName('OwnerTitle').Text='Test room';$page.FindName('RoomCode').Text='0123456789ABCDEF';$page.FindName('GameAddress').Text='127.0.0.1:25565'
$page.FindName('CurrentRoomName').Text='Test room'
$page.FindName('CurrentRoomSummary').Text='Test host - 2/8'
$page.FindName('ActiveRoom').TriggerForceResize()
Capture 'cpt-room-owner-settings.png'
$page.FindName('PrefixCard').Visibility='Visible'
$page.FindName('OwnerSettings').Visibility='Collapsed'
$prefixItems=$page.FindName('PrefixItems')
foreach($address in 'survival.cpts.pw',('a'*32+'.cpts.pw')) {
    $line=New-Object Windows.Controls.StackPanel
    $line.Margin=New-Object Windows.Thickness(0,0,0,15)
    $label=New-Object Windows.Controls.TextBlock
    $label.Text=$address;$label.FontSize=15;$label.TextWrapping='Wrap'
    $line.Children.Add($label) | Out-Null
    $buttons=New-Object Windows.Controls.WrapPanel
    $copyLabel=-join ([char[]]@(0x590d,0x5236,0x6e38,0x620f,0x5730,0x5740))
    $publishLabel=-join ([char[]]@(0x53d1,0x5e03,0x5230,0x5f53,0x524d,0x623f,0x95f4))
    $releaseLabel=-join ([char[]]@(0x91ca,0x653e,0x524d,0x7f00))
    foreach($entry in @(@($copyLabel,130),@($publishLabel,150),@($releaseLabel,110))) {
        $button=[Activator]::CreateInstance($assembly.GetType('PCL.MyButton'))
        $button.Text=$entry[0];$button.Width=$entry[1];$button.Height=32;$button.Margin=New-Object Windows.Thickness(0,8,8,8)
        $buttons.Children.Add($button) | Out-Null
    }
    $line.Children.Add($buttons) | Out-Null
    $prefixItems.Children.Add($line) | Out-Null
}
$page.FindName('PrefixStatus').Text='2 / 2'
$page.FindName('ActiveRoom').TriggerForceResize()
$page.FindName('ActiveRoom').InvalidateMeasure();$page.InvalidateMeasure()
$page.ScrollToHome()
Capture 'cpt-game-prefixes.png'
Write-Output 'CPT merged join/create, current-room-only host/guest states, lobby isolation, address management and exit reset checks passed; minimum-width views rendered.'
