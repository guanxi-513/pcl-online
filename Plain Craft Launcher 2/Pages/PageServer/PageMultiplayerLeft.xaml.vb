Public Class PageMultiplayerLeft
    Public PageID As Integer
    Private Rooms As PageRooms
    Private Lobby As PageRooms

    Public Sub New()
        InitializeComponent()
        For Each Item As MyListItem In PanItem.Children
            Dim Entry = Item
            Dim Button As New MyIconButton With {.ToolTip = "刷新", .Logo = Logo.IconButtonRefresh, .LogoScale = 0.85, .Theme = MyIconButton.Themes.Black}
            AddHandler Button.Click, Sub()
                                         If PageID <> CInt(Entry.Tag) Then Return
                                         CType(PageGet(), IRefreshable).Refresh()
                                     End Sub
            Item.Buttons = {Button}
        Next
    End Sub

    Public Function PageGet() As MyPageRight
        PageID = If(PageID = 3, 1, If(PageID >= 0 AndAlso PageID <= 2, PageID, 0))
        ItemServers.SetChecked(PageID = 0, False, False)
        ItemRooms.SetChecked(PageID = 1, False, False)
        ItemLobby.SetChecked(PageID = 2, False, False)
        If PageID = 2 Then
            If Lobby Is Nothing Then
                Lobby = New PageRooms
                Lobby.SetLobbyMode()
            End If
            Return Lobby
        End If
        If PageID = 1 Then
            If Rooms Is Nothing Then Rooms = New PageRooms
            Return Rooms
        End If
        If FrmPlayServers Is Nothing Then FrmPlayServers = New PagePlayServers
        Return FrmPlayServers
    End Function

    Private Sub SelectPage(sender As Object, e As RouteEventArgs) Handles ItemServers.Check, ItemRooms.Check, ItemLobby.Check
        If AniControlEnabled <> 0 OrElse Not IsLoaded Then Return
        Dim Index = CInt(CType(sender, MyListItem).Tag)
        If Index = PageID Then Return
        FrmMain.PageChange(FormMain.PageType.Link, CType(Index, FormMain.PageSubType))
    End Sub
End Class
