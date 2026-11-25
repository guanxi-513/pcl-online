Imports PCLCS.Server
Imports System.Windows.Threading

Public Class PagePlayServers
    Implements IRefreshable
    Private ReadOnly Rows As New Dictionary(Of String, MyListItem)
    Private ReadOnly Poll As New DispatcherTimer With {.Interval = TimeSpan.FromSeconds(30)}
    Private Querying As Boolean
    Private Generation As Integer
    Private ReadOnly Results As New Dictionary(Of String, JObject)

    Public Sub New()
        InitializeComponent()
        BtnRefresh.Logo = Logo.IconButtonRefresh
        AddHandler Poll.Tick, Sub() RefreshServers()
    End Sub

    Public Function SavedServers() As List(Of PlayServer)
        Dim Entries = PublicServers()
        For Each Profile In ModServerTasks.Profiles().Where(Function(Item) Item.Deployed)
            Try
                Entries.Add(New PlayServer With {.Id = "managed:" & Profile.Id, .Name = Profile.Name & "（我的服务器）", .Address = ServerManager.ValidateGameAddress(Profile)})
            Catch ex As Exception
                Logger.Warn("跳过地址无效的已部署服务器，请从我的服务器中检查连接地址。")
            End Try
        Next
        Return Entries
    End Function

    Private Function PublicServers() As List(Of PlayServer)
        Return If(JsonConvert.DeserializeObject(Of List(Of PlayServer))(Settings.Get(Of String)("PlayServers")), New List(Of PlayServer))
    End Function

    Public Sub RenderServers()
        Generation += 1
        Rows.Clear()
        PanServers.Children.Clear()
        Dim Entries = SavedServers()
        EmptyMessage.Visibility = If(Entries.Count = 0, Visibility.Visible, Visibility.Collapsed)
        If Entries.Count = 0 Then Return
        Dim Card As New MyCard With {.Title = "服务器 (" & Entries.Count & ")", .Margin = New Thickness(0, 0, 0, 15)}
        Dim Stack As New StackPanel With {.Margin = New Thickness(20, 40, 18, 15)}
        Card.Children.Add(Stack)
        PanServers.Children.Add(Card)
        For Each Entry As PlayServer In Entries
            Dim Selected = Entry
            Dim Item As New MyListItem With {.Title = Entry.Name, .Info = Entry.Address & " · 等待检测", .Height = 42,
                .Type = MyListItem.CheckType.Clickable, .Logo = PathImage & "Blocks/Grass.png"}
            AddHandler Item.Click, Sub() Join(Selected)
            If Entry.Id.StartsWith("managed:") Then
                Dim Manage As New MyIconButton With {.Logo = Logo.IconButtonSetup, .ToolTip = "管理我的服务器"}
                AddHandler Manage.Click, Sub() ModServerTasks.OpenProfile(ModServerTasks.Profiles().First(Function(Profile) "managed:" & Profile.Id = Selected.Id))
                Item.Buttons = {Manage}
            Else
                Dim Edit As New MyIconButton With {.Logo = Logo.IconButtonSetup, .ToolTip = "编辑服务器"}
                AddHandler Edit.Click, Sub() EditServer(Selected)
                Dim Delete As New MyIconButton With {.Logo = Logo.IconButtonDelete, .ToolTip = "删除服务器"}
                AddHandler Delete.Click, Sub() DeleteServer(Selected)
                Item.Buttons = {Edit, Delete}
            End If
            Stack.Children.Add(Item)
            Rows(Entry.Id) = Item
            If Results.ContainsKey(Entry.Id) Then PresentStatus(Entry, Results(Entry.Id))
        Next
    End Sub

    Private Sub EnterPage(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        RenderServers()
        RefreshServers()
        Poll.Start()
    End Sub
    Private Sub LeavePage(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        Poll.Stop()
        Generation += 1
    End Sub

    Public Async Sub RefreshServers()
        If Querying Then Return
        Querying = True
        BtnRefresh.IsEnabled = False
        Dim Current = Generation
        Dim Entries = SavedServers()
        Using Limit As New SemaphoreSlim(4)
            Try
                Dim Tasks = Entries.Select(Async Function(Server)
                    Await Limit.WaitAsync()
                    Try
                        Dim Result = Await Task.Run(Function() PlayServer.Query(Server))
                        If Current <> Generation Then Return
                        Results(Server.Id) = Result
                        PresentStatus(Server, Result)
                    Catch ex As Exception
                        If Current = Generation Then PresentStatus(Server, New JObject From {{"online", False}, {"message", ex.Message}})
                    Finally
                        Limit.Release()
                    End Try
                End Function).ToArray()
                Await Task.WhenAll(Tasks)
            Finally
                Querying = False
                BtnRefresh.IsEnabled = True
                If Current <> Generation AndAlso IsLoaded Then
                    Dim Scheduled = Dispatcher.BeginInvoke(New Action(Sub() RefreshServers()))
                End If
            End Try
        End Using
    End Sub
    Public Sub Refresh() Implements IRefreshable.Refresh
        RefreshServers()
    End Sub

    Public Sub PresentStatus(Server As PlayServer, Result As JObject)
        Results(Server.Id) = Result
        If Not Rows.ContainsKey(Server.Id) Then Return
        If Result("online").Value(Of Boolean)() Then
            Dim Players = If(Result("players")?.Type = JTokenType.Integer AndAlso Result("maximum")?.Type = JTokenType.Integer, Result("players").ToString() & "/" & Result("maximum").ToString() & " 人", "人数未公开")
            Rows(Server.Id).Info = "在线 · " & Players & " · " & Result("latency").ToString() & " ms · " & Result("version").ToString()
            Rows(Server.Id).ToolTip = Server.Address & vbCrLf & Result("motd").ToString()
        Else
            Rows(Server.Id).Info = Server.Address & " · " & Result("message").ToString()
            Rows(Server.Id).ToolTip = Server.Address & vbCrLf & Result("message").ToString()
        End If
    End Sub

    Private Sub Join(Server As PlayServer)
        Try
            If FrmPlayServerDetail Is Nothing Then FrmPlayServerDetail = New PagePlayServerDetail
            Dim Managed = If(Server.Id.StartsWith("managed:"), ModServerTasks.Profiles().FirstOrDefault(Function(Profile) "managed:" & Profile.Id = Server.Id), Nothing)
            FrmPlayServerDetail.Bind(Server, Managed)
            FrmMain.PageChange(New FormMain.PageStackData With {.Page = FormMain.PageType.PlayServerDetail, .Additional = Server.Id})
        Catch ex As Exception
            Hint(ex.Message, HintType.Red)
        End Try
    End Sub

    Private Sub Add(sender As Object, e As MouseButtonEventArgs) Handles BtnAdd.Click
        EditServer(New PlayServer)
    End Sub
    Private Sub RefreshClick() Handles BtnRefresh.Click
        CType(Me, IRefreshable).Refresh()
    End Sub
    Private Sub EditServer(Server As PlayServer)
        Dim Name = MyMsgBoxInput("服务器名称", DefaultInput:=Server.Name)
        If String.IsNullOrWhiteSpace(Name) Then Return
        Dim Address = MyMsgBoxInput("服务器地址", DefaultInput:=Server.Address, HintText:="例如 play.example.com 或 127.0.0.1:25565")
        If String.IsNullOrWhiteSpace(Address) Then Return
        Try
            Server.Name = Name.Trim()
            Server.Address = Address.Trim()
            Server.Endpoint()
            Dim Entries = PublicServers()
            Dim Index = Entries.FindIndex(Function(Entry) Entry.Id = Server.Id)
            If Index < 0 Then Entries.Add(Server) Else Entries(Index) = Server
            Settings.Set("PlayServers", JsonConvert.SerializeObject(Entries))
            RenderServers()
            RefreshServers()
        Catch ex As Exception
            Hint(ex.Message, HintType.Red)
        End Try
    End Sub
    Private Sub DeleteServer(Server As PlayServer)
        If MyMsgBox("从列表删除 " & Server.Name & "？不会删除任何游戏文件。", "删除服务器", "删除", "取消") <> 1 Then Return
        Dim Entries = PublicServers()
        Entries.RemoveAll(Function(Entry) Entry.Id = Server.Id)
        Settings.Set("PlayServers", JsonConvert.SerializeObject(Entries))
        RenderServers()
    End Sub
End Class
