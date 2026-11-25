Imports PCLCS.Server

Public Class ServerDeployment
    Public Profile As ServerProfile
    Public Loader As LoaderTask(Of Integer, JObject)
    Public Stage As String = "等待部署"
    Public StageIndex As Integer
    Public StageProgress As Double
    Public Started As Date = Date.Now
    Public Detail As String = "等待远端任务"
    Public TransferProgress As Double
    Public TransferKnown As Boolean
    Public TransferText As String = ""
    Public LastUpdate As Date = Date.Now
    Public Finished As Date?
    Public ReadOnly Control As New DeploymentControl()
    Public Sub PauseDeployment()
        If Loader IsNot Nothing AndAlso Loader.State = LoadState.Loading Then Control.Pause()
    End Sub
    Public Sub ResumeDeployment()
        Control.Resume()
    End Sub
    Private ReadOnly TerminalLines As New List(Of String)
    Private ReadOnly Lines As New List(Of String)

    Public Sub Report(Text As String)
        SyncLock Lines
            Stage = Text.Split({vbCrLf, vbLf}, StringSplitOptions.None)(0)
            If Stage.Length > 240 Then Stage = Stage.Substring(0, 240) & "…"
            Dim Index As Integer
            If Text.Length >= 3 AndAlso Text.Substring(1, 2) = "/5" AndAlso Integer.TryParse(Text.Substring(0, 1), Index) Then
                StageIndex = Index
                StageProgress = Math.Min(0.99, (Index - 1) / 5.0)
                TransferKnown = False
                TransferText = ""
                Detail = Text
                Loader.Progress = StageProgress
            End If
            Lines.Add($"[{Date.Now:HH:mm:ss}] {Text}")
            If Lines.Count > 2000 Then Lines.RemoveAt(0)
            LastUpdate = Date.Now
        End SyncLock
    End Sub

    Public Sub UpdateRemote(Value As DeploymentUpdate)
        SyncLock Lines
            LastUpdate = Date.Now
            Dim Text = System.Text.RegularExpressions.Regex.Replace(Value.Message, "(?i)((?:rcon\.password|password|access_token|refresh_token)\s*[=:]\s*)\S+", "$1[已隐藏]")
            If Value.Kind = "paused" OrElse Value.Kind = "resumed" Then
                Lines.Add($"[{Date.Now:HH:mm:ss}] {Text}")
            ElseIf Value.Kind = "terminal" Then
                TerminalLines.Add($"[{Date.Now:HH:mm:ss}] {Text}")
                If TerminalLines.Count > 2000 Then TerminalLines.RemoveAt(0)
            ElseIf Value.Kind = "download" Then
                Detail = Text
                TransferKnown = Value.Total > 0
                TransferProgress = If(TransferKnown, Math.Min(1, Value.Bytes / CDbl(Value.Total)), 0)
                TransferText = (Value.Bytes / 1048576.0).ToString("F2") & " MiB" & If(TransferKnown, " / " & (Value.Total / 1048576.0).ToString("F2") & " MiB", "") & If(Value.Speed > 0, " · " & (Value.Speed / 1048576.0).ToString("F2") & " MiB/s", "")
            Else
                Detail = Text
                TransferKnown = False
                TransferText = ""
                Lines.Add($"[{Date.Now:HH:mm:ss}] {Text}")
                If Lines.Count > 2000 Then Lines.RemoveAt(0)
            End If
        End SyncLock
    End Sub

    Public Function TerminalText() As String
        SyncLock Lines
            Return String.Join(vbCrLf, TerminalLines)
        End SyncLock
    End Function

    Public Function LogText() As String
        SyncLock Lines
            Return String.Join(vbCrLf, Lines)
        End SyncLock
    End Function
End Class

