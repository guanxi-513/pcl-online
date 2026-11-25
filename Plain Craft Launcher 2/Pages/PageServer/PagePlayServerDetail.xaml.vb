Imports PCLCS.Server

Public Class PagePlayServerDetail
    Private Play As PlayServer
    Private Managed As ServerProfile
    Private ServerKey As String

    Public Sub New()
        InitializeComponent()
        AddHandler AccountChoice.SelectionChanged, Sub() RefreshAccountSummary()
    End Sub

    Public Sub Bind(Server As PlayServer, Optional Profile As ServerProfile = Nothing)
        Play = Server
        Managed = Profile
        ServerKey = If(Profile Is Nothing, "play:" & Server.Id, "managed:" & Profile.Id)
        ServerName.Text = Server.Name
        AddressText.Text = Server.Address
        BtnManage.Visibility = If(Profile Is Nothing, Visibility.Collapsed, Visibility.Visible)
        ResultText.Text = ""
        ReloadAccounts()
    End Sub

    Private Sub EnterPage(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        ReloadAccounts()
    End Sub

    Public Sub ReloadAccounts()
        AccountChoice.Items.Clear()
        AccountChoice.Items.Add(New MyComboBoxItem With {.Content = "使用默认账号", .Tag = ""})
        Dim SelectedId = ModAccounts.BoundAccount(ServerKey)
        For Each Account In ModAccounts.Accounts()
            AccountChoice.Items.Add(New MyComboBoxItem With {.Content = Account.Label, .Tag = Account.Id})
        Next
        Dim Index = AccountChoice.Items.OfType(Of MyComboBoxItem)().ToList().FindIndex(Function(Item) Item.Tag.ToString() = SelectedId)
        AccountChoice.SelectedIndex = If(Index < 0, 0, Index)
        If Index < 0 AndAlso SelectedId <> "" Then ResultText.Text = "此前选择的账号已删除，请重新选择。"
        RefreshAccountSummary()
    End Sub

    Private Function SelectedId() As String
        Return If(TryCast(AccountChoice.SelectedItem, MyComboBoxItem)?.Tag?.ToString(), "")
    End Function
    Private Sub RefreshAccountSummary()
        Dim Account = ModAccounts.Resolve(SelectedId())
        AccountView.Bind(Account)
        AccountSummary.Text = If(Account Is Nothing, "尚未设置可用账号", "将使用：" & Account.Label)
    End Sub
    Private Sub SaveSelection(sender As Object, e As MouseButtonEventArgs) Handles BtnSave.Click
        Dim Bindings = JObject.Parse(Settings.Get(Of String)("ServerGameAccounts"))
        Bindings(ServerKey) = SelectedId()
        Settings.Set("ServerGameAccounts", Bindings.ToString(Formatting.None))
        ResultText.Text = "此服务器的账号选择已保存。"
    End Sub
    Private Sub ManageAccounts(sender As Object, e As MouseButtonEventArgs) Handles BtnAccounts.Click
        ModAccounts.OpenSettings(ServerKey)
    End Sub
    Private Sub ManageServer(sender As Object, e As MouseButtonEventArgs) Handles BtnManage.Click
        If Managed IsNot Nothing Then ModServerTasks.OpenProfile(Managed)
    End Sub
    Private Sub JoinServer(sender As Object, e As MouseButtonEventArgs) Handles BtnJoin.Click
        Try
            Dim Id = SelectedId()
            If ModAccounts.Resolve(Id) Is Nothing Then Throw New InvalidOperationException("请先添加账号并设为默认，或在此选择可用账号。")
            SaveSelection(Nothing, Nothing)
            If Managed IsNot Nothing Then
                ModServerClient.Enter(Managed)
                Return
            End If
            If McInstanceSelected Is Nothing Then Throw New InvalidOperationException("请先在启动页选择对应客户端或服主提供的整合包。")
            ModAccounts.SelectForServer(Id)
            FrmMain.PageChange(FormMain.PageType.GameLaunch)
            McLaunchStart(New McLaunchOptions With {.ServerIp = Play.GameAddress()})
        Catch ex As Exception
            ResultText.Text = ex.Message
        End Try
    End Sub
End Class
