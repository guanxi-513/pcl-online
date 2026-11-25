Imports System.Security.Cryptography

Public Class GameAccount
    Public Id As String = Guid.NewGuid().ToString("N")
    Public Label As String = ""
    Public Type As McLoginType = McLoginType.Legacy
    Public UserName As String = ""
    Public Password As String = ""
    Public AuthServer As String = ""
    Public RefreshToken As String = ""
    Public Provider As String = ""
    Public DisplayName As String = ""
    Public Uuid As String = ""
    Public SkinPath As String = ""

    Public ReadOnly Property LoginMethod As String
        Get
            If Provider = "LittleSkin" Then Return "LittleSkin 登录"
            Select Case Type
                Case McLoginType.Ms : Return "微软账号登录"
                Case McLoginType.Auth : Return "第三方账号登录"
                Case McLoginType.Nide : Return "统一通行证登录"
                Case Else : Return "离线登录"
            End Select
        End Get
    End Property

    Public ReadOnly Property PlayerName As String
        Get
            Return If(DisplayName <> "", DisplayName, If(Type = McLoginType.Legacy OrElse Type = McLoginType.Ms, UserName, "尚未验证角色"))
        End Get
    End Property

    Public Function EffectiveAuthServer() As String
        Return If(Provider = "LittleSkin", ModAccounts.LittleSkinEndpoint, AuthServer)
    End Function

    Public Function Input() As McLoginData
        Select Case Type
            Case McLoginType.Legacy
                Return New McLoginLegacy With {.UserName = UserName, .SkinType = Settings.Get(Of Integer)("LaunchSkinType"), .SkinName = Settings.Get(Of String)("LaunchSkinID")}
            Case McLoginType.Ms
                Return New McLoginMs With {.UserName = UserName, .OAuthRefreshToken = RefreshToken}
            Case McLoginType.Auth, McLoginType.Nide
                Dim Server = EffectiveAuthServer()
                If Type = McLoginType.Auth Then
                    Dim Address As Uri = Nothing
                    If Not Uri.TryCreate(Server, UriKind.Absolute, Address) OrElse Address.Scheme <> "https" OrElse Address.UserInfo <> "" OrElse Address.Query <> "" OrElse Address.Fragment <> "" Then Throw New InvalidOperationException("第三方账号认证地址必须为有效的 HTTPS 地址，请在设置中检查。")
                ElseIf Not Server.RegexCheck("^[A-Za-z0-9_-]{1,100}$") Then
                    Throw New InvalidOperationException("统一通行证服务器 ID 无效，请在设置中检查。")
                End If
                Dim Base = If(Type = McLoginType.Auth, Server.TrimEnd("/"c), "https://auth.mc-user.com:233/" & Server)
                Return New McLoginServer(Type) With {.UserName = UserName, .Password = Password, .BaseUrl = Base & "/authserver", .Token = If(Type = McLoginType.Auth, "Auth", "Nide"), .Description = If(Provider = "LittleSkin", "LittleSkin", If(Type = McLoginType.Auth, "Authlib-Injector", "统一通行证"))}
            Case Else
                Throw New InvalidOperationException("不支持的账号类型。")
        End Select
    End Function
End Class

