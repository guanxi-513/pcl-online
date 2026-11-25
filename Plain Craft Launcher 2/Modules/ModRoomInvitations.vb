Imports PCLCS.Server
Imports System.Windows.Threading

Public Module ModRoomInvitations
    Private Activation As RoomInvitationActivation
    Private Pending As String
    Private ReadOnly Gate As New Object
    Private Timer As DispatcherTimer

    Public Function StartupArgument(Arguments As String()) As String
        If Arguments.Length <> 1 OrElse Not Arguments(0).StartsWith("cpt:", StringComparison.OrdinalIgnoreCase) Then Return Nothing
        Try
            RoomInvitationActivation.Parse(Arguments(0), ProductWebsite)
            Return Arguments(0)
        Catch
            Return Nothing
        End Try
    End Function

    Public Sub Initialize(Value As String)
        Try
            Activation = New RoomInvitationActivation()
            Activation.Start(ProductWebsite, AddressOf Queue)
            If Value IsNot Nothing Then Queue(RoomInvitationActivation.Parse(Value, ProductWebsite))
        Catch
            Logger.Warn("启动房间邀请接收失败")
        End Try
        Try
            RoomInvitationActivation.Register(PathExe)
        Catch
            Logger.Warn("注册 CPT 网页唤醒协议失败")
        End Try
    End Sub

    Private Sub Queue(Link As String)
        SyncLock Gate
            Pending = Link
        End SyncLock
    End Sub

    Public Sub Start()
        Timer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(300)}
        AddHandler Timer.Tick, Sub()
                                  If FrmMain Is Nothing OrElse Not FrmMain.IsLoaded OrElse AniControlEnabled <> 0 OrElse IsProgramEnding OrElse Settings.Get(Of Integer)("SystemEulaVersion") < 2 Then Return
                                  If FrmMain.PanMsg.Children.Count > 0 OrElse WaitingMyMsgBox.Any() Then Return
                                  Dim Link As String
                                  SyncLock Gate
                                      Link = Pending
                                      Pending = Nothing
                                  End SyncLock
                                  If Link Is Nothing Then Return
                                  ShowWindowToTop(New System.Windows.Interop.WindowInteropHelper(FrmMain).Handle)
                                  FrmMain.PageChange(FormMain.PageType.Link, CType(1, FormMain.PageSubType))
                                  Dim Page = CType(FrmMultiplayerLeft.PageGet(), PageRooms)
                                  Page.ReceiveInvitation(Link)
                              End Sub
        Timer.Start()
    End Sub

    Public Sub StopReceiving()
        Timer?.Stop()
        Activation?.Dispose()
        SyncLock Gate
            Pending = Nothing
        End SyncLock
    End Sub
End Module
