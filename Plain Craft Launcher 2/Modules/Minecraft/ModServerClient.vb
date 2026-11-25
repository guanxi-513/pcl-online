Imports PCLCS.Server

Public Module ModServerClient
    Public SelectedServerId As String
    Public ClientInstalling As Boolean

    Public Function SelectedServer() As ServerProfile
        Return ModServerTasks.Profiles().FirstOrDefault(Function(Profile) Profile.Id = SelectedServerId)
    End Function

    Public Function Ready(Profile As ServerProfile) As Boolean
        If Profile IsNot Nothing AndAlso Profile.ClientInstance <> "" Then ModDataPaths.MigrateInstanceData(McFolderSelected & "versions\" & Profile.ClientInstance)
        Return Profile IsNot Nothing AndAlso Profile.ClientInstance <> "" AndAlso
            File.Exists(McFolderSelected & "versions\" & Profile.ClientInstance & "\" & Profile.ClientInstance & ".json") AndAlso
            File.Exists(McFolderSelected & "versions\" & Profile.ClientInstance & "\CPT\CraftPilotClient.json")
    End Function

    Public Async Function Synchronize(Profile As ServerProfile, Password As String, LaunchAfter As Boolean) As Task
        If ClientInstalling Then Throw New InvalidOperationException("已有客户端正在同步。")
        If McLaunchLoader.State = LoadState.Loading Then Throw New InvalidOperationException("游戏正在启动，请稍后再同步。")
        ClientInstalling = True
        Try
            Dim NameId As Guid
            If Not Guid.TryParseExact(Profile.Id, "N", NameId) Then Throw New InvalidDataException("服务器标识无效。")
            Dim Pack = Paths.Base & "CPT\ServerClients\" & Profile.Id & ".mrpack"
            Dim Metadata As JObject = Nothing
            Try
                Metadata = Await Task.Run(Function() ServerManager.FetchClientPack(Profile, Password, Pack))
            Catch ex As RootPrivilegeRequiredException
                Profile.ActiveElevation = ex.Method
                Profile.RootPassword = MyMsgBoxInput("取得 root 权限", If(ex.Method = "sudo", "请输入当前 SSH 用户的 sudo 密码以读取客户端配置。", "请输入 root 密码，通过 su - root 读取客户端配置。") & "仅本次同步使用，不保存。", HintText:=If(ex.Method = "sudo", "sudo 密码", "root 密码"), MaskInput:=True)
                If String.IsNullOrEmpty(Profile.RootPassword) Then Throw New OperationCanceledException()
            End Try
            If Metadata Is Nothing Then Metadata = Await Task.Run(Function() ServerManager.FetchClientPack(Profile, Password, Pack))
            If DirectCast(Metadata("unresolved"), JArray).Count > 0 Then
                Throw New InvalidOperationException("无法确认以下 Mod 的客户端要求，请管理员核对后再同步：" & vbCrLf & Metadata("unresolved").ToString())
            End If
            Profile.Version = Metadata("minecraft").ToString()
            Profile.Core = Metadata("core").ToString()
            Profile.LoaderVersion = Metadata("loaderVersion").ToString()
            Profile.Deployed = True
            Profile.ClientPack = Pack
            Dim Revision = Metadata("revision").ToString()
            If Not System.Text.RegularExpressions.Regex.IsMatch(Revision, "\A[0-9a-f]{64}\z") Then Throw New InvalidDataException("客户端配置指纹无效。")
            Await Task.Run(Sub() McFolderListLoader.WaitForExitTime(30000))
            If McFolderSelected = "" Then Throw New InvalidOperationException("Minecraft 文件夹尚未加载完成，请稍后重试。")
            Dim InstanceName = "CraftPilot-" & Profile.Id.Substring(0, 8) & "-" & Revision.Substring(0, 12)
            Dim ManagedFolder = McFolderSelected & "versions\" & InstanceName & "\"
            ModDataPaths.MigrateInstanceData(ManagedFolder)
            If Profile.ClientRevision <> Revision OrElse Not Ready(Profile) Then
                If Directory.Exists(ManagedFolder) AndAlso Not File.Exists(ManagedFolder & "CPT\CraftPilotClient.json") Then Throw New InvalidOperationException("客户端目标目录已存在但未通过管理校验，请在版本选择中检查或更名后重试。")
                If Not File.Exists(ManagedFolder & InstanceName & ".json") Then
                    Dim Install = Await Task.Run(Function() ModpackInstall(Pack, InstanceName))
                    If Install Is Nothing Then Throw New InvalidOperationException("客户端安装未开始。")
                    Await Task.Run(Sub() Install.WaitForExit())
                    Directory.CreateDirectory(ManagedFolder & "CPT")
                    File.WriteAllText(ManagedFolder & "CPT\CraftPilotClient.json", Metadata.ToString())
                End If
                Profile.ClientInstance = InstanceName
                Profile.ClientRevision = Revision
            End If
            ModServerTasks.SaveProfile(Profile)
            Dim Instance As New McInstance(Profile.ClientInstance)
            Instance.Load()
            If Not Instance.Check() Then Throw New InvalidOperationException("客户端版本检查失败，请重新下载客户端。")
            Using Archive = ZipFile.OpenRead(Pack)
                Dim Manifest As JObject
                Using Reader As New StreamReader(Archive.GetEntry("modrinth.index.json").Open())
                    Manifest = JObject.Parse(Reader.ReadToEnd())
                End Using
                For Each Entry In Manifest("files")
                    Dim Local = ManagedFolder & Entry("path").ToString().Replace("/", "\")
                    Dim Expected = Entry("hashes")("sha1").ToString()
                    Dim Valid As Boolean = False
                    If File.Exists(Local) Then
                        Using Sha = System.Security.Cryptography.SHA1.Create(), Input = File.OpenRead(Local)
                            Valid = BitConverter.ToString(Sha.ComputeHash(Input)).Replace("-", "").ToLowerInvariant() = Expected
                        End Using
                    End If
                    If Not Valid Then
                        Dim Urls = Entry("downloads").Select(Function(Url) Url.ToString()).ToList()
                        Await Task.Run(Sub() NetDownloadByLoader(Urls, Local, Check:=New FileChecker With {.Hash = Expected}))
                    End If
                Next
            End Using
            SelectedServerId = Profile.Id
            McInstanceSelected = Instance
            FrmLaunchLeft.RefreshButtonsUI()
            FrmLaunchLeft.RefreshPage(False, False)
            If LaunchAfter Then
                ModAccounts.SelectForServer(ModAccounts.BoundAccount("managed:" & Profile.Id))
                FrmMain.PageChange(FormMain.PageType.GameLaunch)
                McLaunchStart(New McLaunchOptions With {.Instance = Instance, .ServerIp = ServerManager.ValidateGameAddress(Profile)})
            Else
                Hint("客户端已安装，可以进入游戏。", HintType.Green)
            End If
        Finally
            Password = Nothing
            Profile.RootPassword = Nothing
            ClientInstalling = False
        End Try
    End Function

    Public Async Sub Enter(Profile As ServerProfile)
        Try
            If Profile Is Nothing Then
                ModServerTasks.OpenProfile(New ServerProfile)
                Return
            End If
            Dim Password = MyMsgBoxInput("同步服务器客户端", "请输入服务器 SSH 密码。仅用于此次配置同步，不保存。", HintText:="SSH 密码", MaskInput:=True)
            If Password Is Nothing Then Return
            If MyMsgBox("将从该服务器同步客户端配置，使用 CPT 安装匹配的游戏、加载器和必需 Mod。Mod 属于可执行代码，请仅连接可信服务器。", "同步客户端", "继续", "取消", IsWarn:=True) <> 1 Then Return
            Await Synchronize(Profile, Password, True)
        Catch ex As HostKeyConfirmationException
            Hint("服务器主机身份需要确认，请从我的服务器进入管理页测试连接。", HintType.Red)
        Catch ex As Exception
            Hint("客户端同步失败：" & ex.Message, HintType.Red)
        End Try
    End Sub
End Module
