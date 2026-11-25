Imports System.Windows.Threading

Public Class PageServerDeployRight
    Private Job As ServerDeployment
    Private ReadOnly Timer As New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(200)}
    Private ReadOnly Steps As New List(Of MyListItem)
    Private LastStage As Double = -1
    Private LastTransfer As Double = -1

    Public Sub New()
        InitializeComponent()
        For Each StepName In {"连接服务器并验证 root", "保护端口、准备 Java 与安装服务端", "启动 Java 服务端进程", "等待 Minecraft 与 RCON 就绪", "生成并同步客户端配置"}
            Dim Item As New MyListItem With {.Title = StepName, .Height = 42, .Margin = New Thickness(0, 0, 0, 8), .IsHitTestVisible = False, .Info = "等待", .Logo = PathImage & "Blocks/CommandBlock.png"}
            Steps.Add(Item)
            PanSteps.Children.Add(Item)
        Next
        AddHandler Timer.Tick, Sub() Refresh()
    End Sub

    Public Sub Bind(Value As ServerDeployment)
        Job = Value
        LastStage = -1
        LastTransfer = -1
        Refresh()
    End Sub

    Private Sub EnterPage(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        Refresh()
        Timer.Start()
    End Sub

    Private Sub LeavePage(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        Timer.Stop()
        DetailProgress.IsIndeterminate = False
        StageProgress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, Nothing)
        DetailProgress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, Nothing)
    End Sub

    Public Sub Refresh()
        If Job Is Nothing Then Return
        If Job.Loader.State <> LoadState.Loading AndAlso Job.Loader.State <> LoadState.Waiting Then
            If Job.Finished Is Nothing Then Job.Finished = Date.Now
        End If
        LabHeading.Text = Job.Profile.Name
        LabStage.Text = Job.Stage
        Dim Running = Job.Loader.State = LoadState.Loading
        Dim Paused = Job.Control.IsPaused
        BtnPause.Visibility = If(Running AndAlso Not Job.Control.PauseRequested, Visibility.Visible, Visibility.Collapsed)
        BtnResume.Visibility = If(Running AndAlso Job.Control.PauseRequested, Visibility.Visible, Visibility.Collapsed)
        BtnPause.IsEnabled = Running
        BtnRetryDeployment.Visibility = If(Job.Loader.State = LoadState.Failed, Visibility.Visible, Visibility.Collapsed)
        BtnResume.IsEnabled = Running
        If Job.Control.PauseRequested Then LabStage.Text = If(Paused, "部署已暂停 · ", "正在等待安全暂停点 · ") & Job.Stage
        Dim Value = If(Job.Loader.State = LoadState.Finished, 100, Job.StageProgress * 100)
        If Value <> LastStage Then
            AnimateProgress(StageProgress, Value)
            LastStage = Value
        End If
        LabProgress.Text = If(Job.Loader.State = LoadState.Finished, "5 / 5 阶段完成", Math.Max(0, Job.StageIndex - 1) & " / 5 阶段完成")
        LabDetail.Text = Job.Detail
        LabTransfer.Text = Job.TransferText
        DetailProgress.IsIndeterminate = Running AndAlso Not Paused AndAlso Not Job.TransferKnown
        If Paused Then
            StageProgress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, Nothing)
            StageProgress.Value = Value
            DetailProgress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, Nothing)
            DetailProgress.Value = If(Job.TransferKnown, Job.TransferProgress * 100, 0)
        End If
        If Not Job.TransferKnown Then
            LastTransfer = -1
            DetailProgress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, Nothing)
            DetailProgress.Value = 0
        End If
        If Job.TransferKnown AndAlso Job.TransferProgress <> LastTransfer Then
            AnimateProgress(DetailProgress, Job.TransferProgress * 100)
            LastTransfer = Job.TransferProgress
        End If
        If Job.TransferKnown Then LabTransfer.Text &= " · " & (Job.TransferProgress * 100).ToString("F1") & "%"
        If Paused Then
            LabTransfer.Text &= " · 已暂停，点击继续部署恢复"
        ElseIf Running AndAlso (Date.Now - Job.LastUpdate).TotalSeconds >= 20 Then
            LabTransfer.Text &= " · 等待远端输出，当前任务未完成"
        End If
        If Not SystemParameters.ClientAreaAnimation OrElse AniControlEnabled <> 0 Then DetailProgress.IsIndeterminate = False
        LabElapsed.Text = "已用时 " & Math.Floor((If(Job.Finished, Date.Now) - Job.Started).TotalSeconds) & " 秒" & If(Running, " · 最近输出 " & Math.Floor((Date.Now - Job.LastUpdate).TotalSeconds) & " 秒前", "")
        BtnManage.IsEnabled = Job.Loader.State <> LoadState.Loading
        For i = 0 To Steps.Count - 1
            Steps(i).Info = If(Job.Loader.State = LoadState.Finished OrElse i + 1 < Job.StageIndex, "已完成", If(i + 1 = Job.StageIndex, If(Job.Loader.State = LoadState.Failed, "失败", "进行中"), "等待"))
        Next
        UpdateLog(LabLog, Job.LogText())
        UpdateLog(LabTerminal, Job.TerminalText())
        PanResult.Visibility = If(Job.Loader.State = LoadState.Finished OrElse Job.Loader.State = LoadState.Failed, Visibility.Visible, Visibility.Collapsed)
        If Job.Loader.State = LoadState.Finished Then
            LabResult.Text = "服务器已运行。连接地址：" & PCLCS.Server.ServerManager.ValidateGameAddress(Job.Profile)
        ElseIf Job.Loader.State = LoadState.Failed Then
            LabResult.Text = Job.Loader.Error.Message & vbCrLf & "返回服务器管理查看配置或远程日志后重试。"
        End If
    End Sub
    Private Sub RetryDeployment() Handles BtnRetryDeployment.Click
        If Job Is Nothing OrElse Job.Loader.State <> LoadState.Failed Then Return
        ModServerTasks.OpenProfile(Job.Profile)
        FrmServer.OpenResume(Job.Profile)
    End Sub
    Private Sub PauseClick() Handles BtnPause.Click
        Job?.PauseDeployment()
        Refresh()
    End Sub
    Private Sub ResumeClick() Handles BtnResume.Click
        Job?.ResumeDeployment()
        Refresh()
    End Sub
    Private Sub AnimateProgress(Bar As ProgressBar, Value As Double)
        If IsLoaded AndAlso AniControlEnabled = 0 AndAlso SystemParameters.ClientAreaAnimation Then
            Dim Animation As New System.Windows.Media.Animation.DoubleAnimation(Bar.Value, Value, TimeSpan.FromMilliseconds(240)) With {.EasingFunction = New System.Windows.Media.Animation.CubicEase With {.EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut}}
            Bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, Animation)
        Else
            Bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, Nothing)
            Bar.Value = Value
        End If
    End Sub
    Private Sub UpdateLog(Box As TextBox, Text As String)
        If Box.Text = Text Then Return
        Dim Position = Box.VerticalOffset
        Box.Text = Text
        If FollowLog.Checked Then Box.ScrollToEnd() Else Box.ScrollToVerticalOffset(Position)
    End Sub
    Private Sub CopyLog() Handles BtnCopyLog.Click
        If Job IsNot Nothing Then ClipboardSet(If(LogTabs.SelectedIndex = 1, Job.TerminalText(), Job.LogText()))
    End Sub
    Private Sub BackToList(sender As Object, e As MouseButtonEventArgs) Handles BtnBack.Click
        FrmMain.PageChange(FormMain.PageType.Server)
    End Sub
    Private Sub Manage(sender As Object, e As MouseButtonEventArgs) Handles BtnManage.Click
        If Job IsNot Nothing Then ModServerTasks.OpenProfile(Job.Profile)
    End Sub
End Class
