Public Class ServerVersionChoice
    Public Id As String
    Public Kind As String
    Public Data As Object

    Public Function CreateItem(OnClick As MyListItem.ClickEventHandler) As FrameworkElement
        Select Case Kind
            Case "minecraft"
                Return McDownloadListItem(DirectCast(Data, JObject), OnClick, True)
            Case "Forge"
                Return ForgeDownloadListItem(DirectCast(Data, DlForgeVersionEntry), OnClick, True)
            Case "NeoForge"
                Return NeoForgeDownloadListItem(DirectCast(Data, DlNeoForgeListEntry), OnClick, True)
            Case "Fabric"
                Return FabricDownloadListItem(DirectCast(Data, JObject), OnClick)
            Case Else
                Dim Item As New MyListItem With {.Title = Id, .Info = "Paper 稳定构建", .Height = 42, .Type = MyListItem.CheckType.Clickable, .Tag = Data, .Logo = PathImage & "Blocks/CommandBlock.png"}
                AddHandler Item.Click, OnClick
                Return Item
        End Select
    End Function

    Public ReadOnly Property Stable As Boolean
        Get
            Select Case Kind
                Case "minecraft"
                    Return DirectCast(Data, JObject)("type").ToString() = "release"
                Case "NeoForge"
                    Return Not DirectCast(Data, DlNeoForgeListEntry).IsBeta
                Case "Fabric"
                    Return DirectCast(Data, JObject)("stable").Value(Of Boolean)()
                Case Else
                    Return True
            End Select
        End Get
    End Property
End Class

Public Module ModServerCatalog
    Public Function MinecraftVersions(Force As Boolean) As List(Of ServerVersionChoice)
        DlClientListLoader.WaitForExitTime(120000, IsForceRestart:=Force)
        Return DlClientListLoader.Output.Value("versions").OfType(Of JObject)().
            Where(Function(Entry) Entry("url") IsNot Nothing).
            Select(Function(Entry) New ServerVersionChoice With {.Id = Entry("id").ToString(), .Kind = "minecraft", .Data = Entry.DeepClone()}).ToList()
    End Function

    Public Function LoaderVersions(Core As String, Minecraft As String, Force As Boolean) As List(Of ServerVersionChoice)
        Select Case Core
            Case "Forge"
                Dim Loader As New LoaderTask(Of String, List(Of DlForgeVersionEntry))("Server Forge " & Minecraft, AddressOf DlForgeVersionMain)
                Loader.WaitForExitTime(120000, Minecraft, IsForceRestart:=Force)
                Return Loader.Output.Where(Function(Entry) Entry.Category = "installer").OrderByDescending(Function(Entry) Entry).
                    Select(Function(Entry) New ServerVersionChoice With {.Id = Entry.FileVersion, .Kind = Core, .Data = Entry}).ToList()
            Case "NeoForge"
                Dim Prefix = If(Minecraft.StartsWith("1."), Minecraft.Substring(2), Minecraft)
                Dim Entries As New List(Of DlNeoForgeListEntry)
                Try
                    Dim Data As JObject = NetRequestByClientRetry("https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge?filter=" & Uri.EscapeDataString(Prefix & "."), RequireJson:=True).DeserializeJson()
                    For Each Version In Data("versions")
                        Dim Entry As New DlNeoForgeListEntry(Version.ToString())
                        If Entry.Inherit = Minecraft Then Entries.Add(Entry)
                    Next
                Catch ex As Exception
                    Logger.Info("NeoForge 官方列表暂不可用，尝试 PCL 镜像源。")
                End Try
                If Not Entries.Any() Then
                    Dim Mirror As New LoaderTask(Of Integer, DlNeoForgeListResult)("Server NeoForge mirror", AddressOf DlNeoForgeListBmclapiMain)
                    Mirror.WaitForExitTime(90000, 1, IsForceRestart:=Force)
                    Entries = Mirror.Output.Value.Where(Function(Entry) Entry.Inherit = Minecraft).ToList()
                End If
                Return Entries.OrderByDescending(Function(Entry) Entry).
                    Select(Function(Entry) New ServerVersionChoice With {.Id = Entry.ApiName, .Kind = Core, .Data = Entry}).ToList()
            Case "Fabric"
                DlFabricListLoader.WaitForExitTime(120000, 1, IsForceRestart:=Force)
                If Not DlFabricListLoader.Output.Value("game").Any(Function(Entry) Entry("version").ToString() = Minecraft) Then Return New List(Of ServerVersionChoice)
                Return DlFabricListLoader.Output.Value("loader").OfType(Of JObject)().
                    Select(Function(Entry) New ServerVersionChoice With {.Id = Entry("version").ToString(), .Kind = Core, .Data = Entry.DeepClone()}).ToList()
            Case "Paper"
                Dim Builds = NetRequestByClientRetry($"https://fill.papermc.io/v3/projects/paper/versions/{Uri.EscapeDataString(Minecraft)}/builds", RequireJson:=True).DeserializeJson(Of JArray)()
                Return Builds.OfType(Of JObject)().Where(Function(Entry) Entry("channel").ToString() = "STABLE").OrderByDescending(Function(Entry) Entry("id").Value(Of Integer)()).
                    Select(Function(Entry) New ServerVersionChoice With {.Id = Entry("id").ToString(), .Kind = Core, .Data = Entry}).ToList()
            Case Else
                Return New List(Of ServerVersionChoice)
        End Select
    End Function
End Module
