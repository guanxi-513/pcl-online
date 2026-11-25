Imports PCLCS.Server

Public Module ModCommunity
    Public ReadOnly Property WebsiteEndpoint As String
        Get
            Return ProductWebsite
        End Get
    End Property
    Private UsageTimer As System.Threading.Timer
    Private Sending As Integer
    Private Checking As Integer
    Private LastPrompt As Integer

    Private Function Channel() As String
        Return If(BuildType = BuildTypes.Release, "release", "snapshot")
    End Function

    Public Sub Start()
        SyncLock GetType(ModCommunity)
            UsageTimer?.Dispose()
            UsageTimer = Nothing
            If Settings.Get(Of Boolean)("CommunityUsageEnabled") AndAlso Settings.Get(Of Boolean)("SystemSystemTelemetry") Then
                UsageTimer = New System.Threading.Timer(Sub() SendHeartbeat(), Nothing, 0, 15 * 60 * 1000)
            End If
        End SyncLock
    End Sub

    Private Sub SendHeartbeat()
        If Interlocked.Exchange(Sending, 1) = 1 Then Return
        Try
            If IsProgramEnding OrElse Not Settings.Get(Of Boolean)("CommunityUsageEnabled") OrElse Not Settings.Get(Of Boolean)("SystemSystemTelemetry") Then Return
            Dim Endpoint = WebsiteEndpoint
            Dim Id = Settings.Get(Of String)("CommunityInstallationId")
            If Id = "" Then
                Id = Guid.NewGuid().ToString()
                Settings.Set("CommunityInstallationId", Id)
            End If
            CommunityClient.Heartbeat(Endpoint, Id, VersionBaseName, Channel()).GetAwaiter().GetResult()
        Catch ex As Exception
            Logger.Info("官网匿名统计暂不可用")
        Finally
            Interlocked.Exchange(Sending, 0)
        End Try
    End Sub

    Public Sub CheckUpdates(Optional Manual As Boolean = False)
        If Interlocked.Exchange(Checking, 1) = 1 Then Return
        RunInNewThread(
        Sub()
            Try
                Dim Endpoint = WebsiteEndpoint
                If Not Manual AndAlso Settings.Get(Of Integer)("SystemSystemUpdate") >= 2 Then Return
                Dim Release = CommunityClient.Latest(Endpoint, Channel()).GetAwaiter().GetResult()
                If Release Is Nothing OrElse Release("buildCode").Value(Of Integer)() <= VersionCode Then
                    If Manual Then Hint("当前没有可用更新。", HintType.Green)
                    Return
                End If
                Dim Code = Release("buildCode").Value(Of Integer)()
                If Not Manual AndAlso LastPrompt = Code Then Return
                LastPrompt = Code
                Dim ExtractionCode = Release("extractionCode").ToString()
                Dim Description = "版本：" & Release("version").ToString() & vbCrLf & Release("notes").ToString() & vbCrLf &
                    If(ExtractionCode = "", "", "提取码：" & ExtractionCode & vbCrLf) & "从迅雷网盘下载后，关闭启动器并替换原文件。"
                If MyMsgBox(Description, "CPT 更新", "打开迅雷网盘", "稍后") = 1 Then
                    OpenWebsite(CommunityClient.ValidateDownload(Release("url").ToString()))
                End If
            Catch ex As Exception
                If Manual Then Hint("检查更新失败：" & ex.Message, HintType.Red)
            Finally
                Interlocked.Exchange(Checking, 0)
            End Try
        End Sub, "Community update check")
    End Sub
End Module
