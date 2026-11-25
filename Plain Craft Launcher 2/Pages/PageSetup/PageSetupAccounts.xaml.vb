Public Class PageSetupAccounts
    Public ServerKey As String
    Private Editing As GameAccount
    Private Reloading As Boolean
    Private Authorizing As Boolean

    Public Sub SetServerContext(Key As String)
        ServerKey = Key
        BtnUseForServer.Visibility = If(String.IsNullOrEmpty(Key), Visibility.Collapsed, Visibility.Visible)
        AccountCard.TriggerForceResize()
    End Sub

    Public Sub New()
        InitializeComponent()
        AddHandler AccountList.SelectionChanged, Sub() If Not Reloading Then ShowAccount(TryCast(TryCast(AccountList.SelectedItem, MyComboBoxItem)?.Tag, GameAccount))
        AddHandler TypeList.SelectionChanged, Sub() ChangeType()
        ShowAccount(Nothing)
    End Sub

    Private Sub LoadPage(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        ReloadAccounts()
        BtnUseForServer.Visibility = If(String.IsNullOrEmpty(ServerKey), Visibility.Collapsed, Visibility.Visible)
        AccountCard.TriggerForceResize()
    End Sub
    Private Sub LeavePage(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        Secret.Clear()
        ServerKey = Nothing
    End Sub

    Public Sub ReloadAccounts()
        Reloading = True
        AccountList.Items.Clear()
        For Each Account In ModAccounts.Accounts()
            AccountList.Items.Add(New MyComboBoxItem With {.Content = Account.Label, .Tag = Account})
        Next
        Dim Index = AccountList.Items.OfType(Of MyComboBoxItem)().ToList().FindIndex(Function(Item) DirectCast(Item.Tag, GameAccount).Id = Editing?.Id)
        AccountList.SelectedIndex = Index
        Reloading = False
        Dim DefaultAccount = ModAccounts.Resolve()
        DefaultSummary.Text = "默认账号：" & If(DefaultAccount Is Nothing, "未设置", DefaultAccount.Label)
    End Sub

    Private Sub ShowAccount(Account As GameAccount)
        Editing = If(Account, New GameAccount)
        AccountView.Bind(Account)
        LabelText.Text = Editing.Label
        UserText.Text = Editing.UserName
        AuthText.Text = Editing.EffectiveAuthServer()
        Secret.Clear()
        TypeList.SelectedIndex = If(Editing.Provider = "LittleSkin", 2, Array.IndexOf({"0", "5", "littleskin", "3", "2"}, CInt(Editing.Type).ToString()))
        ChangeType()
    End Sub
    Private Sub ChangeType()
        If TypeList.SelectedItem Is Nothing Then Return
        Dim Tag = DirectCast(TypeList.SelectedItem, MyComboBoxItem).Tag.ToString()
        Dim Type = If(Tag = "littleskin", 3, CInt(Tag))
        Credentials.Visibility = If(Type = 5, Visibility.Collapsed, Visibility.Visible)
        ThirdPartyPanel.Visibility = If(Type = 2 OrElse Type = 3, Visibility.Visible, Visibility.Collapsed)
        BtnOfficial.Visibility = If(Type = 5, Visibility.Visible, Visibility.Collapsed)
        OfficialSummary.Visibility = BtnOfficial.Visibility
        BtnThirdLogin.Visibility = If(Type = 2 OrElse Type = 3, Visibility.Visible, Visibility.Collapsed)
        AuthText.IsReadOnly = Tag = "littleskin"
        If Tag = "littleskin" Then
            AuthText.Text = ModAccounts.LittleSkinEndpoint
        ElseIf Editing IsNot Nothing AndAlso Editing.Provider = "LittleSkin" AndAlso AuthText.Text = ModAccounts.LittleSkinEndpoint Then
            AuthText.Text = ""
        End If
        OfficialSummary.Text = If(Editing IsNot Nothing AndAlso Editing.Type = McLoginType.Ms AndAlso Editing.UserName <> "", "已授权账号：" & Editing.UserName, "尚未授权")
        UserLabel.Text = If(Type = 0, "玩家名称", "账号 / 邮箱")
        AuthLabel.Text = If(Type = 2, "统一通行证服务器 ID", "认证服务器地址（HTTPS）")
        EditorCard.TriggerForceResize()
    End Sub
    Private Function ReadAccount() As GameAccount
        Dim Tag = DirectCast(TypeList.SelectedItem, MyComboBoxItem).Tag.ToString()
        Dim Type = CType(If(Tag = "littleskin", 3, CInt(Tag)), McLoginType)
        Dim Account As New GameAccount With {.Id = Editing.Id, .Type = Type, .Label = LabelText.Text.Trim(), .UserName = UserText.Text.Trim(), .AuthServer = AuthText.Text.Trim(), .Provider = If(Tag = "littleskin", "LittleSkin", "")}
        If Type = McLoginType.Ms Then
            If Editing.Type <> Type OrElse Editing.RefreshToken = "" Then Throw New InvalidOperationException("请先点击微软授权登录。")
            Account.UserName = Editing.UserName
            Account.RefreshToken = Editing.RefreshToken
        ElseIf Type = McLoginType.Legacy Then
            If Not Account.UserName.RegexCheck("^[A-Za-z0-9_]{1,16}$") Then Throw New InvalidOperationException("离线玩家名使用 1 到 16 位字母、数字或下划线。")
        Else
            Account.Password = If(Secret.Password = "" AndAlso Editing.Type = Type AndAlso Editing.UserName = Account.UserName AndAlso Editing.EffectiveAuthServer() = Account.EffectiveAuthServer(), Editing.Password, Secret.Password)
            If Account.UserName = "" OrElse Account.Password = "" Then Throw New InvalidOperationException("请输入账号和密码。")
            If Type = McLoginType.Auth Then
                Dim Address As Uri = Nothing
                If Not Uri.TryCreate(Account.AuthServer, UriKind.Absolute, Address) OrElse Address.Scheme <> "https" OrElse Address.UserInfo <> "" OrElse Address.Query <> "" OrElse Address.Fragment <> "" Then Throw New InvalidOperationException("第三方认证地址必须为不带用户名密码的 HTTPS 地址。")
                Account.AuthServer = Address.AbsoluteUri.TrimEnd("/"c)
            ElseIf Not Account.AuthServer.RegexCheck("^[A-Za-z0-9_-]{1,100}$") Then
                Throw New InvalidOperationException("统一通行证服务器 ID 无效。")
            End If
        End If
        If Account.Label = "" Then Account.Label = Account.UserName
        If Editing.Type = Account.Type AndAlso Editing.UserName = Account.UserName AndAlso Editing.EffectiveAuthServer() = Account.EffectiveAuthServer() Then
            Account.DisplayName = Editing.DisplayName
            Account.Uuid = Editing.Uuid
            Account.SkinPath = Editing.SkinPath
        End If
        Return Account
    End Function
    Private Sub SaveAccount(sender As Object, e As MouseButtonEventArgs) Handles BtnSave.Click
        Try
            Editing = ReadAccount()
            ModAccounts.Save(Editing)
            Secret.Clear()
            ReloadAccounts()
            AccountView.Bind(Editing)
            FrmLaunchLeft?.RefreshPage(False, False)
            StatusText.Text = "账号已保存，凭据由当前 Windows 用户加密保护。"
        Catch ex As Exception
            StatusText.Text = ex.Message
        End Try
    End Sub
    Private Sub NewAccount(sender As Object, e As MouseButtonEventArgs) Handles BtnNew.Click
        If Authorizing Then Return
        AccountList.SelectedIndex = -1
        ShowAccount(Nothing)
    End Sub
    Private Sub SetDefault(sender As Object, e As MouseButtonEventArgs) Handles BtnDefault.Click
        If Authorizing Then Return
        Dim Selected = TryCast(TryCast(AccountList.SelectedItem, MyComboBoxItem)?.Tag, GameAccount)
        If Selected Is Nothing Then Return
        Settings.Set("DefaultGameAccount", Selected.Id)
        FrmLaunchLeft?.RefreshPage(False, False)
        ReloadAccounts()
    End Sub
    Private Sub DeleteAccount(sender As Object, e As MouseButtonEventArgs) Handles BtnDelete.Click
        Dim Selected = TryCast(TryCast(AccountList.SelectedItem, MyComboBoxItem)?.Tag, GameAccount)
        If Selected Is Nothing OrElse Authorizing Then Return
        If MyMsgBox("删除账号 " & Selected.Label & "？服务器若绑定此账号需重新选择。", "删除 Minecraft 账号", "删除", "取消", IsWarn:=True) <> 1 Then Return
        Try
            ModAccounts.Delete(Selected.Id)
            ShowAccount(Nothing)
            ReloadAccounts()
        Catch ex As Exception
            StatusText.Text = ex.Message
        End Try
    End Sub
    Private Async Sub OfficialLogin(sender As Object, e As MouseButtonEventArgs) Handles BtnOfficial.Click
        If Authorizing OrElse McLoginLoader.State = LoadState.Loading OrElse McLaunchLoader.State = LoadState.Loading OrElse McLoginMsLoader.State = LoadState.Loading OrElse McLoginAuthLoader.State = LoadState.Loading OrElse McLoginNideLoader.State = LoadState.Loading Then Return
        Authorizing = True
        BtnOfficial.IsEnabled = False
        PanAccounts.IsEnabled = False
        Try
            Dim Input As New McLoginMs With {.UserName = If(Editing.Type = McLoginType.Ms, Editing.UserName, ""), .OAuthRefreshToken = If(Editing.Type = McLoginType.Ms, Editing.RefreshToken, "")}
            Await Task.Run(Sub() McLoginMsLoader.WaitForExit(Input, IsForceRestart:=True))
            Editing = New GameAccount With {.Id = Editing.Id, .Type = McLoginType.Ms, .UserName = Settings.Get(Of String)("CacheMsV2Name"), .RefreshToken = Settings.Get(Of String)("CacheMsV2OAuthRefresh"), .Label = If(LabelText.Text.Trim() = "", Settings.Get(Of String)("CacheMsV2Name"), LabelText.Text.Trim())}
            ModAccounts.Save(Editing)
            ModAccounts.RecordLogin(Editing, McLoginMsLoader.Output)
            FrmLaunchLeft?.RefreshPage(False, False)
            ShowAccount(Editing)
            ReloadAccounts()
            StatusText.Text = "微软账号授权成功。"
        Catch ex As Exception
            StatusText.Text = "授权失败：" & ex.Message
        Finally
            Authorizing = False
            BtnOfficial.IsEnabled = True
            PanAccounts.IsEnabled = True
        End Try
    End Sub
    Private Async Sub VerifyThirdParty(sender As Object, e As MouseButtonEventArgs) Handles BtnThirdLogin.Click
        If Authorizing OrElse McLoginLoader.State = LoadState.Loading OrElse McLaunchLoader.State = LoadState.Loading OrElse McLoginMsLoader.State = LoadState.Loading OrElse McLoginAuthLoader.State = LoadState.Loading OrElse McLoginNideLoader.State = LoadState.Loading Then Return
        Authorizing = True
        PanAccounts.IsEnabled = False
        Try
            Dim Account = ReadAccount()
            Dim Input = DirectCast(Account.Input(), McLoginServer)
            For Each Suffix In {"Access", "Client", "Uuid", "Name", "Username", "Pass"}
                Settings.Set("Cache" & Input.Token & Suffix, "")
            Next
            Dim Loader = If(Account.Type = McLoginType.Nide, McLoginNideLoader, McLoginAuthLoader)
            StatusText.Text = "正在验证登录……"
            Await Task.Run(Sub() Loader.WaitForExit(Input, IsForceRestart:=True))
            ModAccounts.Save(Account)
            ModAccounts.RecordLogin(Account, Loader.Output)
            FrmLaunchLeft?.RefreshPage(False, False)
            ShowAccount(Account)
            ReloadAccounts()
            StatusText.Text = "登录验证成功：" & Loader.Output.Name
        Catch ex As Exception
            StatusText.Text = "登录验证失败：" & ex.Message
        Finally
            Authorizing = False
            PanAccounts.IsEnabled = True
        End Try
    End Sub
    Private Sub UseForServer(sender As Object, e As MouseButtonEventArgs) Handles BtnUseForServer.Click
        Dim Selected = TryCast(TryCast(AccountList.SelectedItem, MyComboBoxItem)?.Tag, GameAccount)
        If Selected Is Nothing OrElse String.IsNullOrEmpty(ServerKey) Then Return
        Dim Bindings = JObject.Parse(Settings.Get(Of String)("ServerGameAccounts"))
        Bindings(ServerKey) = Selected.Id
        Settings.Set("ServerGameAccounts", Bindings.ToString(Formatting.None))
        StatusText.Text = "此服务器的账号已设置。"
    End Sub
End Class
