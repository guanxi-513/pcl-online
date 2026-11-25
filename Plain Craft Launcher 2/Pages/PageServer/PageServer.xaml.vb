Imports PCLCS.Server

Public Class PageServer
    Private CurrentProfile As ServerProfile
    Public ReadOnly Property IsDeploymentMode As Boolean
        Get
            Return CurrentProfile Is Nothing OrElse Not CurrentProfile.Deployed
        End Get
    End Property
    Private Busy As Boolean
    Private PickerTarget As String
    Private PickerCore As String
    Private PickerMinecraft As String
    Private PickerEntries As List(Of ServerVersionChoice)
    Private PickerGeneration As Integer
    Private ShowingProfile As Boolean
    Private KnownState As String = "unknown"
    Private ImportedPack As JObject
    Private ImportedPackPath As String
    Private RootSecret As String
    Private RootSecretTarget As String
    Private ConfigRevision As String
    Private ReadOnly ConfigDefinitions As New Dictionary(Of String, JObject)
    Private ReadOnly ConfigEditors As New Dictionary(Of String, FrameworkElement)
    Private ReadOnly ConfigRows As New Dictionary(Of String, FrameworkElement)
    Private ReadOnly ConfigGroups As New Dictionary(Of String, StackPanel)

    Public Sub New()
        InitializeComponent()
        AddHandler ConfigSearch.TextChanged, Sub() FilterConfigFields()
        AddHandler DeploySource.SelectionChanged, Sub()
                                                      PackImportPanel.Visibility = If(DeploySource.SelectedIndex = 1, Visibility.Visible, Visibility.Collapsed)
                                                      OfficialCorePanel.Visibility = If(DeploySource.SelectedIndex = 1, Visibility.Collapsed, Visibility.Visible)
                                                      OfficialLoaderPanel.Visibility = OfficialCorePanel.Visibility
                                                      DeploymentCard.TriggerForceResize()
                                                  End Sub
        AddHandler LinkJoin.Click, Sub() JoinServer(Nothing, Nothing)
        AddHandler LinkResources.Click, Sub() OpenResources(Nothing, Nothing)
        AddHandler LinkCopyAddress.Click, Sub() CopyGameAddress(Nothing, Nothing)
        ShowProfile(New ServerProfile)
        AddHandler Core.SelectionChanged, Sub() If Not ShowingProfile Then LoaderVersion.Text = ""
        AddHandler RuntimeCore.SelectionChanged, Sub() If Not ShowingProfile Then RuntimeLoaderVersion.Text = ""
        AddHandler PickerFilter.SelectionChanged, Sub() RenderVersionPicker()
        AddHandler PickerSearch.TextChanged, Sub() RenderVersionPicker()
        AddHandler AdvancedDeploy.Change, Sub()
                                             AdvancedConnection.Visibility = If(AdvancedDeploy.Checked, Visibility.Visible, Visibility.Collapsed)
                                             AdvancedRuntime.Visibility = AdvancedConnection.Visibility
                                         End Sub
        AddHandler RuntimeAdvanced.Change, Sub() RuntimeAdvancedPanel.Visibility = If(RuntimeAdvanced.Checked, Visibility.Visible, Visibility.Collapsed)
        AddHandler ShowResourceSearch.Change,
            Sub()
                ResourceSearchCard.Visibility = If(ShowResourceSearch.Checked, Visibility.Visible, Visibility.Collapsed)
                ResourceUrlCard.Visibility = ResourceSearchCard.Visibility
            End Sub
    End Sub

    Public Sub SelectSection(Index As Integer)
        If IsDeploymentMode Then Index = 0
        CloseVersionPicker(Nothing, Nothing)
        If IsLoaded Then
            Sections.IsHitTestVisible = False
            AniStart({AaOpacity(Sections, -Sections.Opacity, 100),
                AaCode(Sub()
                           ApplySection(Index)
                           PanBack.ScrollToHome()
                       End Sub, 100),
                AaOpacity(Sections, 1, 180, 120, New AniEaseOutFluent),
                AaCode(Sub() Sections.IsHitTestVisible = True, 300)}, "Server section change")
            Return
        End If
        ApplySection(Index)
        PanBack.ScrollToHome()
    End Sub

    Private Sub ApplySection(Index As Integer)
        For Each SectionTab In ManagementTabs.Children.OfType(Of MyRadioButton)()
            SectionTab.SetChecked(CInt(SectionTab.Tag) = Index, False, False)
        Next
        For i = 0 To Sections.Children.Count - 1
            DirectCast(Sections.Children(i), FrameworkElement).Visibility = If(i = Index, Visibility.Visible, Visibility.Collapsed)
        Next
    End Sub
    Private Sub ChangeManagementTab(sender As MyRadioButton, raiseByMouse As Boolean) Handles TabStatus.Check, TabResources.Check, TabSettings.Check, TabPlayers.Check, TabConnection.Check
        If Not raiseByMouse OrElse IsDeploymentMode Then Return
        Dim Index = CInt(sender.Tag)
        If FrmServerLeft IsNot Nothing Then FrmServerLeft.PageID = Index
        SelectSection(Index)
    End Sub

    Public Sub OpenProfile(Profile As ServerProfile)
        If Busy Then Throw New InvalidOperationException("当前服务器正在执行操作，请稍后再切换。")
        ShowProfile(Profile)
        If FrmServerLeft Is Nothing Then FrmServerLeft = New PageServerLeft
        FrmServerLeft.ConfigureMode(IsDeploymentMode)
        FrmServerLeft.PageID = If(IsDeploymentMode, 0, 1)
        CType(FrmServerLeft.PanItem.Children(FrmServerLeft.PageID), MyListItem).SetChecked(True, False, False)
        SelectSection(FrmServerLeft.PageID)
    End Sub

    Private Sub RenderResources(Panel As StackPanel, Entries As JToken, TitleKey As String)
        Panel.Children.Clear()
        For Each Entry As JObject In Entries
            Dim Description = If(Entry("description")?.ToString(), If(Entry("category")?.ToString(), If(Entry("enabled")?.Value(Of Boolean)() = True, "已启用", "已禁用")))
            Dim Item As New MyListItem With {.Title = Entry(TitleKey).ToString(), .Info = Description, .Tag = Entry, .Type = MyListItem.CheckType.RadioBox, .Height = 58, .MinPaddingRight = 12}
            Panel.Children.Add(Item)
        Next
        If Panel.Children.Count = 0 Then Panel.Children.Add(New TextBlock With {.Text = "暂无资源", .Opacity = 0.5, .Margin = New Thickness(4, 10, 4, 10)})
    End Sub

    Private Function SelectedResource(Panel As StackPanel) As JObject
        Dim Selected = Panel.Children.OfType(Of MyListItem)().FirstOrDefault(Function(Item) Item.Checked)
        Return If(Selected Is Nothing, Nothing, DirectCast(Selected.Tag, JObject))
    End Function

    Private Async Sub ChooseMinecraft(sender As Object, e As MouseButtonEventArgs)
        PickerTarget = CType(sender, FrameworkElement).Tag.ToString()
        PickerCore = "minecraft"
        Await LoadVersionPicker(False)
    End Sub

    Private Async Sub ChooseLoader(sender As Object, e As MouseButtonEventArgs)
        PickerTarget = CType(sender, FrameworkElement).Tag.ToString()
        PickerCore = CType(If(PickerTarget = "runtime", RuntimeCore.SelectedItem, Core.SelectedItem), MyComboBoxItem).Content.ToString()
        PickerMinecraft = If(PickerTarget = "runtime", RuntimeVersion.Text, GameVersion.Text)
        If PickerCore = "Vanilla" Then
            OperationStatus.Text = "原版服务器无需加载器。"
            Return
        End If
        Await LoadVersionPicker(False)
    End Sub

    Private Async Function LoadVersionPicker(Force As Boolean) As Task
        If Busy Then Return
        Busy = True
        PickerGeneration += 1
        Dim Generation = PickerGeneration
        Dim Kind = PickerCore
        Dim Minecraft = PickerMinecraft
        Sections.Visibility = Visibility.Collapsed
        VersionPicker.Visibility = Visibility.Visible
        If IsLoaded Then
            VersionPicker.Opacity = 0
            AniStart(AaOpacity(VersionPicker, 1, 200, Ease:=New AniEaseOutFluent), "Server version picker")
        End If
        PickerAutomatic.Visibility = If(Kind = "minecraft", Visibility.Collapsed, Visibility.Visible)
        PickerLoading.Visibility = Visibility.Visible
        PickerStatus.Text = "正在获取版本列表……"
        PickerItems.Children.Clear()
        PickerEntries = Nothing
        Try
            Dim Entries = Await Task.Run(Function() If(Kind = "minecraft", ModServerCatalog.MinecraftVersions(Force), ModServerCatalog.LoaderVersions(Kind, Minecraft, Force)))
            If Generation <> PickerGeneration Then Return
            PickerEntries = Entries
            PickerStatus.Text = If(Entries.Count = 0, "没有可用版本。", If(Kind = "minecraft", "Minecraft", Kind & " · " & Minecraft) & "：" & Entries.Count & " 个版本")
            RenderVersionPicker()
        Catch ex As Exception
            If Generation = PickerGeneration Then PickerStatus.Text = "列表获取失败：" & ex.Message
        Finally
            If Generation = PickerGeneration Then PickerLoading.Visibility = Visibility.Collapsed
            Busy = False
        End Try
    End Function

    Private Sub RenderVersionPicker()
        If PickerEntries Is Nothing OrElse PickerItems Is Nothing Then Return
        PickerItems.Children.Clear()
        Dim Filter = CType(PickerFilter.SelectedItem, MyComboBoxItem).Tag.ToString()
        Dim Search = PickerSearch.Text.Trim()
        For Each Entry In PickerEntries
            If Search <> "" AndAlso Entry.Id.IndexOf(Search, StringComparison.OrdinalIgnoreCase) < 0 Then Continue For
            If Filter = "release" AndAlso Not Entry.Stable OrElse Filter = "snapshot" AndAlso Entry.Stable Then Continue For
            Dim Choice = Entry
            PickerItems.Children.Add(Choice.CreateItem(Sub(sender, args) SelectVersion(Choice)))
        Next
    End Sub

    Private Sub SelectVersion(Entry As ServerVersionChoice)
        If PickerCore = "minecraft" Then
            If PickerTarget = "runtime" Then
                RuntimeVersion.Text = Entry.Id
                RuntimeLoaderVersion.Text = ""
            Else
                GameVersion.Text = Entry.Id
                LoaderVersion.Text = ""
            End If
        ElseIf PickerTarget = "runtime" Then
            RuntimeLoaderVersion.Text = Entry.Id
        Else
            LoaderVersion.Text = Entry.Id
        End If
        CloseVersionPicker(Nothing, Nothing)
    End Sub

    Private Sub CloseVersionPicker(sender As Object, e As MouseButtonEventArgs)
        If VersionPicker Is Nothing Then Return
        PickerGeneration += 1
        VersionPicker.Visibility = Visibility.Collapsed
        Sections.Visibility = Visibility.Visible
        If IsLoaded Then AniStart(AaOpacity(Sections, 1 - Sections.Opacity, 180, Ease:=New AniEaseOutFluent), "Server version picker")
        PanBack.ScrollToHome()
    End Sub

    Private Async Sub ReloadVersionPicker(sender As Object, e As MouseButtonEventArgs)
        Await LoadVersionPicker(True)
    End Sub

    Private Sub SelectAutomaticLoader(sender As Object, e As MouseButtonEventArgs)
        If PickerCore = "minecraft" Then Return
        If PickerTarget = "runtime" Then
            RuntimeLoaderVersion.Text = ""
        Else
            LoaderVersion.Text = ""
        End If
        CloseVersionPicker(Nothing, Nothing)
    End Sub

    Private Sub PageUnloaded(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        Password.Clear()
        RootSecret = Nothing
        RootSecretTarget = Nothing
        If CurrentProfile IsNot Nothing Then CurrentProfile.RootPassword = Nothing
    End Sub

    Private Sub ShowProfile(Profile As ServerProfile)
        RootSecret = Nothing
        RootSecretTarget = Nothing
        ShowingProfile = True
        CurrentProfile = Profile
        ImportedPack = Nothing
        ImportedPackPath = Nothing
        DeploySource.SelectedIndex = 0
        PackSummary.Text = ""
        ManagementHeading.Visibility = If(IsDeploymentMode, Visibility.Collapsed, Visibility.Visible)
        ManagementName.Text = Profile.Name
        ManagementAddress.Text = Profile.Core & " " & Profile.Version & " · " & Profile.Host & ":" & Profile.GamePort
        DeploymentCard.Visibility = If(IsDeploymentMode, Visibility.Visible, Visibility.Collapsed)
        BtnSaveConnection.Visibility = If(IsDeploymentMode, Visibility.Collapsed, Visibility.Visible)
        AdvancedDeploy.Checked = False
        AdvancedConnection.Visibility = Visibility.Collapsed
        AdvancedRuntime.Visibility = Visibility.Collapsed
        RuntimeAdvanced.Checked = False
        RuntimeAdvancedPanel.Visibility = Visibility.Collapsed
        ShowResourceSearch.Checked = False
        ResourceSearchCard.Visibility = Visibility.Collapsed
        ResourceUrlCard.Visibility = Visibility.Collapsed
        ProfileName.Text = Profile.Name
        Host.Text = Profile.Host
        SshPort.Text = Profile.SshPort.ToString()
        User.Text = Profile.User
        ElevationMode.SelectedIndex = Array.IndexOf({"auto", "sudo", "su"}, Profile.ElevationMethod)
        If ElevationMode.SelectedIndex < 0 Then ElevationMode.SelectedIndex = 1
        OperatingSystem.SelectedIndex = If(Profile.Windows, 1, 0)
        RemoteRoot.Text = Profile.Root
        GameAddress.Text = Profile.GameAddress
        Core.SelectedIndex = Array.IndexOf({"Vanilla", "Paper", "Fabric", "Forge", "NeoForge"}, Profile.Core)
        Category.SelectedIndex = If(Profile.Core = "Paper", 1, 0)
        GameVersion.Text = Profile.Version
        Memory.Text = Profile.Memory.ToString()
        GamePort.Text = Profile.GamePort.ToString()
        JavaMajor.SelectedIndex = Array.IndexOf({0, 8, 11, 16, 17, 21, 25}, Profile.JavaMajor)
        JavaPath.Text = Profile.JavaPath
        LoaderVersion.Text = Profile.LoaderVersion
        RuntimeCore.SelectedIndex = Core.SelectedIndex
        RuntimeVersion.Text = Profile.Version
        RuntimeJavaMajor.SelectedIndex = JavaMajor.SelectedIndex
        RuntimeJavaPath.Text = Profile.JavaPath
        RuntimeLoaderVersion.Text = Profile.LoaderVersion
        RuntimeMemory.Text = Profile.Memory.ToString()
        RuntimePort.Text = Profile.GamePort.ToString()
        RuntimeInfo.Text = ""
        PendingFiles.Children.Clear()
        KnownState = "unknown"
        ServerState.Text = "尚未查询"
        ServerPlayers.Text = "在线玩家：未查询"
        PendingSummary.Text = "待应用变更：未查询"
        LogCard.IsSwapped = True
        BtnSaveBasic.IsEnabled = False
        BtnSavePanelBottom.IsEnabled = False
        ConfigRevision = Nothing
        ConfigDefinitions.Clear()
        ConfigEditors.Clear()
        ConfigRows.Clear()
        ConfigGroups.Clear()
        ConfigFieldsPanel.Children.Clear()
        ConfigSearch.Text = ""
        ConfigPanelStatus.Text = "尚未读取配置"
        BtnPower.Text = "查询服务器状态"
        Fingerprint.Text = Profile.Fingerprint
        Password.Clear()
        AcceptEula.Checked = False
        ConfigText.Clear()
        InstalledFiles.Children.Clear()
        SearchResults.Children.Clear()
        InstalledFiles.Children.Add(New TextBlock With {.Text = "尚未读取资源", .Opacity = 0.5, .Margin = New Thickness(4, 10, 4, 10)})
        PendingFiles.Children.Add(New TextBlock With {.Text = "尚未读取待安装项", .Opacity = 0.5, .Margin = New Thickness(4, 10, 4, 10)})
        ShowingProfile = False
        DeploymentResult.Text = ""
    End Sub

    Private Function ReadProfile() As ServerProfile
        Dim Result As New ServerProfile With {
            .Name = ProfileName.Text.Trim(), .Host = Host.Text.Trim(), .SshPort = Integer.Parse(SshPort.Text),
            .User = User.Text.Trim(), .Windows = OperatingSystem.SelectedIndex = 1,
            .Root = RemoteRoot.Text.Trim(), .GameAddress = GameAddress.Text.Trim(),
            .Core = CType(Core.SelectedItem, ComboBoxItem).Content.ToString(), .Version = GameVersion.Text.Trim(),
            .Memory = Integer.Parse(Memory.Text), .GamePort = Integer.Parse(GamePort.Text),
            .JavaMajor = CInt(CType(JavaMajor.SelectedItem, ComboBoxItem).Tag), .JavaPath = JavaPath.Text.Trim(),
            .LoaderVersion = LoaderVersion.Text.Trim(), .ElevationMethod = DirectCast(ElevationMode.SelectedItem, MyComboBoxItem).Tag.ToString()}
        Dim Identity = Result.Host & ":" & Result.SshPort & ":" & Result.User & ":" & Result.ElevationMethod
        If RootSecretTarget = Identity Then Result.RootPassword = RootSecret
        If CurrentProfile IsNot Nothing AndAlso CurrentProfile.Host = Result.Host AndAlso CurrentProfile.SshPort = Result.SshPort AndAlso CurrentProfile.User = Result.User AndAlso CurrentProfile.ElevationMethod = Result.ElevationMethod Then
            Result.UseSu = CurrentProfile.UseSu
            Result.ActiveElevation = CurrentProfile.ActiveElevation
        End If
        If CurrentProfile IsNot Nothing Then Result.Id = CurrentProfile.Id
        If CurrentProfile IsNot Nothing Then
            Result.Deployed = CurrentProfile.Deployed
            Result.ClientInstance = CurrentProfile.ClientInstance
            Result.ClientRevision = CurrentProfile.ClientRevision
            Result.ClientPack = CurrentProfile.ClientPack
        End If
        Result.Validate()
        If CurrentProfile IsNot Nothing AndAlso CurrentProfile.Host = Result.Host AndAlso CurrentProfile.SshPort = Result.SshPort Then Result.Fingerprint = CurrentProfile.Fingerprint
        Return Result
    End Function

    Private Sub StoreProfile(Profile As ServerProfile)
        ModServerTasks.SaveProfile(JsonConvert.DeserializeObject(Of ServerProfile)(JsonConvert.SerializeObject(Profile)))
        CurrentProfile = Profile
        Fingerprint.Text = Profile.Fingerprint
        ManagementName.Text = Profile.Name
        ManagementAddress.Text = Profile.Core & " " & Profile.Version & " · " & Profile.Host & ":" & Profile.GamePort
        DeploymentCard.Visibility = If(IsDeploymentMode, Visibility.Visible, Visibility.Collapsed)
        BtnSaveConnection.Visibility = If(IsDeploymentMode, Visibility.Collapsed, Visibility.Visible)
    End Sub
    Private Sub SaveProfile(sender As Object, e As MouseButtonEventArgs)
        If Busy Then Return
        If CurrentProfile IsNot Nothing AndAlso ModServerTasks.Find(CurrentProfile)?.Loader.State = LoadState.Loading Then
            OperationStatus.Text = "部署期间不能修改连接信息，请等待部署完成。"
            Return
        End If
        Try
            StoreProfile(ReadProfile())
            OperationStatus.Text = "连接信息已保存，密码未保存。"
        Catch ex As Exception
            OperationStatus.Text = ex.Message
        End Try
    End Sub

    Private Async Function WithConnection(Action As Func(Of ServerProfile, String, JObject)) As Task(Of JObject)
        Dim Profile = ReadProfile()
        Dim Secret = Password.Password
        If Secret = "" Then
            Secret = MyMsgBoxInput("连接服务器", "请输入这台服务器的 SSH 密码，本次页面内使用，离开页面即清除。", MaskInput:=True)
            If String.IsNullOrEmpty(Secret) Then Throw New OperationCanceledException()
            Password.Password = Secret
        End If
        For Attempt = 1 To 3
        Try
            Return Await Task.Run(Function() Action(Profile, Secret))
        Catch ex As RootPrivilegeRequiredException
            RootSecret = MyMsgBoxInput("取得 root 权限", If(ex.Method = "sudo", "请输入当前 SSH 用户的 sudo 密码，服务器需授予该用户 sudo 权限。", "请输入 root 密码，通过 su - root 切换。") & "密码仅在本次页面或部署任务内存中使用，不保存。", HintText:=If(ex.Method = "sudo", "sudo 密码", "root 密码"), MaskInput:=True)
            If String.IsNullOrEmpty(RootSecret) Then Throw New OperationCanceledException()
            RootSecretTarget = Profile.Host & ":" & Profile.SshPort & ":" & Profile.User & ":" & Profile.ElevationMethod
            Profile.RootPassword = RootSecret
            Profile.ActiveElevation = ex.Method
            Profile.UseSu = True
            If CurrentProfile IsNot Nothing Then
                CurrentProfile.UseSu = True
                CurrentProfile.ActiveElevation = ex.Method
            End If
        Catch ex As HostKeyConfirmationException
            Dim Previous = If(Profile.Fingerprint = "", "首次连接", "警告：服务器主机指纹已改变，可能存在中间人攻击。")
            If MyMsgBox(Previous & vbCrLf & Profile.Host & vbCrLf & ex.Fingerprint & vbCrLf & "请与服务器管理员核对指纹。", "SSH 主机校验", "已核对，信任", "取消", IsWarn:=True) <> 1 Then Throw New OperationCanceledException()
            Profile.Fingerprint = ex.Fingerprint
            StoreProfile(Profile)
        End Try
        Next
        Throw New InvalidOperationException("未完成服务器身份或 root 权限验证，请重新连接。")
    End Function

    Private Async Function Perform(Action As Func(Of Task)) As Task
        If Busy Then Return
        Busy = True
        Sections.IsEnabled = False
        OperationStatus.Text = "正在执行……"
        Try
            Await Action()
            OperationStatus.Text = "操作完成"
        Catch ex As OperationCanceledException
            OperationStatus.Text = "已取消"
            If ConfigRevision IsNot Nothing Then UpdateConfigDirtyStatus()
        Catch ex As Exception
            OperationStatus.Text = FriendlyServerError(ex.Message)
            If ConfigRevision IsNot Nothing AndAlso CollectConfigChanges().Count > 0 Then ConfigPanelStatus.Text = "操作未完成，修改尚未确认保存：" & OperationStatus.Text
        Finally
            Busy = False
            Sections.IsEnabled = True
        End Try
    End Function

    Private Function FriendlyServerError(Message As String) As String
        If Message.Contains("Authentication") OrElse Message.Contains("Permission denied") Then Return "登录失败，请检查服务器账号和密码。" & vbCrLf & Message
        If Message.Contains("already exists") Then Return "目标目录已有服务器，已停止部署且没有覆盖文件。请在运行状态页管理它，或在高级设置中选择新的空目录。"
        If Message.Contains("Firewall") OrElse Message.Contains("firewall") Then Return "防火墙配置失败，服务器尚未启动。请使用有管理权限的账号后重试。" & vbCrLf & Message
        If Message.Contains("Server exited") Then Return "服务端启动后退出，安装文件已保留。请读取运行状态页的控制台或崩溃报告后再启动。" & vbCrLf & Message
        If Message.Contains("timed out") OrElse Message.Contains("timeout") Then Return "连接或下载超时，请检查服务器网络后重试。" & vbCrLf & Message
        Return Message
    End Function

    Private Function Remote(Request As JObject) As Task(Of JObject)
        If CurrentProfile IsNot Nothing AndAlso ModServerTasks.Find(CurrentProfile)?.Loader.State = LoadState.Loading Then Throw New InvalidOperationException("该服务器正在部署，请从服务器列表查看进度，部署结束后再执行管理操作。")
        Return WithConnection(Function(Profile, Secret) ServerManager.Run(Profile, Secret, Request))
    End Function
    Private Function SelectedCategory() As String
        Return CType(Category.SelectedItem, ComboBoxItem).Tag.ToString()
    End Function
    Private Async Sub TestConnection(sender As Object, e As MouseButtonEventArgs)
        Await Perform(Async Function()
                          Dim Result = Await WithConnection(Function(Profile, Secret) New JObject From {{"system", ServerManager.CheckConnection(Profile, Secret)}})
                          ConsoleOutput.Text = Result.ToString()
                      End Function)
    End Sub
    Private Async Function EnsureNetwork(Optional Manual As Boolean = False) As Task(Of Boolean)
        If CurrentProfile IsNot Nothing AndAlso ModServerTasks.Find(CurrentProfile)?.Loader.State = LoadState.Loading Then Throw New InvalidOperationException("部署正在运行，请等待任务完成后检测网络。")
        Dim Kind = If(DeploySource.SelectedIndex = 1 AndAlso ImportedPack IsNot Nothing, ImportedPack("core").ToString(), CType(Core.SelectedItem, ComboBoxItem).Content.ToString())
        OperationStatus.Text = "正在检测远端 DNS、路由与 HTTPS 连接……"
        Dim Report = Await WithConnection(Function(Profile, Secret) ServerManager.CheckNetwork(Profile, Secret, Kind))
        Return Await HandleNetworkReport(Report, Kind, Manual)
    End Function
    Private Async Function HandleNetworkReport(Report As JObject, Kind As String, Manual As Boolean) As Task(Of Boolean)
        If Report("ready").Value(Of Boolean)() Then
            If Manual Then Await MyMsgBoxAsync(Report("message").ToString(), "远端网络正常")
            Return True
        End If
        If Not Report("canRepair").Value(Of Boolean)() Then
            Await MyMsgBoxAsync(Report("message").ToString() & vbCrLf & "未修改远端 DNS、网关或防火墙。请检查服务器外网或网络管理器。", "网络检测未通过", IsWarn:=True)
            Return False
        End If
        Dim Details = Report("message").ToString() & vbCrLf & "目标服务器：" & Host.Text.Trim() & vbCrLf & "网卡：" & Report("adapter").ToString() & vbCrLf &
            "将备份原配置并尝试 DNS：223.5.5.5、1.1.1.1。修改可能影响服务器其他程序和内部域名解析，需要管理员权限。" & vbCrLf &
            "不修改网关、不重启网卡，复测失败将回退。systemd-resolved 的更改仅在当前网络会话生效。"
        If Await MyMsgBoxAsync(Details, "检测到远端 DNS 故障", "修复并复测", "取消", IsWarn:=True) <> 1 Then Return False
        OperationStatus.Text = "正在备份并修复远端 DNS，随后复测网络；失败时回退原配置……"
        Dim RepairError As Exception = Nothing
        Dim Fixed As JObject = Nothing
        Try
            Fixed = Await WithConnection(Function(Profile, Secret) ServerManager.CheckNetwork(Profile, Secret, Kind, True))
        Catch ex As Exception
            RepairError = ex
        End Try
        If RepairError IsNot Nothing Then
            Await MyMsgBoxAsync(RepairError.Message & vbCrLf & "请核对远端权限和网络备份；不会继续部署。", "网络修复未完成", IsWarn:=True)
            Return False
        End If
        Await MyMsgBoxAsync(Fixed("message").ToString(), If(Fixed("ready").Value(Of Boolean)(), "网络修复成功", "网络修复未完成"), IsWarn:=Not Fixed("ready").Value(Of Boolean)())
        Return Fixed("ready").Value(Of Boolean)()
    End Function
    Private Async Sub CheckNetwork(sender As Object, e As MouseButtonEventArgs)
        Await Perform(Async Function()
                          If Not Await EnsureNetwork(True) Then Throw New OperationCanceledException("网络检测或修复未通过。")
                      End Function)
    End Sub
    Private Async Sub Deploy(sender As Object, e As MouseButtonEventArgs)
        If Busy Then Return
        If Not IsDeploymentMode Then
            OperationStatus.Text = "已部署服务器请使用运行管理，不能重复部署。"
            Return
        End If
        If Not AcceptEula.Checked Then
            OperationStatus.Text = "请先阅读并同意 Minecraft EULA。"
            Return
        End If
        If DeploySource.SelectedIndex = 1 AndAlso ImportedPack Is Nothing Then
            OperationStatus.Text = "请先选择并识别开服包。"
            Return
        End If
        Dim Warning = If(DeploySource.SelectedIndex = 1, "将上传并启动你选择的开服包，服务端和模组能在远端执行代码，请仅使用可信来源。包内世界及游戏配置将保留，管理所需的 RCON 设置将重新生成。", "将自动补全环境、下载服务端并启动，不覆盖已有服务器。")
        If MyMsgBox(Warning & vbCrLf & "程序将保护管理端口，并放行游戏端口。云服务器的安全组仍由云平台控制。", "部署并启动", "开始部署", "取消", IsWarn:=True) <> 1 Then Return
        Await Perform(Async Function()
                          Await WithConnection(Function(Connection, Secret) New JObject From {{"system", ServerManager.CheckConnection(Connection, Secret, requireRoot:=True)}})
                          If Not Await EnsureNetwork() Then Throw New OperationCanceledException("网络检查未通过，已停止部署。")
                          Dim Profile = ReadProfile()
                          If Not AdvancedDeploy.Checked Then
                              Profile.Root = "~/CraftPilot/server"
                              Profile.Memory = 4096
                              Profile.GamePort = 25565
                              Profile.JavaMajor = 0
                              Profile.JavaPath = ""
                          End If
                          Dim Request = New JObject From {{"action", "deploy"}, {"acceptEula", True}, {"core", Profile.Core}, {"version", Profile.Version}, {"memory", Profile.Memory}, {"port", Profile.GamePort}, {"javaMajor", Profile.JavaMajor}, {"javaPath", Profile.JavaPath}, {"loaderVersion", Profile.LoaderVersion}, {"autoConfigure", Not AdvancedDeploy.Checked}, {"configureFirewall", True}}
                          Dim LocalPack As String = Nothing
                          If DeploySource.SelectedIndex = 1 Then
                              Dim Pack = DirectCast(ImportedPack.DeepClone(), JObject)
                              Pack("version") = GameVersion.Text.Trim()
                              If AdvancedDeploy.Checked Then
                                  Pack("memory") = Profile.Memory
                                  Pack("port") = Profile.GamePort
                                  Pack("javaMajor") = Profile.JavaMajor
                              End If
                              If Pack("version").ToString() = "" Then Throw New InvalidOperationException("未识别 Minecraft 版本，请先选择版本。")
                              Profile.Core = Pack("core").ToString()
                              Profile.Version = Pack("version").ToString()
                              Request("pack") = Pack
                              Request("autoConfigure") = False
                              LocalPack = ImportedPackPath
                          End If
                          StoreProfile(Profile)
                          Dim Job = ModServerTasks.BeginDeployment(Profile, Password.Password, Request, LocalPack)
                          ModServerTasks.OpenDeployment(Job)
                      End Function)
    End Sub
    Public Sub OpenResume(Profile As ServerProfile)
        OpenProfile(Profile)
        SelectSection(0)
        PanBack.ScrollToHome()
        OperationStatus.Text = "请输入连接密码后点击继续上次部署；程序先检查远端记录，不覆盖既有服务器。"
    End Sub
    Private Async Sub ContinueDeployment(sender As Object, e As MouseButtonEventArgs)
        If Busy Then Return
        If MyMsgBox("将检查此目录的已安装服务器或上次失败记录。已安装时跳过下载，未安装时使用原参数和校验缓存重试；不会停止现有服务器或覆盖世界。", "继续上次部署", "继续", "取消") <> 1 Then Return
        Await Perform(Async Function()
                          Await WithConnection(Function(Connection, Secret) New JObject From {{"system", ServerManager.CheckConnection(Connection, Secret, requireRoot:=True)}})
                          Dim Profile = ReadProfile()
                          If CurrentProfile IsNot Nothing Then Profile.Root = CurrentProfile.Root
                          StoreProfile(Profile)
                          Dim Request As New JObject From {{"action", "deploy-resume"}}
                          Dim Job = ModServerTasks.BeginDeployment(Profile, Password.Password, Request)
                          ModServerTasks.OpenDeployment(Job)
                      End Function)
    End Sub
    Private Async Sub ImportServerPack(sender As Object, e As MouseButtonEventArgs)
        If Busy Then Return
        Dim Dialog As New Microsoft.Win32.OpenFileDialog With {.Filter = "开服包 ZIP 或服务端 JAR|*.zip;*.jar", .Title = "导入自定义开服包"}
        If Dialog.ShowDialog() <> True Then Return
        Await Perform(Async Function()
                          Dim Pack = Await Task.Run(Function() ServerPack.Inspect(Dialog.FileName))
                          ImportedPack = Pack
                          ImportedPackPath = Dialog.FileName
                          GameVersion.Text = Pack("version").ToString()
                          LoaderVersion.Text = Pack("loaderVersion").ToString()
                          Memory.Text = Pack("memory").ToString()
                          GamePort.Text = Pack("port").ToString()
                          JavaMajor.SelectedIndex = Array.IndexOf({0, 8, 11, 16, 17, 21, 25}, Pack("javaMajor").Value(Of Integer)())
                          PackSummary.Text = System.IO.Path.GetFileName(Dialog.FileName) & vbCrLf & Pack("core").ToString() & " " & If(GameVersion.Text = "", "（请选择 Minecraft 版本）", GameVersion.Text) & vbCrLf & "内存：" & Memory.Text & " MiB；端口：" & GamePort.Text & vbCrLf & "启动入口：" & String.Join(" ", Pack("launchArgs").Select(Function(Item) Item.ToString()))
                          DeploymentCard.TriggerForceResize()
                      End Function)
    End Sub
    Private Async Sub Status(sender As Object, e As MouseButtonEventArgs)
        Await Perform(Async Function()
                          Await RefreshStatus()
                      End Function)
    End Sub
    Private Async Function RefreshStatus() As Task
        Dim Result = Await Remote(New JObject From {{"action", "status"}})
        PresentStatus(Result)
        If Result("settings") IsNot Nothing Then
            SyncStatusSettings(Result("settings"))
        End If
    End Function

    Private Sub SyncStatusSettings(Actual As JToken)
        Dim Profile = ReadProfile()
        Profile.Deployed = True
        Profile.Core = Actual("core").ToString()
        Profile.Version = Actual("version").ToString()
        Profile.LoaderVersion = If(Actual("loaderVersion")?.ToString(), "")
        Profile.GamePort = Actual("port").Value(Of Integer)()
        ShowingProfile = True
        Try
            Core.SelectedIndex = Array.IndexOf({"Vanilla", "Paper", "Fabric", "Forge", "NeoForge"}, Profile.Core)
            GameVersion.Text = Profile.Version
            LoaderVersion.Text = Profile.LoaderVersion
            GamePort.Text = Profile.GamePort.ToString()
            RuntimePort.Text = Profile.GamePort.ToString()
        Finally
            ShowingProfile = False
        End Try
        StoreProfile(Profile)
    End Sub

    Public Sub PresentStatus(Result As JObject)
        KnownState = Result("state").ToString()
        Dim Description = If(KnownState = "running", "运行中", If(KnownState = "stopped", "已停止", "启动中或连接暂不可用"))
        ServerState.Text = Description
        ServerState.SetResourceReference(TextBlock.ForegroundProperty, If(KnownState = "running", "ColorBrush3", "ColorBrushGray2"))
        ServerPlayers.Text = If(KnownState = "running", "在线玩家：" & Result("players").ToString(), "在线玩家：未查询")
        PendingSummary.Text = "等待重启安装：" & Result("pendingResources").ToString() & " 项；待应用设置：" & If(Result("pendingRuntime").Value(Of Boolean)(), "有", "无")
        BtnPower.Text = If(KnownState = "running", "停止服务器", If(KnownState = "stopped", "启动服务器", "查询服务器状态"))
        If CurrentProfile IsNot Nothing Then ModServerTasks.SetStatus(CurrentProfile, Description)
    End Sub

    Private Async Sub PowerServer(sender As Object, e As MouseButtonEventArgs)
        If Busy Then Return
        If KnownState = "running" Then
            StopServer(sender, e)
        ElseIf KnownState = "stopped" Then
            StartServer(sender, e)
        Else
            Await Perform(Async Function()
                              Await RefreshStatus()
                          End Function)
        End If
    End Sub

    Private Sub OpenResources(sender As Object, e As MouseButtonEventArgs)
        If FrmServerLeft IsNot Nothing Then CType(FrmServerLeft.PanItem.Children(2), MyListItem).SetChecked(True, False, False)
        SelectSection(2)
    End Sub

    Private Sub CopyGameAddress(sender As Object, e As MouseButtonEventArgs)
        Try
            ClipboardSet(ServerManager.ValidateGameAddress(ReadProfile()))
            OperationStatus.Text = "连接地址已复制"
        Catch ex As Exception
            OperationStatus.Text = FriendlyServerError(ex.Message)
        End Try
    End Sub
    Private Async Sub StartServer(sender As Object, e As MouseButtonEventArgs)
        If CollectConfigChanges().Count > 0 Then
            ConfigPanelStatus.Text = "配置尚未保存，请保存修改，或使用重启时的保存并重启。"
            OperationStatus.Text = ConfigPanelStatus.Text
            Return
        End If
        Await Perform(Async Function()
                          Dim Result = Await Remote(New JObject From {{"action", "start"}})
                          If Result("settings") IsNot Nothing Then SyncStatusSettings(Result("settings"))
                          KnownState = "starting"
                          BtnPower.Text = "查询服务器状态"
                          ServerState.Text = "启动中"
                          ModServerTasks.SetStatus(CurrentProfile, "启动中")
                      End Function)
    End Sub
    Private Async Sub StopServer(sender As Object, e As MouseButtonEventArgs)
        If MyMsgBox("服务器将保存世界并退出。", "停止服务器", "停止", "取消", IsWarn:=True) <> 1 Then Return
        Await Perform(Async Function()
                          Await Remote(New JObject From {{"action", "stop"}})
                          KnownState = "stopped"
                          BtnPower.Text = "启动服务器"
                          ServerState.Text = "已停止"
                          ServerPlayers.Text = "在线玩家：0"
                          ModServerTasks.SetStatus(CurrentProfile, "已停止")
                      End Function)
    End Sub
    Private Async Sub RestartServer(sender As Object, e As MouseButtonEventArgs)
        If Busy Then Return
        Dim Changes = CollectConfigChanges()
        Dim Request As New JObject From {{"action", "restart"}}
        If Changes.Count > 0 Then
            If Not Await ConfirmConfigRisk(Changes) Then Return
            If Await MyMsgBoxAsync("检测到 " & Changes.Count & " 项未保存的配置。将先正常停服，再备份、保存并校验配置，应用待安装项后启动。保存失败不会继续启动。", "保存配置并重启", "保存并重启", "取消", IsWarn:=True) <> 1 Then Return
            Request = New JObject From {{"action", "config-panel-save-restart"}, {"changes", Changes}, {"revision", ConfigRevision}, {"confirmOffline", True}}
        Else
            If Await MyMsgBoxAsync("服务器将保存世界并退出，统一应用预安装资源与已准备的版本 / Java 配置，然后重新启动。跨版本配置会先备份服务器数据。", "重启服务器", "重启并应用", "取消", IsWarn:=True) <> 1 Then Return
        End If
        Await Perform(Async Function()
                          Dim Result = Await Remote(Request)
                          If Result("fields") IsNot Nothing Then
                              RenderConfigPanel(Result)
                              ConfigText.Clear()
                              ConfigPanelStatus.Text = Result("message").ToString()
                          End If
                          If Result("settings") IsNot Nothing Then SyncStatusSettings(Result("settings"))
                          KnownState = "starting"
                          BtnPower.Text = "查询服务器状态"
                          ServerState.Text = "启动中"
                          ModServerTasks.SetStatus(CurrentProfile, "启动中")
                          RenderResources(PendingFiles, (Await Remote(New JObject From {{"action", "pending"}}))("resources"), "name")
                      End Function)
    End Sub
    Private Async Sub Logs(sender As Object, e As MouseButtonEventArgs)
        Await Perform(Async Function()
                          ConsoleOutput.Text = (Await Remote(New JObject From {{"action", "logs"}}))("text").ToString()
                      End Function)
    End Sub
    Private Async Sub JoinServer(sender As Object, e As MouseButtonEventArgs)
        If Busy Then Return
        Try
            If Password.Password = "" Then
                Dim Secret = MyMsgBoxInput("同步游戏客户端", "请输入服务器 SSH 密码，仅用于本次页面操作。", MaskInput:=True)
                If String.IsNullOrEmpty(Secret) Then Return
                Password.Password = Secret
            End If
            Await ModServerClient.Synchronize(ReadProfile(), Password.Password, True)
        Catch ex As Exception
            OperationStatus.Text = ex.Message
        End Try
    End Sub
    Private Async Sub ListFiles(sender As Object, e As MouseButtonEventArgs)
        Await Perform(Async Function()
                          RenderResources(InstalledFiles, (Await Remote(New JObject From {{"action", "files"}, {"category", SelectedCategory()}}))("files"), "name")
                          RenderResources(PendingFiles, (Await Remote(New JObject From {{"action", "pending"}}))("resources"), "name")
                      End Function)
    End Sub
    Private Async Sub ImportLocalResources(sender As Object, e As MouseButtonEventArgs)
        If Busy Then Return
        If CurrentProfile IsNot Nothing AndAlso ModServerTasks.Find(CurrentProfile)?.Loader.State = LoadState.Loading Then
            OperationStatus.Text = "请等待服务器部署完成后再导入资源。"
            Return
        End If
        Dim Dialog As New Microsoft.Win32.OpenFileDialog With {.Filter = "Mod 或插件 JAR|*.jar", .Multiselect = True, .Title = "选择要导入的模组或插件"}
        If Dialog.ShowDialog() <> True Then Return
        If MyMsgBox("本地 JAR 会加入预安装队列，下次重启时应用。仅导入可信文件，未知模组可能需要手动确认客户端需求。", "导入资源", "导入", "取消", IsWarn:=True) <> 1 Then Return
        Await Perform(Async Function()
                          Dim CategoryName = SelectedCategory()
                          For Each Path In Dialog.FileNames
                              Dim FilePath = Path
                              OperationStatus.Text = "正在导入：" & System.IO.Path.GetFileName(Path)
                              Await WithConnection(Function(Profile, Secret) ServerManager.Run(Profile, Secret,
                                  New JObject From {{"action", "install"}, {"category", CategoryName}, {"name", System.IO.Path.GetFileName(FilePath)}}, FilePath))
                          Next
                          RenderResources(PendingFiles, (Await Remote(New JObject From {{"action", "pending"}}))("resources"), "name")
                      End Function)
    End Sub
    Private Async Sub RefreshPending(sender As Object, e As MouseButtonEventArgs)
        Await Perform(Async Function()
                          RenderResources(PendingFiles, (Await Remote(New JObject From {{"action", "pending"}}))("resources"), "name")
                      End Function)
    End Sub
    Private Async Sub CancelPending(sender As Object, e As MouseButtonEventArgs)
        If SelectedResource(PendingFiles) Is Nothing Then Return
        Await Perform(Async Function()
                          Dim Entry = SelectedResource(PendingFiles)
                          Await Remote(New JObject From {{"action", "pending-cancel"}, {"id", Entry("id")}})
                          RenderResources(PendingFiles, (Await Remote(New JObject From {{"action", "pending"}}))("resources"), "name")
                      End Function)
    End Sub
    Private Async Sub ToggleFile(sender As Object, e As MouseButtonEventArgs)
        If SelectedResource(InstalledFiles) Is Nothing Then Return
        Await Perform(Async Function()
                          Dim File = SelectedResource(InstalledFiles)
                          Await Remote(New JObject From {{"action", "toggle"}, {"category", SelectedCategory()}, {"name", File("name")}})
                          RenderResources(InstalledFiles, (Await Remote(New JObject From {{"action", "files"}, {"category", SelectedCategory()}}))("files"), "name")
                      End Function)
    End Sub
    Private Async Sub SearchResources(sender As Object, e As MouseButtonEventArgs)
        Await Perform(Async Function()
                          Dim Actual = (Await Remote(New JObject From {{"action", "runtime-read"}}))
                          Dim Target = If(Actual("pending")?.Type = JTokenType.Object, Actual("pending"), Actual("settings"))
                          RenderResources(SearchResults, Await ResourceCatalog.Search(SearchQuery.Text, SelectedCategory(), Target("core").ToString(), Target("version").ToString()), "title")
                      End Function)
    End Sub
    Private Async Sub InstallSelected(sender As Object, e As MouseButtonEventArgs)
        If SelectedResource(SearchResults) Is Nothing Then Return
        Await Perform(Async Function()
                          Dim Project = SelectedResource(SearchResults)
                          Dim Actual = (Await Remote(New JObject From {{"action", "runtime-read"}}))
                          Dim Target = If(Actual("pending")?.Type = JTokenType.Object, Actual("pending"), Actual("settings"))
                          Dim File = Await ResourceCatalog.Resolve(Project("project_id").ToString(), Target("core").ToString(), Target("version").ToString())
                          File("expectedCore") = Target("core")
                          File("expectedVersion") = Target("version")
                          Dim Dependencies = File("dependencies").ToString()
                          If MyMsgBox(File("name").ToString() & vbCrLf & "依赖项：" & Dependencies & vbCrLf & "请确认依赖已安装或加入队列。仅下载到暂存区，下次重启 / 启动时统一安装。", "预安装资源", "加入队列", "取消", IsWarn:=True) <> 1 Then Throw New OperationCanceledException()
                          File.Remove("dependencies")
                          File.Remove("version")
                          File("action") = "install"
                          File("category") = SelectedCategory()
                          Await Remote(File)
                          RenderResources(PendingFiles, (Await Remote(New JObject From {{"action", "pending"}}))("resources"), "name")
                      End Function)
    End Sub
    Private Async Sub InstallUrl(sender As Object, e As MouseButtonEventArgs)
        If MyMsgBox("第三方 JAR 可以在服务器上执行代码。仅安装可信来源的文件。下载到暂存区，不影响正在运行的服务器，下次重启 / 启动时统一安装。", "预安装远程资源", "加入队列", "取消", IsWarn:=True) <> 1 Then Return
        Await Perform(Async Function()
                          Await Remote(New JObject From {{"action", "install"}, {"category", SelectedCategory()}, {"url", ResourceUrl.Text.Trim()}, {"name", ResourceName.Text.Trim()}, {"sha256", ResourceHash.Text.Trim()}})
                          RenderResources(PendingFiles, (Await Remote(New JObject From {{"action", "pending"}}))("resources"), "name")
                      End Function)
    End Sub
    Private Async Sub ReadRuntime(sender As Object, e As MouseButtonEventArgs)
        Await Perform(Async Function()
                          Dim Result = Await Remote(New JObject From {{"action", "runtime-read"}})
                          RuntimeInfo.Text = "当前配置：" & Result("settings")("core").ToString() & " " & Result("settings")("version").ToString() & If(Result("pending")?.Type = JTokenType.Object, "；有等待重启应用的设置", "")
                          Dim Actual = Result("settings")
                          RuntimeCore.SelectedIndex = Array.IndexOf({"Vanilla", "Paper", "Fabric", "Forge", "NeoForge"}, Actual("core").ToString())
                          RuntimeVersion.Text = Actual("version").ToString()
                          RuntimeJavaMajor.SelectedIndex = Array.IndexOf({0, 8, 11, 16, 17, 21, 25}, If(Actual("javaMajor") Is Nothing, 0, Actual("javaMajor").Value(Of Integer)()))
                          RuntimeJavaPath.Text = If(Actual("javaPath")?.ToString(), "")
                          RuntimeLoaderVersion.Text = If(Actual("loaderVersion")?.ToString(), "")
                          RuntimeMemory.Text = Actual("memory").ToString()
                          RuntimePort.Text = Actual("port").ToString()
                          RuntimeAdvanced.Checked = False
                          RuntimeAdvancedPanel.Visibility = Visibility.Collapsed
                      End Function)
    End Sub
    Private Async Sub PrepareRuntime(sender As Object, e As MouseButtonEventArgs)
        If MyMsgBox("准备新的 Minecraft 版本、加载器和 Java。不会立即修改运行中的服务器。重启应用前备份数据；世界升级可能不可逆，请先禁用不兼容的已安装资源。", "准备服务器配置", "准备", "取消", IsWarn:=True) <> 1 Then Return
        Await Perform(Async Function()
                          Await Remote(New JObject From {{"action", "runtime-prepare"}, {"confirmBackup", True}, {"core", CType(RuntimeCore.SelectedItem, ComboBoxItem).Content.ToString()}, {"version", RuntimeVersion.Text.Trim()}, {"javaMajor", If(RuntimeAdvanced.Checked, CInt(CType(RuntimeJavaMajor.SelectedItem, ComboBoxItem).Tag), 0)}, {"javaPath", If(RuntimeAdvanced.Checked, RuntimeJavaPath.Text.Trim(), "")}, {"loaderVersion", RuntimeLoaderVersion.Text.Trim()}, {"memory", Integer.Parse(RuntimeMemory.Text)}, {"port", Integer.Parse(RuntimePort.Text)}})
                          RuntimeInfo.Text = "设置已准备好，下次重启服务器时应用。"
                      End Function)
    End Sub
    Private Async Sub CancelRuntime(sender As Object, e As MouseButtonEventArgs)
        Await Perform(Async Function()
                          Await Remote(New JObject From {{"action", "runtime-cancel"}})
                          RuntimeInfo.Text = "待应用设置已取消，当前服务器保持不变。"
                      End Function)
    End Sub
    Private Async Sub ReadConfig(sender As Object, e As MouseButtonEventArgs)
        Await Perform(Async Function()
                          ConfigText.Text = (Await Remote(New JObject From {{"action", "config-read"}}))("text").ToString()
                      End Function)
    End Sub
    Private Async Sub ReadBasicConfig(sender As Object, e As MouseButtonEventArgs)
        If Busy Then Return
        If CollectConfigChanges().Count > 0 AndAlso Await MyMsgBoxAsync("重新读取会放弃尚未保存的修改。", "读取服务器配置", "重新读取", "取消") <> 1 Then Return
        Await Perform(Async Function()
                          RenderConfigPanel(Await Remote(New JObject From {{"action", "config-panel-read"}}))
                      End Function)
    End Sub
    Private Sub RenderConfigPanel(Result As JObject)
        ConfigRevision = Result("revision").ToString()
        ConfigDefinitions.Clear()
        ConfigEditors.Clear()
        ConfigRows.Clear()
        ConfigGroups.Clear()
        ConfigFieldsPanel.Children.Clear()
        For Each Definition As JObject In Result("fields")
            Dim Key = Definition("key").ToString(), Group = Definition("group").ToString(), Value = Definition("value").ToString()
            If Not ConfigGroups.ContainsKey(Group) Then
                Dim Section As New StackPanel With {.Margin = New Thickness(0, 10, 0, 8)}
                Section.Children.Add(New TextBlock With {.Text = Group, .FontSize = 15, .FontWeight = FontWeights.SemiBold, .Margin = New Thickness(0, 0, 0, 12)})
                ConfigGroups(Group) = Section
                ConfigFieldsPanel.Children.Add(Section)
            End If
            Dim Row As New StackPanel With {.Margin = New Thickness(0, 0, 0, 14)}
            Row.Children.Add(New TextBlock With {.Text = Definition("label").ToString(), .TextWrapping = TextWrapping.Wrap})
            Dim EnglishKey As New TextBlock With {.Text = Key, .FontSize = 12, .TextWrapping = TextWrapping.Wrap, .Margin = New Thickness(0, 2, 0, 5)}
            EnglishKey.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray2")
            Row.Children.Add(EnglishKey)
            Dim Editor As FrameworkElement
            If Not Definition("editable").Value(Of Boolean)() Then
                Editor = New TextBlock With {.Text = Value, .TextWrapping = TextWrapping.Wrap}
            ElseIf Definition("type").ToString() = "boolean" AndAlso {"true", "false"}.Contains(Value.ToLowerInvariant()) Then
                Editor = New MyCheckBox With {.Text = "启用", .Checked = Value.ToLowerInvariant() = "true", .Height = 22}
            ElseIf Definition("type").ToString() = "enum" Then
                Dim Combo As New MyComboBox With {.Height = 28}
                For Each OptionValue As JProperty In DirectCast(Definition("options"), JObject).Properties()
                    Dim OptionItem As New MyComboBoxItem With {.Content = OptionValue.Value.ToString(), .Tag = OptionValue.Name}
                    Combo.Items.Add(OptionItem)
                    If OptionValue.Name = Value Then Combo.SelectedItem = OptionItem
                Next
                If Combo.SelectedItem Is Nothing Then
                    Dim Current As New MyComboBoxItem With {.Content = "当前值：" & Value, .Tag = Value}
                    Combo.Items.Add(Current)
                    Combo.SelectedItem = Current
                End If
                Editor = Combo
            Else
                Editor = New TextBox With {.Text = Value, .MinHeight = 28, .Padding = New Thickness(7, 4, 7, 4), .MaxLength = 8192, .VerticalContentAlignment = VerticalAlignment.Center}
            End If
            Editor.ToolTip = Key
            Editor.Tag = Key
            Row.Children.Add(Editor)
            ConfigDefinitions(Key) = DirectCast(Definition.DeepClone(), JObject)
            ConfigEditors(Key) = Editor
            ConfigRows(Key) = Row
            ConfigGroups(Group).Children.Add(Row)
            If TypeOf Editor Is MyCheckBox Then
                AddHandler DirectCast(Editor, MyCheckBox).Change, Sub() UpdateConfigDirtyStatus()
            ElseIf TypeOf Editor Is MyComboBox Then
                AddHandler DirectCast(Editor, MyComboBox).SelectionChanged, Sub() UpdateConfigDirtyStatus()
            ElseIf TypeOf Editor Is TextBox Then
                AddHandler DirectCast(Editor, TextBox).TextChanged, Sub() UpdateConfigDirtyStatus()
            End If
        Next
        BtnSaveBasic.IsEnabled = True
        BtnSavePanelBottom.IsEnabled = True
        FilterConfigFields()
    End Sub
    Private Function ConfigValue(Key As String) As String
        Dim Editor = ConfigEditors(Key)
        If TypeOf Editor Is MyCheckBox Then Return If(DirectCast(Editor, MyCheckBox).Checked, "true", "false")
        If TypeOf Editor Is MyComboBox Then Return DirectCast(DirectCast(Editor, MyComboBox).SelectedItem, MyComboBoxItem).Tag.ToString()
        If TypeOf Editor Is TextBox Then Return DirectCast(Editor, TextBox).Text
        Return ConfigDefinitions(Key)("value").ToString()
    End Function
    Private Function CollectConfigChanges() As JObject
        Dim Changes As New JObject
        For Each Entry In ConfigDefinitions
            If Not Entry.Value("editable").Value(Of Boolean)() Then Continue For
            Dim Current = ConfigValue(Entry.Key), Original = Entry.Value("value").ToString()
            If TypeOf ConfigEditors(Entry.Key) Is MyCheckBox AndAlso Current = Original.ToLowerInvariant() Then Continue For
            If Current <> Original Then Changes(Entry.Key) = Current
        Next
        Return Changes
    End Function
    Private Sub FilterConfigFields()
        If ConfigGroups Is Nothing Then Return
        Dim Search = If(ConfigSearch?.Text, "").Trim()
        Dim Count = 0
        For Each Entry In ConfigDefinitions
            Dim Matches = Search = "" OrElse Entry.Key.IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0 OrElse Entry.Value("label").ToString().IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0 OrElse Entry.Value("group").ToString().IndexOf(Search, StringComparison.OrdinalIgnoreCase) >= 0
            ConfigRows(Entry.Key).Visibility = If(Matches, Visibility.Visible, Visibility.Collapsed)
            If Matches Then Count += 1
        Next
        For Each Group In ConfigGroups.Values
            Group.Visibility = If(Group.Children.OfType(Of FrameworkElement)().Skip(1).Any(Function(Row) Row.Visibility = Visibility.Visible), Visibility.Visible, Visibility.Collapsed)
        Next
        If ConfigRevision IsNot Nothing Then ConfigPanelStatus.Text = If(CollectConfigChanges().Count > 0, "有 " & CollectConfigChanges().Count & " 项未保存的修改", If(Count = 0, "未找到对应配置", "显示 " & Count & " 项配置"))
        ConfigPanelCard.TriggerForceResize()
        ConfigPanelCard.InvalidateMeasure()
        PanBack.InvalidateMeasure()
    End Sub
    Private Sub UpdateConfigDirtyStatus()
        If ConfigRevision Is Nothing Then Return
        Dim Count = CollectConfigChanges().Count
        ConfigPanelStatus.Text = If(Count > 0, "有 " & Count & " 项未保存的修改", "没有未保存的修改")
    End Sub
    Private Async Function ConfirmConfigRisk(Changes As JObject) As Task(Of Boolean)
        If Changes("online-mode")?.ToString() = "false" AndAlso ConfigDefinitions("online-mode")("value").ToString().ToLowerInvariant() <> "false" Then
            Return Await MyMsgBoxAsync("关闭在线验证后，服务器不再验证玩家的微软正版身份，其他人可能冒用玩家名称或管理员身份。离线模式也可能改变玩家 UUID 和已有玩家数据。请仅对可信用户开放，并配合访问限制。", "关闭正版在线验证", "确认关闭", "取消", IsWarn:=True) = 1
        End If
        Return True
    End Function
    Private Async Sub SaveBasicConfig(sender As Object, e As MouseButtonEventArgs)
        If Busy OrElse ConfigRevision Is Nothing Then Return
        Dim Changes = CollectConfigChanges()
        If Changes.Count = 0 Then
            OperationStatus.Text = "没有需要保存的修改。"
            Return
        End If
        If Not Await ConfirmConfigRisk(Changes) Then Return
        Dim ConfirmOffline = Changes("online-mode")?.ToString() = "false"
        If Await MyMsgBoxAsync("保存前请先停止服务器。配置将自动备份，只更新修改项，下次启动时生效。", "保存服务器配置", "保存", "取消") <> 1 Then Return
        Await Perform(Async Function()
                          Dim Result = Await Remote(New JObject From {{"action", "config-panel-save"}, {"changes", Changes}, {"revision", ConfigRevision}, {"confirmOffline", ConfirmOffline}})
                          SyncSavedConfigPort(Result)
                          ConfigText.Clear()
                          RenderConfigPanel(Result)
                          ConfigPanelStatus.Text = Result("message").ToString()
                      End Function)
    End Sub
    Private Sub SyncSavedConfigPort(Result As JObject)
        If Result("port") Is Nothing Then Return
        Dim Profile = ReadProfile()
        Profile.GamePort = Result("port").Value(Of Integer)()
        GamePort.Text = Profile.GamePort.ToString()
        RuntimePort.Text = Profile.GamePort.ToString()
        StoreProfile(Profile)
    End Sub
    Private Async Sub SaveConfig(sender As Object, e As MouseButtonEventArgs)
        Await Perform(Async Function()
                          Dim Result = Await Remote(New JObject From {{"action", "config-save"}, {"text", ConfigText.Text}})
                          SyncSavedConfigPort(Result)
                          BtnSaveBasic.IsEnabled = False
                          BtnSavePanelBottom.IsEnabled = False
                          ConfigRevision = Nothing
                          ConfigPanelStatus.Text = "原始配置已保存，请重新读取中文面板。"
                      End Function)
    End Sub
    Private Async Sub Players(sender As Object, e As MouseButtonEventArgs)
        Await Perform(Async Function()
                          Dim Result = Await Remote(New JObject From {{"action", "players"}})
                          Dim Lines As New List(Of String) From {"在线玩家：" & Result("online").ToString()}
                          For Each Group In {Tuple.Create("whitelist", "白名单"), Tuple.Create("ops", "管理员"), Tuple.Create("banned-players", "已封禁")}
                              Dim Names = Result(Group.Item1).Select(Function(Entry) Entry("name").ToString()).ToList()
                              Lines.Add(Group.Item2 & "：" & If(Names.Count = 0, "暂无", String.Join("、", Names)))
                          Next
                          PlayerList.Text = String.Join(vbCrLf & vbCrLf, Lines)
                      End Function)
    End Sub
    Private Async Sub ManagePlayer(sender As Object, e As MouseButtonEventArgs)
        Dim Verb = CType(PlayerAction.SelectedItem, ComboBoxItem).Tag.ToString()
        If MyMsgBox(CType(PlayerAction.SelectedItem, ComboBoxItem).Content.ToString() & "：" & PlayerName.Text, "确认玩家操作", "执行", "取消", IsWarn:=True) <> 1 Then Return
        Await Perform(Async Function()
                          PlayerList.Text = (Await Remote(New JObject From {{"action", "player"}, {"player", PlayerName.Text.Trim()}, {"verb", Verb}}))("message").ToString()
                      End Function)
    End Sub
End Class
