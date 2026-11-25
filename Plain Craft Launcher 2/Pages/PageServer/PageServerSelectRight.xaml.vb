Imports PCLCS.Server
Imports System.Windows.Threading

Public Class PageServerSelectRight
    Implements IRefreshable
    Private ReadOnly Timer As New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(300)}
    Private ReadOnly Rows As New Dictionary(Of String, Tuple(Of ServerProfile, MyListItem, Border, Border))
    Private Signature As String
    Private GroupSignature As String

    Public Sub New()
        InitializeComponent()
        BtnAddFloating.Show = True
        AddHandler Timer.Tick, Sub() RefreshProgress()
    End Sub

    Private Sub EnterPage(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        RefreshList()
        Timer.Start()
    End Sub

    Private Sub LeavePage(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        Timer.Stop()
    End Sub

    Public Sub RefreshList()
        Dim Entries = ModServerTasks.Profiles()
        Rows.Clear()
        PanMain.Children.Clear()
        Signature = JsonConvert.SerializeObject(Entries)
        GroupSignature = String.Join(",", Entries.Select(Function(Profile) Profile.Id & ":" & (ModServerTasks.Find(Profile)?.Loader.State = LoadState.Loading).ToString()).OrderBy(Function(Value) Value))
        PanEmpty.Visibility = If(Entries.Count = 0, Visibility.Visible, Visibility.Collapsed)
        For Each Group In Entries.GroupBy(Function(Profile) If(ModServerTasks.Find(Profile)?.Loader.State = LoadState.Loading, "正在部署", "服务器"))
            Dim Card As New MyCard With {.Title = Group.Key & " (" & Group.Count() & ")", .Margin = New Thickness(0, 0, 0, 15)}
            Dim Stack As New StackPanel With {.Margin = New Thickness(20, 40, 18, 15)}
            Card.Children.Add(Stack)
            PanMain.Children.Add(Card)
            For Each Profile In Group
                Dim Selected = Profile
                Dim Item As New MyListItem With {.Title = Profile.Name, .Info = Profile.Core & " " & Profile.Version, .Height = 42, .Type = MyListItem.CheckType.Clickable, .Tag = Profile, .Logo = PathImage & "Blocks/CommandBlock.png"}
                AddHandler Item.Click,
                    Sub()
                        Dim Job = ModServerTasks.Find(Selected)
                        If Selected.Deployed Then
                            ModServerTasks.OpenProfile(Selected)
                        ElseIf Job IsNot Nothing AndAlso (Job.Loader.State = LoadState.Loading OrElse Job.Loader.State = LoadState.Failed) Then
                            ModServerTasks.OpenDeployment(Job)
                        Else
                            ModServerTasks.OpenProfile(Selected)
                        End If
                    End Sub
                Dim Manage As New MyIconButton With {.Logo = Logo.IconButtonSetup, .ToolTip = "服务器管理"}
                AddHandler Manage.Click, Sub() ModServerTasks.OpenProfile(Selected)
                Dim Export As New MyIconButton With {.Logo = Logo.IconButtonSave, .ToolTip = "导出客户端整合包"}
                AddHandler Export.Click, Sub() ExportClient(Selected)
                Dim Delete As New MyIconButton With {.Logo = Logo.IconButtonDelete, .ToolTip = "删除服务器记录", .Theme = MyIconButton.Themes.Red, .IsEnabled = Not If(ModServerTasks.Find(Profile)?.Loader.State = LoadState.Loading, False)}
                AddHandler Delete.Click, Sub() DeleteServer(Selected)
                Item.Buttons = If(Profile.Deployed, {Export, Manage, Delete}, {Manage, Delete})
                Stack.Children.Add(Item)
                Dim Track As New Border With {.Height = 3, .Margin = New Thickness(6, 0, 6, 8)}
                Track.SetResourceReference(Border.BackgroundProperty, "ColorBrush6")
                Dim Fill As New Border With {.HorizontalAlignment = HorizontalAlignment.Left, .Width = 0}
                Fill.SetResourceReference(Border.BackgroundProperty, "ColorBrush3")
                Track.Child = Fill
                Stack.Children.Add(Track)
                Rows(Profile.Id) = Tuple.Create(Profile, Item, Track, Fill)
            Next
        Next
        RefreshProgress()
    End Sub
    Public Sub Refresh() Implements IRefreshable.Refresh
        RefreshList()
    End Sub

    Private Sub RefreshProgress()
        Dim Groups = String.Join(",", Rows.Values.Select(Function(Row) Row.Item1.Id & ":" & (ModServerTasks.Find(Row.Item1)?.Loader.State = LoadState.Loading).ToString()).OrderBy(Function(Value) Value))
        If Groups <> GroupSignature Then
            RefreshList()
            Return
        End If
        If Signature <> Settings.Get(Of String)("ServerProfiles") Then
            Dim Entries = ModServerTasks.Profiles()
            If Signature <> JsonConvert.SerializeObject(Entries) Then
                RefreshList()
                Return
            End If
        End If
        For Each Row In Rows.Values
            Dim Profile = Row.Item1
            Dim Job = ModServerTasks.Find(Profile)
            Row.Item2.Info = Profile.Core & " " & Profile.Version & " · " & ModServerTasks.StatusText(Profile)
            Row.Item2.ToolTip = Profile.Host & vbCrLf & ModServerTasks.StatusText(Profile)
            Row.Item3.Visibility = If(Job IsNot Nothing, Visibility.Visible, Visibility.Collapsed)
            If Job IsNot Nothing Then Row.Item4.Width = Math.Max(0, Row.Item3.ActualWidth) * If(Job.Loader.State = LoadState.Finished, 1, Job.StageProgress)
        Next
    End Sub

    Private Sub AddServer(sender As Object, e As MouseButtonEventArgs) Handles BtnNew.Click, BtnAddFloating.Click
        ModServerTasks.OpenProfile(New ServerProfile)
    End Sub
    Private Sub DeleteServer(Profile As ServerProfile)
        Try
            If ModServerTasks.Find(Profile)?.Loader.State = LoadState.Loading Then Throw New InvalidOperationException("该服务器正在部署，请等待任务结束后再删除记录。")
            If MyMsgBox("从我的服务器移除“" & Profile.Name & "”？" & vbCrLf & "只删除本地连接记录，不停止远端服务器，不删除世界、服务端或客户端文件。", "删除服务器记录", "删除", "取消", IsWarn:=True) <> 1 Then Return
            ModServerTasks.DeleteProfile(Profile.Id)
            RefreshList()
            FrmPlayServers?.RenderServers()
        Catch ex As Exception
            Hint(ex.Message, HintType.Red)
        End Try
    End Sub
    Private Sub ExportClient(Profile As ServerProfile)
        Try
            If Not File.Exists(Profile.ClientPack) Then Throw New InvalidOperationException("尚无客户端整合包，请先在服务器管理中同步客户端。")
            Dim Dialog As New Microsoft.Win32.SaveFileDialog With {.Filter = "Modrinth 整合包|*.mrpack", .FileName = "CPT-client.mrpack"}
            If Dialog.ShowDialog() = True Then File.Copy(Profile.ClientPack, Dialog.FileName, True)
        Catch ex As Exception
            Hint(ex.Message, HintType.Red)
        End Try
    End Sub
End Class
