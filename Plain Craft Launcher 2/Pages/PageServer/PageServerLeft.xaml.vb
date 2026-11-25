Public Class PageServerLeft
    Public PageID As Integer

    Public Sub New()
        InitializeComponent()
        For Each Item As MyListItem In PanItem.Children
            AddHandler Item.Check, AddressOf PageCheck
        Next
    End Sub

    Public Sub ConfigureMode(Deploying As Boolean)
        For Each Item As MyListItem In PanItem.Children
            Item.Visibility = If(Not Deploying OrElse CInt(Item.Tag) = 0, Visibility.Visible, Visibility.Collapsed)
        Next
        CType(PanItem.Children(0), MyListItem).Title = If(Deploying, "部署服务器", "连接设置")
    End Sub

    Private Sub PageCheck(sender As MyListItem, e As RouteEventArgs)
        If sender.Tag Is Nothing Then Return
        PageID = CInt(sender.Tag)
        If FrmServer IsNot Nothing Then FrmServer.SelectSection(PageID)
    End Sub
End Class