Public Module ModAccounts
    Public Const LittleSkinEndpoint As String = "https://littleskin.cn/api/yggdrasil"
    Private Entries As List(Of GameAccount)
    Private ReadOnly Gate As New Object
    Private Active As GameAccount
    Private PendingId As String
    Private StoragePath As String = Paths.Base & "CPT\accounts.dat"

    Public Function Accounts() As List(Of GameAccount)
        SyncLock Gate
            If Entries Is Nothing Then
                Dim Path = StoragePath
                If File.Exists(Path) Then
                    Dim Data = ProtectedData.Unprotect(File.ReadAllBytes(Path), Nothing, DataProtectionScope.CurrentUser)
                    Entries = JsonConvert.DeserializeObject(Of List(Of GameAccount))(Encoding.UTF8.GetString(Data))
                Else
                    Entries = New List(Of GameAccount)
                    Dim Name = Settings.Get(Of String)("LoginLegacyName").Split("¨"c).FirstOrDefault()
                    If Not String.IsNullOrWhiteSpace(Name) Then Entries.Add(New GameAccount With {.UserName = Name, .Label = Name & "（离线）"})
                    Dim Ms = JObject.Parse(Settings.Get(Of String)("LoginMsJson"))
                    For Each Entry As JProperty In Ms.Properties()
                        Entries.Add(New GameAccount With {.Type = McLoginType.Ms, .UserName = Entry.Name, .Label = Entry.Name & "（官方）", .RefreshToken = Entry.Value.ToString()})
                    Next
                    For Each Kind In {McLoginType.Auth, McLoginType.Nide}
                        Dim Prefix = If(Kind = McLoginType.Auth, "Auth", "Nide")
                        Dim Auth = Settings.Get(Of String)(If(Kind = McLoginType.Auth, "CacheAuthServerServer", "CacheNideServer"))
                        Dim User = Settings.Get(Of String)("Cache" & Prefix & "Username")
                        Dim Secret = Settings.Get(Of String)("Cache" & Prefix & "Pass")
                        If Auth <> "" AndAlso User <> "" AndAlso Secret <> "" Then Entries.Add(New GameAccount With {.Type = Kind, .UserName = User, .Password = Secret, .AuthServer = Auth, .Label = User & "（第三方）", .Provider = If(Auth.TrimEnd("/"c) = LittleSkinEndpoint, "LittleSkin", ""), .DisplayName = Settings.Get(Of String)("Cache" & Prefix & "Name"), .Uuid = Settings.Get(Of String)("Cache" & Prefix & "Uuid")})
                    Next
                    If Entries.Count > 0 Then Persist(Entries)
                End If
                If Entries Is Nothing Then Entries = New List(Of GameAccount)
            End If
            Return Entries.ToList()
        End SyncLock
    End Function

    Public Sub Save(Account As GameAccount)
        If McLaunchLoader.State = LoadState.Loading OrElse McLoginLoader.State = LoadState.Loading OrElse McLoginMsLoader.State = LoadState.Loading Then Throw New InvalidOperationException("正在登录或启动，请稍后修改账号。")
        Dim All = Accounts()
        Dim Index = All.FindIndex(Function(Item) Item.Id = Account.Id)
        If Index < 0 Then All.Add(Account) Else All(Index) = Account
        Persist(All)
    End Sub

    Private Sub Persist(All As List(Of GameAccount))
        Dim Folder = Path.GetDirectoryName(StoragePath)
        Directory.CreateDirectory(Folder)
        Dim Data = ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(All)), Nothing, DataProtectionScope.CurrentUser)
        Dim Target = StoragePath
        File.WriteAllBytes(Target & ".tmp", Data)
        If File.Exists(Target) Then File.Replace(Target & ".tmp", Target, Nothing) Else File.Move(Target & ".tmp", Target)
        SyncLock Gate
            Entries = All
        End SyncLock
    End Sub

    Public Sub Delete(Id As String)
        If McLaunchLoader.State = LoadState.Loading OrElse McLoginLoader.State = LoadState.Loading OrElse McLoginMsLoader.State = LoadState.Loading Then Throw New InvalidOperationException("正在登录或启动，不能删除账号。")
        Dim All = Accounts()
        All.RemoveAll(Function(Item) Item.Id = Id)
        Persist(All)
        If Settings.Get(Of String)("DefaultGameAccount") = Id Then Settings.Set("DefaultGameAccount", "")
    End Sub

    Public Function Resolve(Optional Id As String = Nothing) As GameAccount
        Dim All = Accounts()
        If String.IsNullOrEmpty(Id) Then Id = Settings.Get(Of String)("DefaultGameAccount")
        If String.IsNullOrEmpty(Id) Then Return Nothing
        Return All.FirstOrDefault(Function(Account) Account.Id = Id)
    End Function

    Public Sub SelectForServer(Id As String)
        PendingId = Id
    End Sub

    Public Sub PrepareLaunch()
        Dim Account = Resolve(PendingId)
        PendingId = Nothing
        If Account Is Nothing Then Throw New InvalidOperationException("请在设置 → Minecraft 账号中添加账号并设为默认，或为此服务器选择账号。")
        If McLoginLoader.State = LoadState.Loading OrElse McLoginMsLoader.State = LoadState.Loading OrElse McLoginAuthLoader.State = LoadState.Loading OrElse McLoginNideLoader.State = LoadState.Loading Then Throw New InvalidOperationException("正在登录账号，请等待完成后再启动。")
        Active = JsonConvert.DeserializeObject(Of GameAccount)(JsonConvert.SerializeObject(Account))
        Active.Input()
        Settings.Set("LoginType", Active.Type)
        If Active.Type = McLoginType.Ms AndAlso (Settings.Get(Of String)("CacheMsV2Name") <> Active.UserName OrElse Settings.Get(Of String)("CacheMsV2OAuthRefresh") <> Active.RefreshToken) Then Settings.Set("CacheMsV2Expires", 0L)
        For Each Token In {"Auth", "Nide"}
            For Each Suffix In {"Access", "Client", "Uuid", "Name", "Username", "Pass"}
                Settings.Set("Cache" & Token & Suffix, "")
            Next
        Next
    End Sub

    Public Function LoginInput() As McLoginData
        Dim Account = If(Active, Resolve())
        If Account Is Nothing Then Throw New InvalidOperationException("尚未配置默认 Minecraft 账号，请前往设置。")
        Return Account.Input()
    End Function

    Public Function AuthenticationServer() As String
        Return If(Active Is Nothing, "", Active.EffectiveAuthServer())
    End Function

    Public Function BoundAccount(ServerKey As String) As String
        If String.IsNullOrEmpty(ServerKey) Then Return ""
        Dim Bindings = JObject.Parse(Settings.Get(Of String)("ServerGameAccounts"))
        Return If(Bindings(ServerKey)?.ToString(), "")
    End Function

    Public Sub RefreshOfficialToken(Input As McLoginMs, Token As String, Name As String)
        Dim All = Accounts()
        Dim Account = All.FirstOrDefault(Function(Item) Item.Type = McLoginType.Ms AndAlso Item.UserName = Input.UserName AndAlso Item.RefreshToken = Input.OAuthRefreshToken)
        If Account Is Nothing Then Return
        Account.RefreshToken = Token
        Account.UserName = Name
        Persist(All)
    End Sub

    Public Sub RecordLogin(Account As GameAccount, Result As McLoginResult)
        If Account Is Nothing Then Return
        Account.DisplayName = Result.Name
        Account.Uuid = Result.Uuid
        Account.SkinPath = ""
        Dim All = Accounts()
        Dim Stored = All.FirstOrDefault(Function(Item) Item.Id = Account.Id)
        If Stored Is Nothing Then Return
        Stored.DisplayName = Result.Name
        Stored.Uuid = Result.Uuid
        Stored.SkinPath = ""
        Persist(All)
    End Sub

    Public Sub RecordActiveLogin(Result As McLoginResult)
        Try
            RecordLogin(Active, Result)
        Catch ex As Exception
            Logger.Warn("账号展示信息保存失败，游戏登录结果不受影响。")
        End Try
    End Sub

    Public Function AccountSkin(Account As GameAccount) As String
        If Account.Type = McLoginType.Legacy Then
            If Settings.Get(Of Integer)("LaunchSkinType") = 4 AndAlso File.Exists(Paths.AppDataThenName & "CustomSkin.png") Then Return Paths.AppDataThenName & "CustomSkin.png"
            Return PathImage & "Skins/" & If(Settings.Get(Of Integer)("LaunchSkinType") = 2, "Alex", "Steve") & ".png"
        End If
        If Account.SkinPath <> "" AndAlso File.Exists(Account.SkinPath) Then Return Account.SkinPath
        If Not Account.Uuid.RegexCheck("^[0-9a-fA-F]{32}$") Then Return PathImage & "Skins/Steve.png"
        Dim Endpoint = If(Account.Type = McLoginType.Ms, "https://sessionserver.mojang.com", If(Account.Type = McLoginType.Nide, "https://auth.mc-user.com:233/" & Account.AuthServer, Account.EffectiveAuthServer().TrimEnd("/"c)))
        Dim Profile = JObject.Parse(NetRequestByClientRetry(Endpoint & "/sessionserver/session/minecraft/profile/" & Account.Uuid, RequireJson:=True))
        Dim Texture = Profile("properties").FirstOrDefault(Function(Item) Item("name")?.ToString() = "textures")
        If Texture Is Nothing Then Return PathImage & "Skins/Steve.png"
        Dim Url = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(Texture("value").ToString())))("textures")?("SKIN")?("url")?.ToString()
        If String.IsNullOrEmpty(Url) Then Return PathImage & "Skins/Steve.png"
        Dim Address As New Uri(Url)
        If Address.Host = "textures.minecraft.net" AndAlso Address.Scheme = "http" Then Url = "https://" & Address.Host & Address.PathAndQuery
        If New Uri(Url).Scheme <> "https" Then Return PathImage & "Skins/Steve.png"
        Dim Skin = McSkinDownload(Url)
        SyncLock Gate
            Dim Stored = Accounts().FirstOrDefault(Function(Item) Item.Id = Account.Id AndAlso Item.Uuid = Account.Uuid)
            If Stored IsNot Nothing Then
                Stored.SkinPath = Skin
                Persist(Accounts())
            End If
        End SyncLock
        Return Skin
    End Function

    Public Sub OpenSettings(Optional ServerKey As String = Nothing)
        If McLaunchLoader.State = LoadState.Loading OrElse McLoginLoader.State = LoadState.Loading Then
            Hint("游戏正在登录或启动，请稍后修改账号。", HintType.Red)
            Return
        End If
        If FrmSetupAccounts Is Nothing Then FrmSetupAccounts = New PageSetupAccounts
        FrmSetupAccounts.SetServerContext(ServerKey)
        FrmMain.PageChange(FormMain.PageType.Setup, FormMain.PageSubType.SetupAccounts)
    End Sub
End Module
