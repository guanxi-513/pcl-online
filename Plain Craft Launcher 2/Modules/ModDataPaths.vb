Public Module ModDataPaths
    Private ReadOnly Gate As New Object

    'Copy only missing files; upstream files and existing CPT choices stay untouched.
    Public Sub CopyMissingData(Source As String, Target As String)
        SyncLock Gate
            If Not Directory.Exists(Source) Then Return
            Dim SourceInfo As New DirectoryInfo(Source)
            If (SourceInfo.Attributes And FileAttributes.ReparsePoint) <> 0 Then Return
            If Directory.Exists(Target) AndAlso (New DirectoryInfo(Target).Attributes And FileAttributes.ReparsePoint) <> 0 Then Throw New IOException("CPT data target must not be a directory link.")
            Directory.CreateDirectory(Target)
            For Each Item In SourceInfo.GetFiles()
                If (Item.Attributes And FileAttributes.ReparsePoint) <> 0 Then Continue For
                Dim Destination = Path.Combine(Target, Item.Name)
                If File.Exists(Destination) Then Continue For
                Try
                    If Item.Name.Equals("Setup.ini", StringComparison.OrdinalIgnoreCase) Then
                        Dim Text = File.ReadAllText(Item.FullName).Replace("PCL\Logo.png", "CPT\Logo.png")
                        Using Output As New StreamWriter(New FileStream(Destination, FileMode.CreateNew), New UTF8Encoding(False))
                            Output.Write(Text)
                        End Using
                    Else
                        File.Copy(Item.FullName, Destination, False)
                    End If
                Catch ex As IOException
                    If Not File.Exists(Destination) Then Throw
                End Try
            Next
            For Each Folder In SourceInfo.GetDirectories()
                If (Folder.Attributes And FileAttributes.ReparsePoint) <> 0 Then Continue For
                CopyMissingData(Folder.FullName, Path.Combine(Target, Folder.Name))
            Next
        End SyncLock
    End Sub

    Public Sub MigrateLauncherData(Optional BaseDirectory As String = Nothing)
        Dim Base = If(BaseDirectory, Paths.Base)
        Dim Legacy = Path.Combine(Base, "PCL")
        Dim Target = Path.Combine(Base, "CPT")
        'accounts.dat identifies the previous CPT builds, not a stock PCL install.
        If File.Exists(Path.Combine(Target, ".migration-v1")) Then Return
        If File.Exists(Path.Combine(Legacy, "accounts.dat")) Then
            CopyMissingData(Legacy, Target)
            Dim OldSkin = Path.Combine(Paths.AppData, "PCL", "CustomSkin.png")
            Dim NewSkin = Path.Combine(Paths.AppData, "CPT", "CustomSkin.png")
            If BaseDirectory Is Nothing AndAlso File.Exists(OldSkin) AndAlso Not File.Exists(NewSkin) Then
                Directory.CreateDirectory(Path.GetDirectoryName(NewSkin))
                File.Copy(OldSkin, NewSkin, False)
            End If
        End If
        Directory.CreateDirectory(Target)
        File.WriteAllText(Path.Combine(Target, ".migration-v1"), "CPT data isolation v1", New UTF8Encoding(False))
    End Sub

    Public Sub MigrateInstanceData(VersionDirectory As String)
        SyncLock Gate
            Dim Target = Path.Combine(VersionDirectory, "CPT")
            Dim Marker = Path.Combine(VersionDirectory, ".cpt-settings-migrated-v1")
            If File.Exists(Marker) Then Return
            Dim Legacy = Path.Combine(VersionDirectory, "PCL")
            If Directory.Exists(Legacy) Then CopyMissingData(Legacy, Target)
            If Not Directory.Exists(Target) Then Return
            File.WriteAllText(Marker, "CPT instance snapshot v1", New UTF8Encoding(False))
        End SyncLock
    End Sub

    Public Sub ImportLegacyIni(Target As String)
        SyncLock Gate
            If File.Exists(Target) Then Return
            Dim Name = Path.GetFileName(Target)
            If Name.Equals("CPT.ini", StringComparison.OrdinalIgnoreCase) Then
                Dim Legacy = Path.Combine(Path.GetDirectoryName(Target), "PCL.ini")
                Dim Marker = Target & ".migration-v1"
                If File.Exists(Marker) Then Return
                If File.Exists(Legacy) Then
                    File.Copy(Legacy, Target, False)
                    File.WriteAllText(Marker, "CPT folder snapshot v1", New UTF8Encoding(False))
                End If
            ElseIf Name.Equals("Setup.ini", StringComparison.OrdinalIgnoreCase) AndAlso Path.GetFileName(Path.GetDirectoryName(Target)).Equals("CPT", StringComparison.OrdinalIgnoreCase) Then
                Dim Parent = Directory.GetParent(Path.GetDirectoryName(Target)).FullName
                'Never import the upstream launcher-wide configuration as CPT settings.
                If Not Path.GetFullPath(Parent).TrimEnd("\"c).Equals(Path.GetFullPath(Paths.Base).TrimEnd("\"c), StringComparison.OrdinalIgnoreCase) Then MigrateInstanceData(Parent)
            End If
        End SyncLock
    End Sub
End Module
