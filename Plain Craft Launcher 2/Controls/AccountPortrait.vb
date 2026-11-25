Public Class AccountPortrait
    Inherits Grid

    Private ReadOnly MethodLabel As New TextBlock With {.HorizontalAlignment = HorizontalAlignment.Center, .FontSize = 14, .Margin = New Thickness(20, 24, 20, 0), .TextTrimming = TextTrimming.CharacterEllipsis}
    Private ReadOnly NameLabel As New TextBlock With {.HorizontalAlignment = HorizontalAlignment.Center, .FontSize = 14, .Margin = New Thickness(20, 14, 20, 0), .TextTrimming = TextTrimming.CharacterEllipsis}
    Private ReadOnly UserLabel As New TextBlock With {.HorizontalAlignment = HorizontalAlignment.Center, .FontSize = 12, .Margin = New Thickness(20, 4, 20, 0), .TextTrimming = TextTrimming.CharacterEllipsis}
    Private ReadOnly Face As New Image With {.Width = 48, .Height = 48, .Stretch = Stretch.Fill}
    Private ReadOnly Hat As New Image With {.Width = 54, .Height = 54, .Stretch = Stretch.Fill}
    Private Generation As Integer
    Private Signature As String

    Public Sub New()
        RowDefinitions.Add(New RowDefinition With {.Height = GridLength.Auto})
        RowDefinitions.Add(New RowDefinition)
        MethodLabel.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush3")
        UserLabel.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
        Dim MethodRow As New StackPanel With {.Orientation = Orientation.Horizontal, .HorizontalAlignment = HorizontalAlignment.Center, .Margin = New Thickness(20, 24, 20, 0)}
        Dim MethodIcon As New Shapes.Path With {.Data = Geometry.Parse(Logo.IconButtonCard), .Width = 16, .Height = 16, .Stretch = Stretch.Uniform, .VerticalAlignment = VerticalAlignment.Center, .Margin = New Thickness(0, 0, 12, 0)}
        MethodIcon.SetResourceReference(Shapes.Path.FillProperty, "ColorBrush3")
        MethodLabel.Margin = New Thickness(0)
        MethodRow.Children.Add(MethodIcon)
        MethodRow.Children.Add(MethodLabel)
        Children.Add(MethodRow)
        Dim Avatar As New Grid With {.Width = 60, .Height = 60}
        Avatar.Children.Add(Face)
        Avatar.Children.Add(Hat)
        RenderOptions.SetBitmapScalingMode(Avatar, BitmapScalingMode.NearestNeighbor)
        Dim Body As New StackPanel With {.VerticalAlignment = VerticalAlignment.Center, .Margin = New Thickness(0, 12, 0, 12)}
        Body.Children.Add(Avatar)
        Body.Children.Add(NameLabel)
        Body.Children.Add(UserLabel)
        Grid.SetRow(Body, 1)
        Children.Add(Body)
    End Sub

    Public Async Sub Bind(Account As GameAccount)
        Dim NextSignature = If(Account Is Nothing, "", Account.Id & ":" & Account.Uuid & ":" & Account.PlayerName & ":" & Account.UserName & ":" & Account.SkinPath & ":" & Account.LoginMethod) & ":" & Settings.Get(Of Boolean)("UiLauncherEmail").ToString()
        If Signature = NextSignature Then Return
        Signature = NextSignature
        Generation += 1
        Dim Current = Generation
        MethodLabel.Text = If(Account Is Nothing, "Minecraft 账号", Account.LoginMethod)
        NameLabel.Text = If(Account Is Nothing, "尚未设置默认账号", Account.PlayerName)
        UserLabel.Text = If(Account Is Nothing, "", Account.UserName)
        UserLabel.Visibility = If(Settings.Get(Of Boolean)("UiLauncherEmail"), Visibility.Collapsed, Visibility.Visible)
        ToolTip = If(Account Is Nothing, "", Account.Label)
        LoadSkin(PathImage & "Skins/Steve.png")
        If Account Is Nothing Then Return
        Try
            Dim Skin = Await Task.Run(Function() ModAccounts.AccountSkin(Account))
            If Current = Generation Then LoadSkin(Skin)
        Catch
            'Keep the default head when the skin service is unavailable.
        End Try
    End Sub

    Private Sub LoadSkin(Address As String)
        Dim Bitmap As New BitmapImage()
        Bitmap.BeginInit()
        Bitmap.CacheOption = BitmapCacheOption.OnLoad
        Bitmap.UriSource = New Uri(If(Address.StartsWith("pack://"), Address, System.IO.Path.GetFullPath(Address)), UriKind.Absolute)
        Bitmap.EndInit()
        Bitmap.Freeze()
        If Bitmap.PixelWidth < 64 OrElse Bitmap.PixelHeight < 32 Then Return
        Dim Scale = Bitmap.PixelWidth \ 64
        Face.Source = New CroppedBitmap(Bitmap, New Int32Rect(8 * Scale, 8 * Scale, 8 * Scale, 8 * Scale))
        Dim Image As New MyBitmap(Address)
        Dim HasOverlay = Image.Pic.GetPixel(1, 1).A = 0 OrElse Image.Pic.GetPixel(Image.Pic.Width - 1, Image.Pic.Height - 1).A = 0 OrElse
            (Image.Pic.GetPixel(1, 1) <> Image.Pic.GetPixel(41 * Scale, 9 * Scale) AndAlso Image.Pic.GetPixel(Image.Pic.Width - 1, Image.Pic.Height - 1) <> Image.Pic.GetPixel(41 * Scale, 9 * Scale))
        Hat.Source = If(HasOverlay, New CroppedBitmap(Bitmap, New Int32Rect(40 * Scale, 8 * Scale, 8 * Scale, 8 * Scale)), Nothing)
    End Sub
End Class
