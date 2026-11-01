Public Class PageOtherAbout

    Private Shadows IsLoaded As Boolean = False
    Private Sub PageOtherAbout_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded

        '重复加载部分
        PanBack.ScrollToHome()

        '非重复加载部分
        If IsLoaded Then Return
        IsLoaded = True

        ItemAboutPcl.Info = "当前版本：正式版 1.0.0.0"

    End Sub

    Public Shared Sub CopyIdentify()
        ClipboardSet(Identify)
    End Sub

End Class
