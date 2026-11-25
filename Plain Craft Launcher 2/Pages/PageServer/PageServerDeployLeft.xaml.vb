Imports System.Windows.Threading

Public Class PageServerDeployLeft
    Private Job As ServerDeployment
    Private FinishedElapsed As String
    Private ReadOnly Timer As New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(200)}

    Public Sub New()
        InitializeComponent()
        AddHandler Timer.Tick, Sub() Refresh()
    End Sub

    Public Sub Bind(Value As ServerDeployment)
        Job = Value
        FinishedElapsed = Nothing
        LoadDeploy.State = Job.Loader
        Refresh()
    End Sub

    Private Sub EnterPage(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        Refresh()
        Timer.Start()
    End Sub

    Private Sub LeavePage(sender As Object, e As RoutedEventArgs) Handles Me.Unloaded
        Timer.Stop()
    End Sub

    Public Sub Refresh()
        If Job Is Nothing Then Return
        LabName.Text = Job.Profile.Name & vbCrLf & Job.Profile.Core & " " & Job.Profile.Version
        LabTitle.Text = If(Job.Loader.State = LoadState.Finished, "部署完成", If(Job.Loader.State = LoadState.Failed, "部署失败", "正在部署服务器"))
        LabStage.Text = Job.Stage
        If Job.Control.PauseRequested Then LabStage.Text = If(Job.Control.IsPaused, "已暂停 · ", "正在暂停 · ") & Job.Stage
        Dim Progress = If(Job.Loader.State = LoadState.Finished, 1, Math.Max(0, Math.Min(1, Job.StageProgress)))
        If IsLoaded Then
            AniStart({AaGridLengthWidth(ProgressFinished, Progress - ProgressFinished.Width.Value, 130,, New AniEaseOutFluent),
                AaGridLengthWidth(ProgressRemaining, 1 - Progress - ProgressRemaining.Width.Value, 130,, New AniEaseOutFluent)}, "Server deploy progress")
        Else
            ProgressFinished.Width = New GridLength(Progress, GridUnitType.Star)
            ProgressRemaining.Width = New GridLength(1 - Progress, GridUnitType.Star)
        End If
        LabProgress.Text = If(Job.Loader.State = LoadState.Finished, "5 / 5 阶段完成", Math.Max(0, Job.StageIndex - 1) & " / 5 阶段完成")
        If FinishedElapsed Is Nothing Then
            LabElapsed.Text = "已用时 " & Math.Floor((Date.Now - Job.Started).TotalSeconds) & " 秒"
            If Job.Loader.State = LoadState.Finished OrElse Job.Loader.State = LoadState.Failed Then FinishedElapsed = LabElapsed.Text
        Else
            LabElapsed.Text = FinishedElapsed
        End If
        BtnManage.IsEnabled = Job.Loader.State <> LoadState.Loading
    End Sub

    Private Sub BackToList(sender As Object, e As MouseButtonEventArgs) Handles BtnBack.Click
        FrmMain.PageChange(FormMain.PageType.Server)
    End Sub

    Private Sub Manage(sender As Object, e As MouseButtonEventArgs) Handles BtnManage.Click
        If Job IsNot Nothing Then ModServerTasks.OpenProfile(Job.Profile)
    End Sub
End Class
