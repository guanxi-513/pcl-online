Public Class PageServerSelectLeft
    Public Sub New()
        InitializeComponent()
        Dim RefreshButton As New MyIconButton With {.ToolTip = "刷新", .Logo = Logo.IconButtonRefresh, .LogoScale = 0.85}
        AddHandler RefreshButton.Click, AddressOf RefreshList
        ItemList.Buttons = {RefreshButton}
    End Sub

    Private Sub RefreshList(sender As Object, e As MouseButtonEventArgs)
        FrmServerSelectRight?.RefreshList()
    End Sub
End Class
