Imports PCLCS.Server

Public Class PageRooms
    Implements IRefreshable
    Private Shared ReadOnly Client As New Lazy(Of RoomClient)(Function() New RoomClient(ProductWebsite))
    Private Shared ReadOnly SessionPath As String = Paths.Base & "CPT\community-session.dat"
    Private Busy As Boolean
    Private Restored As Boolean
    Private LobbyMode As Boolean
    Private PrefixExpanded As Boolean
    Private LobbyDialogOpen As Boolean
    Private LobbySearchCode As String = ""
    Private PresentedRoom As String
    Private PendingInvitation As String
    Private ConfirmedInvitation As String
    Private InvitationDialogOpen As Boolean
    Public Sub SetLobbyMode()
        LobbyMode = True
        LobbyPanel.Visibility = Visibility.Visible
        JoinCard.Visibility = Visibility.Collapsed
        CreateCard.Visibility = Visibility.Collapsed
    End Sub

    Public Sub New()
        InitializeComponent()
        BtnLobbyRefresh.Logo = Logo.IconButtonRefresh
        BtnRefreshPrefixes.Logo = Logo.IconButtonRefresh
        AddHandler JoinHasPassword.Change, Sub()
                                              JoinPasswordPanel.Visibility = If(JoinHasPassword.Checked, Visibility.Visible, Visibility.Collapsed)
                                              If Not JoinHasPassword.Checked Then JoinPassword.Clear()
                                              JoinCard.TriggerForceResize()
                                              JoinCard.InvalidateMeasure()
                                              PanBack.InvalidateMeasure()
                                          End Sub
        AddHandler TargetMode.SelectionChanged, Sub()
                                                   ServerTargetPanel.Visibility = If(TargetMode.SelectedIndex = 1, Visibility.Visible, Visibility.Collapsed)
                                                   LanTargetPanel.Visibility = If(TargetMode.SelectedIndex = 1, Visibility.Collapsed, Visibility.Visible)
                                                   CreateCard.TriggerForceResize()
                                               End Sub
        AddHandler PasswordAction.SelectionChanged, Sub()
                                                       OwnerPassword.Visibility = If(PasswordAction.SelectedIndex = 1, Visibility.Visible, Visibility.Collapsed)
                                                       ActiveRoom.TriggerForceResize()
                                                   End Sub
        AddHandler Client.Value.StatusChanged, Sub(Message) RunInUi(Sub()
                                                                       RoomState.Text = Message
                                                                       Status.Text = Message
                                                                       Refresh()
                                                                   End Sub)
    End Sub

    Private Async Sub LoadPage(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        If Not Restored Then
            Restored = True
            Await Perform(Async Function()
                              Await Client.Value.Restore(SessionPath)
                          End Function)
        End If
        MailStatus.Text = "填写昵称即可进入联机，无需注册或密码。"
        Refresh()
        Await ProcessInvitation()
        If PrefixExpanded AndAlso Not LobbyMode AndAlso Client.Value.User IsNot Nothing Then Await Perform(AddressOf RefreshPrefixes)
        If LobbyMode AndAlso Client.Value.User IsNot Nothing Then Await Perform(AddressOf RefreshLobby)
    End Sub
    Private Sub LeavePage() Handles Me.Unloaded
        JoinPassword.Clear()
        CreatePassword.Clear()
        OwnerPassword.Clear()
    End Sub
    Private Sub Refresh()
        Dim Logged = Client.Value.User IsNot Nothing
        LoginCard.Visibility = If(Logged, Visibility.Collapsed, Visibility.Visible)
        AccountPanel.Visibility = If(Logged AndAlso Not LobbyMode AndAlso Not Client.Value.InRoom, Visibility.Visible, Visibility.Collapsed)
        Status.Visibility = If(LobbyMode, Visibility.Collapsed, Visibility.Visible)
        Dim PrefixVisibility = If(Logged AndAlso Not LobbyMode AndAlso PrefixExpanded AndAlso Client.Value.Hosting AndAlso Client.Value.InRoom, Visibility.Visible, Visibility.Collapsed)
        If PrefixCard.Visibility <> PrefixVisibility Then
            PrefixCard.Visibility = PrefixVisibility
            ActiveRoom.TriggerForceResize()
            ActiveRoom.InvalidateMeasure()
            PanBack.InvalidateMeasure()
        End If
        RoomActions.Visibility = If(Logged AndAlso (LobbyMode OrElse Not Client.Value.InRoom), Visibility.Visible, Visibility.Collapsed)
        ActiveRoom.Visibility = If(Not LobbyMode AndAlso Client.Value.InRoom, Visibility.Visible, Visibility.Collapsed)
        If Logged Then UserSummary.Text = Client.Value.User("nickname").ToString()
        If Client.Value.InRoom AndAlso Not LobbyMode Then
            CurrentRoomName.Text = Client.Value.RoomInfo("title").ToString()
            CurrentRoomSummary.Text = "房主：" & Client.Value.RoomInfo("nickname")?.ToString() & " · " & Client.Value.RoomInfo("members")?.ToString() & "/" & Client.Value.RoomInfo("capacity")?.ToString() & " 人"
            RoomCode.Text = Client.Value.Code
            GameAddress.Text = If(Client.Value.Hosting, "游戏地址：" & Client.Value.TargetAddress, "游戏地址：127.0.0.1:" & Client.Value.LocalPort)
            BtnCopyAddress.Visibility = If(Client.Value.Hosting, Visibility.Collapsed, Visibility.Visible)
            BtnLaunch.Visibility = Visibility.Visible
            BtnCopyLink.Visibility = If(Client.Value.Hosting, Visibility.Visible, Visibility.Collapsed)
            OwnerSettings.Visibility = BtnCopyLink.Visibility
            BtnManagePrefixes.Visibility = If(Client.Value.Hosting, Visibility.Visible, Visibility.Collapsed)
            Dim Signature = Client.Value.Code & ":" & Client.Value.RoomInfo("title").ToString() & ":" & Client.Value.RoomInfo("public").ToString() & ":" & Client.Value.RoomInfo("passwordRequired").ToString()
            If PresentedRoom <> Signature Then
                PresentedRoom = Signature
                OwnerTitle.Text = Client.Value.RoomInfo("title").ToString()
                OwnerPublic.Checked = Client.Value.RoomInfo("public").Value(Of Boolean)()
                OwnerPasswordState.Text = If(Client.Value.RoomInfo("passwordRequired").Value(Of Boolean)(), "当前已设置房间密码", "当前未设置房间密码")
                ActiveRoom.TriggerForceResize()
            End If
        Else
            PresentedRoom = Nothing
            PrefixExpanded = False
        End If
    End Sub
    Private Async Function Perform(Action As Func(Of Task)) As Task
        If Busy Then Return
        Busy = True
        ContentPanel.IsEnabled = False
        Status.Text = "正在处理……"
        Try
            Await Action()
        Catch ex As Exception
            Status.Text = ex.Message
        Finally
            Busy = False
            ContentPanel.IsEnabled = True
            Refresh()
        End Try
        If IsLoaded AndAlso Restored Then Await ProcessInvitation()
    End Function
    Private Async Sub Authenticate() Handles BtnAuth.Click
        Dim Name = Nickname.Text.Trim()
        Await Perform(Async Function()
                          If Name.Length < 2 OrElse Name.Length > 24 Then Throw New InvalidOperationException("昵称需为 2 至 24 位。")
                          Await Client.Value.SignIn(Name)
                          Client.Value.SaveSession(SessionPath)
                          Status.Text = "已进入联机。"
                          If LobbyMode Then Await RefreshLobby()
                      End Function)
        Await ProcessInvitation()
    End Sub
    Public Async Sub ReceiveInvitation(Link As String)
        Client.Value.ParseInvitation(Link)
        If PendingInvitation <> Link Then ConfirmedInvitation = Nothing
        PendingInvitation = Link
        RoomNumber.Text = Link
        JoinPassword.Clear()
        JoinHasPassword.Checked = False
        Status.Text = "收到房间邀请。"
        If Restored AndAlso IsLoaded Then Await ProcessInvitation()
    End Sub
    Private Async Function ProcessInvitation() As Task
        If PendingInvitation Is Nothing OrElse Busy OrElse LobbyMode OrElse InvitationDialogOpen Then Return
        Dim Link = PendingInvitation
        Dim Invitation = Client.Value.ParseInvitation(Link)
        If Client.Value.InRoom AndAlso Client.Value.Code = Invitation.Code Then
            PendingInvitation = Nothing
            ConfirmedInvitation = Nothing
            Refresh()
            PanBack.ScrollToHome()
            Return
        End If
        If ConfirmedInvitation <> Link Then
            InvitationDialogOpen = True
            Try
                Dim Switching = Client.Value.InRoom
                Dim Message = If(Switching,
                    "当前房间：" & Client.Value.RoomInfo("title").ToString() & vbCrLf & "当前房间号：" & Client.Value.Code & vbCrLf & vbCrLf & "是否退出当前房间，再加入邀请房间？",
                    "是否加入此邀请房间？") & vbCrLf & "邀请房间号：" & Invitation.Code
                If MyMsgBox(Message, "房间邀请", If(Switching, "退出并加入", "加入房间"), "取消", IsWarn:=Switching) <> 1 Then
                    If PendingInvitation = Link Then PendingInvitation = Nothing
                    ConfirmedInvitation = Nothing
                    Status.Text = "已取消加入邀请房间。"
                    Return
                End If
                If PendingInvitation <> Link Then Return
                ConfirmedInvitation = Link
            Finally
                InvitationDialogOpen = False
            End Try
        End If
        If Client.Value.User Is Nothing Then
            Status.Text = "已确认邀请，请登录 CPT 后继续加入。"
            Return
        End If
        PendingInvitation = Nothing
        ConfirmedInvitation = Nothing
        Await Perform(Async Function()
                          If Client.Value.InRoom Then Await Client.Value.Leave()
                          Await Client.Value.Enter(False, Link, 0)
                          Status.Text = "已加入邀请房间。"
                          RoomNumber.Clear()
                          PanBack.ScrollToHome()
                      End Function)
    End Function
    Private Async Function RefreshPrefixes() As Task
        Dim Result = Await Client.Value.GameAddresses()
        PrefixItems.Children.Clear()
        For Each Address As JObject In Result("addresses")
            Dim Entry = Address
            Dim Line As New StackPanel With {.Margin = New Thickness(0, 0, 0, 15)}
            Line.Children.Add(New TextBlock With {.Text = Entry("address").ToString(), .FontSize = 15, .TextWrapping = TextWrapping.Wrap})
            Line.Children.Add(New TextBlock With {.Text = If(Entry("published").Value(Of Boolean)(), "已发布到游戏，可粘贴到 Minecraft 添加服务器", "已保留，当前未发布"), .Margin = New Thickness(0, 4, 0, 8)})
            Dim Buttons As New WrapPanel
            Dim Copy As New MyButton With {.Text = "复制游戏地址", .Width = 130, .Height = 32, .Margin = New Thickness(0, 0, 8, 8)}
            AddHandler Copy.Click, Sub() ClipboardSet(Entry("address").ToString())
            Buttons.Children.Add(Copy)
            Dim Publish As New MyButton With {.Text = If(Entry("published").Value(Of Boolean)(), "取消发布", "发布到当前房间"), .Width = 150, .Height = 32, .Margin = New Thickness(0, 0, 8, 8)}
            AddHandler Publish.Click, Async Sub()
                                          If Entry("published").Value(Of Boolean)() Then
                                              Await Perform(Async Function()
                                                                Await Client.Value.UnpublishAddress(Entry("prefix").ToString())
                                                                Await RefreshPrefixes()
                                                            End Function)
                                          Else
                                              If Not Client.Value.Hosting OrElse Not Client.Value.InRoom Then
                                                  MyMsgBox("请先创建指向目标世界或 MC 服务器的房间，再发布地址。", "尚无房主连接")
                                                  Return
                                              End If
                                              If MyMsgBox("发布 " & Entry("address").ToString() & " 后，玩家可直接在 Minecraft 添加服务器并连接。CPT 房间密码和 CPT 登录不能拦截此游戏入口，目标服务器自身的正版验证与白名单仍然生效。房主 CPT 必须在线。", "确认发布 Minecraft 地址", "发布", "取消", IsWarn:=True) <> 1 Then Return
                                              Await Perform(Async Function()
                                                                Await Client.Value.PublishAddress(Entry("prefix").ToString())
                                                                Await RefreshPrefixes()
                                                            End Function)
                                          End If
                                      End Sub
            Buttons.Children.Add(Publish)
            Dim Release As New MyButton With {.Text = "释放前缀", .Width = 110, .Height = 32, .Margin = New Thickness(0, 0, 8, 8)}
            AddHandler Release.Click, Async Sub()
                                          If MyMsgBox("释放后地址停止转发，其他用户可以申请这个前缀。", "释放前缀", "释放", "取消", IsWarn:=True) <> 1 Then Return
                                          Await Perform(Async Function()
                                                            Await Client.Value.ReleaseAddress(Entry("prefix").ToString())
                                                            Await RefreshPrefixes()
                                                        End Function)
                                      End Sub
            Buttons.Children.Add(Release)
            Line.Children.Add(Buttons)
            PrefixItems.Children.Add(Line)
        Next
        PrefixStatus.Text = "已保留 " & Result("addresses").Count() & " / 2 个前缀"
        ActiveRoom.TriggerForceResize()
        ActiveRoom.InvalidateMeasure()
        PanBack.InvalidateMeasure()
    End Function
    Private Async Sub AllocatePrefix() Handles BtnAllocatePrefix.Click
        Dim Prefix = PrefixInput.Text.Trim()
        Await Perform(Async Function()
                          Await Client.Value.AllocateAddress(Prefix)
                          PrefixInput.Clear()
                          Await RefreshPrefixes()
                      End Function)
    End Sub
    Private Async Sub ManagePrefixes() Handles BtnManagePrefixes.Click
        PrefixExpanded = True
        Refresh()
        Await Perform(AddressOf RefreshPrefixes)
        PanBack.ScrollToVerticalOffset(PrefixCard.TranslatePoint(New Point(0, 0), ContentPanel).Y)
    End Sub
    Private Sub RefreshClick() Handles BtnLobbyRefresh.Click, BtnRefreshPrefixes.Click
        CType(Me, IRefreshable).Refresh()
    End Sub
    Private Async Sub CreateRoom() Handles BtnCreate.Click
        Dim Port As Integer
        If TargetMode.SelectedIndex = 0 AndAlso (Not Integer.TryParse(HostPort.Text, Port) OrElse Port < 1024 OrElse Port > 65535) Then
            Status.Text = "请输入有效的 Minecraft 局域网端口。"
            Return
        End If
        Dim Address = If(TargetMode.SelectedIndex = 1, ServerTarget.Text.Trim(), ""), Title = CreateTitle.Text.Trim(), PublicRoom = CreatePublic.Checked, Secret = CreatePassword.Password
        If TargetMode.SelectedIndex = 1 AndAlso Address = "" Then
            Status.Text = "请填写 MC 服务器地址。"
            Return
        End If
        If Address <> "" AndAlso MyMsgBox("房间成员将通过你的 CPT 客户端连接 " & Address & "。请确认你允许共享该服务器端口。", "共享 MC 服务器", "继续", "取消", IsWarn:=True) <> 1 Then Return
        Await Perform(Async Function()
                          Await Client.Value.Enter(True, "", Port, Title, PublicRoom, Secret, Address)
                          CreatePassword.Clear()
                          Status.Text = "房间已创建。"
                          PanBack.ScrollToHome()
                      End Function)
    End Sub
    Private Async Sub JoinRoom() Handles BtnJoin.Click
        Dim Number = RoomNumber.Text.Trim()
        Dim Secret = If(JoinHasPassword.Checked, JoinPassword.Password, "")
        Await JoinSelectedRoom(Number, Secret)
    End Sub
    Private Async Sub JoinOnEnter(sender As Object, e As KeyEventArgs) Handles RoomNumber.KeyDown, JoinPassword.KeyDown
        If e.Key <> Key.Enter OrElse Busy Then Return
        e.Handled = True
        Await JoinSelectedRoom(RoomNumber.Text.Trim(), If(JoinHasPassword.Checked, JoinPassword.Password, ""))
    End Sub
    Private Async Function JoinSelectedRoom(Number As String, Secret As String) As Task
        Await Perform(Async Function()
                          Await Client.Value.Enter(False, Number, 0, password:=Secret)
                          JoinPassword.Clear()
                          Status.Text = "已加入房间。"
                          PanBack.ScrollToHome()
                      End Function)
    End Function
    Private Async Function RefreshLobby() As Task
        LobbyStatus.Text = "正在加载房间……"
        Try
            Dim SearchCode = ReadLobbySearchCode()
            Dim Result = Await Client.Value.Lobby(SearchCode)
            LobbySearchCode = SearchCode
            RenderLobby(CType(Result("rooms"), JArray))
            PanBack.ScrollToHome()
        Catch ex As Exception
            LobbyItems.Children.Clear()
            LobbyListCard.Visibility = Visibility.Collapsed
            LobbyStatus.Text = ex.Message
            Throw
        End Try
    End Function
    Private Function ReadLobbySearchCode() As String
        Dim Value = LobbySearch.Text.Trim().ToUpperInvariant()
        If Value <> "" AndAlso Not System.Text.RegularExpressions.Regex.IsMatch(Value, "\A[A-F0-9]{16}\z") Then
            Throw New InvalidOperationException("请输入完整的 16 位房间号。")
        End If
        Return Value
    End Function
    Private Async Sub SearchLobbyChanged(sender As Object, e As EventArgs) Handles LobbySearch.TextChanged
        If Not IsLoaded OrElse Not LobbyMode OrElse Busy OrElse LobbyDialogOpen OrElse Client.Value.User Is Nothing Then Return
        Dim Value = LobbySearch.Text.Trim()
        If Value = "" OrElse System.Text.RegularExpressions.Regex.IsMatch(Value, "\A[A-Fa-f0-9]{16}\z") Then Await Perform(AddressOf RefreshLobby)
    End Sub
    Private Async Sub SearchLobbyOnEnter(sender As Object, e As KeyEventArgs) Handles LobbySearch.KeyDown
        If e.Key <> Key.Enter Then Return
        e.Handled = True
        If Not LobbyMode OrElse Busy OrElse LobbyDialogOpen OrElse Client.Value.User Is Nothing Then Return
        Await Perform(AddressOf RefreshLobby)
    End Sub
    Private Sub RenderLobby(Rooms As JArray)
        LobbyItems.Children.Clear()
        For Each Room As JObject In Rooms
            Dim Entry = Room
            Dim RoomState = If(Room("locked").Value(Of Boolean)(), "已锁定", If(Room("members").Value(Of Integer)() >= Room("capacity").Value(Of Integer)(), "已满", If(Room("passwordRequired").Value(Of Boolean)(), "需要密码", "可加入")))
            Dim Description = Room("nickname").ToString() & " · " & Room("members").ToString() & "/" & Room("capacity").ToString() & " 人 · " & RoomState
            Dim Item As New MyListItem With {.Title = Room("title").ToString(), .Info = Description, .Height = 42, .SnapsToDevicePixels = True, .Type = MyListItem.CheckType.Clickable, .Logo = PathImage & "Blocks/Grass.png", .ToolTip = Room("title").ToString() & vbCrLf & Description}
            AddHandler Item.Click, Async Sub()
                                      Await OpenLobbyRoom(Entry)
                                  End Sub
            LobbyItems.Children.Add(Item)
        Next
        LobbyListCard.Title = If(LobbySearchCode = "", "公开房间", "搜索结果") & " (" & LobbyItems.Children.Count & ")"
        LobbyListCard.Visibility = If(LobbyItems.Children.Count = 0, Visibility.Collapsed, Visibility.Visible)
        LobbyListCard.TriggerForceResize()
        LobbyListCard.InvalidateMeasure()
        PanBack.InvalidateMeasure()
        LobbyStatus.Text = If(LobbyItems.Children.Count = 0, If(LobbySearchCode = "", "暂无公开房间。", "未找到该房间，或房间已关闭。"), "")
    End Sub
    Private Function LobbyRoomDetails(Room As JObject) As String
        Return "房主：" & Room("nickname").ToString() & vbCrLf & "人数：" & Room("members").ToString() & "/" & Room("capacity").ToString() & vbCrLf & "房间号：" & Room("code").ToString()
    End Function
    Private Async Function OpenLobbyRoom(Room As JObject) As Task
        If Busy OrElse LobbyDialogOpen Then Return
        LobbyDialogOpen = True
        Try
            Dim Details = LobbyRoomDetails(Room)
            If Client.Value.InRoom Then
                If MyMsgBox(Details & vbCrLf & "你已在房间中。", Room("title").ToString(), "当前房间", "关闭") = 1 Then
                    FrmMain.PageChange(FormMain.PageType.Link, CType(1, FormMain.PageSubType))
                End If
                Return
            End If
            If Room("locked").Value(Of Boolean)() OrElse Room("members").Value(Of Integer)() >= Room("capacity").Value(Of Integer)() Then
                MyMsgBox(Details & vbCrLf & If(Room("locked").Value(Of Boolean)(), "房间暂不接受加入。", "房间已满。"), Room("title").ToString(), "关闭")
                Return
            End If
            Dim Secret As String = ""
            If Room("passwordRequired").Value(Of Boolean)() Then
                Secret = MyMsgBoxInput(Room("title").ToString(), Details, HintText:="房间密码", Button1:="加入房间", MaskInput:=True)
                If Secret Is Nothing Then Return
            ElseIf MyMsgBox(Details, Room("title").ToString(), "加入房间", "取消") <> 1 Then
                Return
            End If
            Await JoinSelectedRoom(Room("code").ToString(), Secret)
            If Client.Value.InRoom Then
                FrmMain.PageChange(FormMain.PageType.Link, CType(1, FormMain.PageSubType))
            Else
                MyMsgBox(Status.Text, "加入失败", "关闭")
            End If
        Finally
            LobbyDialogOpen = False
        End Try
    End Function
    Public Async Sub Reload() Implements IRefreshable.Refresh
        Await RefreshPageData()
    End Sub
    Private Async Function RefreshPageData() As Task
        If Busy OrElse LobbyDialogOpen OrElse InvitationDialogOpen Then Return
        Await Perform(Async Function()
                          If Client.Value.User Is Nothing Then
                              MailStatus.Text = "填写昵称即可进入联机，无需注册或密码。"
                              Return
                          End If
                          If LobbyMode Then
                              Await RefreshLobby()
                          Else
                              Await Client.Value.RefreshRoom()
                              If Client.Value.Hosting AndAlso Client.Value.InRoom AndAlso PrefixExpanded Then Await RefreshPrefixes()
                              Status.Text = "已刷新房间状态。"
                          End If
                      End Function)
    End Function
    Private Sub CopyInvite() Handles BtnCopyLink.Click
        If Client.Value.ShareLink <> "" Then ClipboardSet(Client.Value.ShareLink)
    End Sub
    Private Async Sub SaveRoomSettings() Handles BtnSaveRoom.Click
        Dim Secret As String = If(PasswordAction.SelectedIndex = 0, Nothing, If(PasswordAction.SelectedIndex = 2, "", OwnerPassword.Password))
        Dim Title = OwnerTitle.Text.Trim(), PublicRoom = OwnerPublic.Checked
        Await Perform(Async Function()
                          If PasswordAction.SelectedIndex = 1 AndAlso (Secret.Length < 4 OrElse Secret.Length > 32) Then Throw New InvalidOperationException("新房间密码需为 4 至 32 位；取消密码请单独选择取消。")
                          Await Client.Value.UpdateRoom(Title, PublicRoom, Secret)
                          PresentedRoom = Nothing
                          OwnerPassword.Clear()
                          PasswordAction.SelectedIndex = 0
                          Status.Text = "房间设置已保存。"
                      End Function)
    End Sub
    Private Async Sub RotateInvite() Handles BtnRotateInvite.Click
        If MyMsgBox("重新生成后，旧邀请链接将失效，已加入的成员不会被踢出。", "重新生成邀请链接", "重新生成", "取消", IsWarn:=True) <> 1 Then Return
        Await Perform(Async Function()
                          Await Client.Value.UpdateRoom(OwnerTitle.Text, OwnerPublic.Checked, rotateInvite:=True)
                          PresentedRoom = Nothing
                          Status.Text = "邀请链接已重新生成。"
                      End Function)
    End Sub
    Private Async Sub ExitRoom() Handles BtnLeave.Click
        Await Perform(Async Function()
                          Await Client.Value.Leave()
                          Status.Text = "已退出房间。"
                          PanBack.ScrollToHome()
                      End Function)
    End Sub
    Private Async Sub Logout() Handles BtnLogout.Click
        Await Perform(Async Function()
                          Await Client.Value.Logout()
                          Client.Value.SaveSession(SessionPath)
                          Status.Text = "已退出账号。"
                      End Function)
    End Sub
    Private Sub CopyCode() Handles BtnCopyCode.Click
        ClipboardSet(Client.Value.Code)
    End Sub
    Private Sub CopyAddress() Handles BtnCopyAddress.Click
        ClipboardSet("127.0.0.1:" & Client.Value.LocalPort)
    End Sub
    Private Sub Launch() Handles BtnLaunch.Click
        If Not Client.Value.InRoom Then Return
        If McInstanceSelected Is Nothing Then
            Status.Text = "请先在启动页选择与房主相同的客户端版本。"
            Return
        End If
        FrmMain.PageChange(FormMain.PageType.GameLaunch)
        McLaunchStart(New McLaunchOptions With {.ServerIp = If(Client.Value.Hosting, Client.Value.TargetAddress, "127.0.0.1:" & Client.Value.LocalPort)})
    End Sub
    Public Shared Function ConfirmExit() As Boolean
        If Not Client.IsValueCreated OrElse Not Client.Value.InRoom Then Return True
        If MyMsgBox("当前仍在联机房间中，退出 CPT 会断开联机连接。", "退出联机", "退出", "取消", IsWarn:=True) <> 1 Then Return False
        Client.Value.StopRoom()
        Return True
    End Function
    Public Shared Sub StopAll()
        If Client.IsValueCreated Then Client.Value.StopRoom()
    End Sub
End Class