Public Module ModServerTasks
    Public ReadOnly Deployments As New ConcurrentDictionary(Of String, ServerDeployment)
    Private ReadOnly Statuses As New ConcurrentDictionary(Of String, String)

    Public Function Profiles() As List(Of ServerProfile)
        Dim Json = JArray.Parse(Settings.Get(Of String)("ServerProfiles"))
        If Json.OfType(Of JObject)().Any(Function(Entry) Entry("Id") Is Nothing) Then
            For Each Entry As JObject In Json
                If Entry("Id") Is Nothing Then Entry("Id") = Guid.NewGuid().ToString("N")
            Next
            Settings.Set("ServerProfiles", Json.ToString(Formatting.None))
        End If
        Dim Result = Json.ToObject(Of List(Of ServerProfile))()
        For Each Profile In If(Result, New List(Of ServerProfile))
            If Profile.ClientPack <> "" AndAlso Not File.Exists(Profile.ClientPack) Then
                Dim Candidate = Paths.Base & "CPT\ServerClients\" & Profile.Id & ".mrpack"
                If File.Exists(Candidate) Then Profile.ClientPack = Candidate
            End If
        Next
        Return If(Result, New List(Of ServerProfile))
    End Function

    Public Sub SaveProfile(Profile As ServerProfile)
        Dim Entries = Profiles()
        Dim Index = Entries.FindIndex(Function(Entry) Entry.Id = Profile.Id)
        If Index < 0 Then
            Entries.Add(Profile)
        Else
            Entries(Index) = Profile
        End If
        Settings.Set("ServerProfiles", JsonConvert.SerializeObject(Entries))
    End Sub

    Public Function Find(Profile As ServerProfile) As ServerDeployment
        Dim Job As ServerDeployment = Nothing
        Deployments.TryGetValue(Profile.Id, Job)
        Return Job
    End Function

    Public Sub DeleteProfile(Id As String)
        Dim Entries = Profiles()
        Dim Profile = Entries.FirstOrDefault(Function(Entry) Entry.Id = Id)
        If Profile Is Nothing Then Return
        If Find(Profile)?.Loader.State = LoadState.Loading Then Throw New InvalidOperationException("该服务器正在部署，请等待任务结束后再删除记录。")
        Entries.Remove(Profile)
        Settings.Set("ServerProfiles", JsonConvert.SerializeObject(Entries))
        Dim Bindings = JObject.Parse(Settings.Get(Of String)("ServerGameAccounts"))
        Bindings.Remove("managed:" & Id)
        Settings.Set("ServerGameAccounts", Bindings.ToString(Formatting.None))
        Dim Job As ServerDeployment = Nothing
        Deployments.TryRemove(Id, Job)
        Dim Status As String = Nothing
        Statuses.TryRemove(Id, Status)
        If ModServerClient.SelectedServerId = Id Then ModServerClient.SelectedServerId = Nothing
    End Sub

    Public Function StatusText(Profile As ServerProfile) As String
        Dim Job = Find(Profile)
        If Job IsNot Nothing Then
            Select Case Job.Loader.State
                Case LoadState.Loading
                    Return If(Job.Control.IsPaused, "部署已暂停 · ", If(Job.Control.PauseRequested, "正在暂停 · ", "部署中 · ")) & Job.Stage
                Case LoadState.Failed
                    Return "部署失败 · " & Job.Loader.Error.Message
            End Select
        End If
        Dim Value As String = Nothing
        Return If(Statuses.TryGetValue(Profile.Id, Value), Value, If(Job IsNot Nothing AndAlso Job.Loader.State = LoadState.Finished, "运行中 · 部署完成", "状态未查询"))
    End Function

    Public Sub SetStatus(Profile As ServerProfile, Text As String)
        Statuses(Profile.Id) = Text
    End Sub

    Public Function BeginDeployment(Profile As ServerProfile, Password As String, Request As JObject, Optional LocalPack As String = Nothing) As ServerDeployment
        Dim RootPassword = Profile.RootPassword
        Dim ActiveElevation = Profile.ActiveElevation
        Profile = JsonConvert.DeserializeObject(Of ServerProfile)(JsonConvert.SerializeObject(Profile))
        Profile.RootPassword = RootPassword
        Profile.ActiveElevation = ActiveElevation
        RootPassword = Nothing
        Dim Existing = Find(Profile)
        If Existing IsNot Nothing AndAlso Existing.Loader.State = LoadState.Loading Then Throw New InvalidOperationException("该服务器已有部署任务。")
        If Deployments.Values.Any(Function(Entry) Entry.Loader.State = LoadState.Loading AndAlso Entry.Profile.Host = Profile.Host AndAlso Entry.Profile.SshPort = Profile.SshPort AndAlso Entry.Profile.Root = Profile.Root) Then Throw New InvalidOperationException("该服务器目录正在部署。")
        SaveProfile(JsonConvert.DeserializeObject(Of ServerProfile)(JsonConvert.SerializeObject(Profile)))
        Dim Job As New ServerDeployment With {.Profile = Profile}
        'Keep credentials only in the running delegate; clear it when the task exits.
        Job.Loader = New LoaderTask(Of Integer, JObject)("服务器部署 · " & Profile.Name,
        Sub(Task)
            Try
                Dim Result = ServerManager.DeployAndStart(Profile, Password, Request, AddressOf Job.Report, LocalPack, AddressOf Job.UpdateRemote, Job.Control,
                    Sub(Installed) RunInUiWait(Sub() SaveProfile(JsonConvert.DeserializeObject(Of ServerProfile)(JsonConvert.SerializeObject(Profile)))))
                Job.Control.Checkpoint()
                Profile.Windows = Result("windows").Value(Of Boolean)()
                Profile.Root = Result("root").ToString()
                Profile.Memory = Result("memory").Value(Of Integer)()
                Profile.Core = Result("core").ToString()
                Profile.Version = Result("version").ToString()
                Profile.GamePort = Result("port").Value(Of Integer)()
                Profile.Deployed = True
                Profile.LoaderVersion = Result("loaderVersion").ToString()
                RunInUiWait(Sub() SaveProfile(JsonConvert.DeserializeObject(Of ServerProfile)(JsonConvert.SerializeObject(Profile))))
                Dim Pack = Paths.Base & "CPT\ServerClients\" & Profile.Id & ".mrpack"
                ServerManager.FetchClientPack(Profile, Password, Pack)
                Profile.ClientPack = Pack
                RunInUiWait(Sub() SaveProfile(JsonConvert.DeserializeObject(Of ServerProfile)(JsonConvert.SerializeObject(Profile))))
                Task.Output = Result
                Job.Report("5/5 部署与客户端配置同步完成。")
                Job.StageProgress = 1
                SetStatus(Profile, "运行中 · 部署完成")
            Catch ex As Exception
                If Profile.Deployed Then RunInUiWait(Sub() SaveProfile(JsonConvert.DeserializeObject(Of ServerProfile)(JsonConvert.SerializeObject(Profile))))
                Job.Report("部署失败：" & ex.Message)
                Throw
            Finally
                Password = Nothing
                Profile.RootPassword = Nothing
                Job.Finished = Date.Now
                Job.Control.Finish()
                Task.LoadDelegate = Nothing
            End Try
        End Sub)
        Deployments(Profile.Id) = Job
        Job.Loader.Start()
        Return Job
    End Function

    Public Sub OpenProfile(Profile As ServerProfile)
        If FrmServer Is Nothing Then FrmServer = New PageServer
        FrmServer.OpenProfile(Profile)
        FrmMain.PageChange(New FormMain.PageStackData With {.Page = If(Profile.Deployed, FormMain.PageType.ServerManage, FormMain.PageType.ServerCreate), .Additional = Profile.Id})
    End Sub

    Public Sub OpenDeployment(Job As ServerDeployment)
        If FrmServerDeployLeft Is Nothing Then FrmServerDeployLeft = New PageServerDeployLeft
        If FrmServerDeployRight Is Nothing Then FrmServerDeployRight = New PageServerDeployRight
        FrmServerDeployLeft.Bind(Job)
        FrmServerDeployRight.Bind(Job)
        FrmMain.PageChange(New FormMain.PageStackData With {.Page = FormMain.PageType.ServerDeploy, .Additional = Job.Profile.Id})
    End Sub
End Module
