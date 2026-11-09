Imports System.IO
Imports PCLCS.Link
Imports PCLCS.Link.Runtime

Public Class PageSetupLink

    Private Sub PageSetupLink_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        ' 重复加载部分
        PanBack.ScrollToHome()

        ' 非重复加载部分
        Static IsLoaded As Boolean = False
        If IsLoaded Then
            Reload()
            Return
        End If
        IsLoaded = True

        AniControlEnabled += 1
        Reload()
        AniControlEnabled -= 1
    End Sub

    Public Sub Reload()
        Try
            ' 1. 刷新标准控件数据绑定
            SettingService.RefreshSettings(Me)

            ' 2. 联动互斥处理
            UpdateControlInteractions()

            ' 3. 动态加载黑名单
            RefreshBlocklist()
        Catch ex As Exception
            Logger.Error(ex, "重载联机设置时出错")
        End Try
    End Sub

    Public Sub Reset()
        Try
            SettingService.ResetSettings(Me)
            Logger.Info("已初始化联机页设置")
            Hint("已初始化联机页设置！", HintType.Green, False)
        Catch ex As Exception
            Logger.Error(ex, "初始化联机页设置失败", LogBehavior.Alert)
        End Try
        Reload()
    End Sub

    ' 控件依赖联动（开启隐藏IP时中继必选）
    Private Sub UpdateControlInteractions()
        Dim isHideAddress As Boolean = Settings.Get(Of Boolean)("LinkHideAddress")
        If isHideAddress Then
            CheckAllowRelay.IsEnabled = False
            CheckAllowRelay.Checked = True
        Else
            CheckAllowRelay.IsEnabled = True
        End If
    End Sub

    Private Sub CheckHideAddress_Change(sender As Object, user As Boolean) Handles CheckHideAddress.Change
        If user Then UpdateControlInteractions()
    End Sub

    ' 动态渲染黑名单
    Private Sub RefreshBlocklist()
        PanBlockedList.Children.Clear()
        Dim blocked = Blocklist.All.OrderByDescending(Function(p) p.Time).ToList()

        If blocked.Count = 0 Then
            LabNoBlocked.Visibility = Visibility.Visible
            Return
        End If

        LabNoBlocked.Visibility = Visibility.Collapsed
        For Each player In blocked
            Dim row As New Grid() With {.Margin = New Thickness(0, 5, 0, 5)}
            row.ColumnDefinitions.Add(New ColumnDefinition() With {.Width = New GridLength(1, GridUnitType.Star)})
            row.ColumnDefinitions.Add(New ColumnDefinition() With {.Width = GridLength.Auto})

            Dim infoStack As New StackPanel() With {.VerticalAlignment = VerticalAlignment.Center}
            Dim nameBlock As New TextBlock() With {
                .Text = If(String.IsNullOrEmpty(player.GameName), player.Name, $"{player.Name} · {player.GameName}"),
                .FontWeight = FontWeights.Bold
            }
            Dim timeBlock As New TextBlock() With {
                .Text = $"拉黑时间：{player.Time:yyyy-MM-dd HH:mm} (设备码: {If(player.MachineId.Length >= 8, player.MachineId.Substring(0, 8) & "...", player.MachineId)})",
                .FontSize = 11,
                .Foreground = CType(FindResource("ColorBrushGray3"), Brush),
                .Margin = New Thickness(0, 2, 0, 0)
            }
            infoStack.Children.Add(nameBlock)
            infoStack.Children.Add(timeBlock)
            Grid.SetColumn(infoStack, 0)
            row.Children.Add(infoStack)

            Dim unblockBtn As New MyButton() With {
                .Text = "解除拉黑",
                .Padding = New Thickness(12, 4, 12, 4),
                .Tag = player.MachineId
            }
            AddHandler unblockBtn.Click, AddressOf BtnUnblock_Click
            Grid.SetColumn(unblockBtn, 1)
            row.Children.Add(unblockBtn)

            PanBlockedList.Children.Add(row)
        Next
    End Sub

    Private Sub BtnUnblock_Click(sender As Object, e As EventArgs)
        Dim btn = TryCast(sender, MyButton)
        If btn IsNot Nothing AndAlso btn.Tag IsNot Nothing Then
            Dim machineId = btn.Tag.ToString()
            Blocklist.Remove(machineId)
            Hint("已解除对该玩家的封禁", HintType.Green, False)
            RefreshBlocklist()
        End If
    End Sub

    ' 重置设备标识
    Private Sub BtnResetMachineId_Click(sender As Object, e As EventArgs) Handles BtnResetMachineId.Click
        LinkSettings.ResetMachineId()
        Hint("已成功生成全新的本机设备特征码！", HintType.Green, False)
    End Sub

    ' 打开数据存储目录
    Private Sub BtnOpenData_Click(sender As Object, e As EventArgs) Handles BtnOpenData.Click
        Try
            Directory.CreateDirectory(LinkSettings.DataDirectory)
            Process.Start("explorer.exe", LinkSettings.DataDirectory)
        Catch ex As Exception
            Logger.Error(ex, "打开联机数据目录失败")
        End Try
    End Sub

    ' 清理内核缓存（防御性检查：联机进行中禁止清理）
    Private Sub BtnCleanCache_Click(sender As Object, e As EventArgs) Handles BtnCleanCache.Click
        If PageLinkMain.LinkState <> PageLinkMain.LinkStates.Waiting Then
            Hint("当前有正在进行的联机房间，请先退出联机后再清理缓存！", HintType.Red, False)
            Return
        End If

        Try
            Dim easyTierDir = Path.GetDirectoryName(EasyTierPackage.InstallDirectory)
            Dim hasCache = EasyTierPackage.IsInstalled() OrElse
                           Directory.Exists(EasyTierPackage.InstallDirectory) OrElse
                           (easyTierDir IsNot Nothing AndAlso Directory.Exists(easyTierDir))

            If hasCache Then
                EasyTierPackage.Uninstall()
                Dim runtimeDir = Path.Combine(LinkSettings.DataDirectory, "runtime")
                If Directory.Exists(runtimeDir) Then
                    Try
                        Directory.Delete(runtimeDir, True)
                    Catch
                    End Try
                End If
                Hint("已清理 EasyTier 内核缓存！下次启动联机时将重新拉取。", HintType.Green, False)
            Else
                Hint("本地暂无内核缓存，无需清理。", HintType.Blue, False)
            End If
        Catch ex As Exception
            Hint("清理内核缓存失败：" & ex.Message, HintType.Red, False)
        End Try
    End Sub

End Class
