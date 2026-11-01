Imports System.Collections.ObjectModel
Imports System.Net
Imports System.Threading
Imports System.Threading.Tasks
Imports PCL
Imports PCLCS.Link
Imports PCLCS.Link.Minecraft
Imports PCLCS.Link.Room
Imports PCLCS.Link.Runtime

Public Class PageLinkMain

    '===============================
    '  全局会话与状态机
    '===============================

#Region "状态管理"

    Public Enum LinkStates
        Waiting
        Loading
        Failed
        Finished
    End Enum

    Public Shared LinkState As LinkStates = LinkStates.Waiting
    Public Shared Session As LinkSession = Nothing

    ' 房主/访客角色标记
    Public Shared IsServerSide As Boolean = True
    Private Shared TargetJoinCode As RoomCode = Nothing
    Private Shared ManualHostPort As Integer? = Nothing

    ''' <summary>
    ''' 切换到指定的状态。
    ''' </summary>
    Private Sub ChangeState(NewState As LinkStates)
        If LinkState = NewState Then Return
        Dim OldState = LinkState
        LinkState = NewState
        Logger.Info($"联机主状态由 {OldState} 变更为 {NewState}")
        RunInUi(
        Sub()
            Select Case NewState
                Case LinkStates.Waiting
                    SwitchToWaiting(OldState)
                Case LinkStates.Loading
                    SwitchToLoading(OldState)
                Case LinkStates.Failed
                    SwitchToFailed(OldState)
                Case LinkStates.Finished
                    SwitchToFinished(OldState)
            End Select
        End Sub)
    End Sub

    ' 初始化加载器与事件绑定
    Private Sub LoaderInit() Handles Me.Initialized
        PageLoaderInit(Load, PanLoad, PanContent, PanAlways, LinkLoader, AutoRun:=False)
        AddHandler LinkLoader.OnStateChangedUi,
        Sub(Loader As LoaderBase, NewState As LoadState, OldState As LoadState)
            Select Case NewState
                Case LoadState.Finished
                    ChangeState(LinkStates.Finished)
                Case LoadState.Failed
                    ChangeState(LinkStates.Failed)
            End Select
        End Sub
    End Sub

    ' 页面进入动画兼容
    Private Sub UpdatePanelVisibility() Handles Me.PageEnter
        If FrmLinkMain Is Nothing Then Return
        FrmLinkMain.PanSelect.Visibility = If(LinkState = LinkStates.Waiting, Visibility.Visible, Visibility.Collapsed)
        FrmLinkMain.PanFinish.Visibility = If(LinkState = LinkStates.Finished, Visibility.Visible, Visibility.Collapsed)
    End Sub

#End Region

#Region "初始页面 (Waiting)"

    Private Sub SwitchToWaiting(OldState As LinkStates)
        LinkLoader.Cancel()
        SyncLock LinkLoader.LockState
            LinkLoader.State = LoadState.Waiting
        End SyncLock

        ProcessStop()

        If PanPlayerItems IsNot Nothing Then PanPlayerItems.Children.Clear()
        If CheckPremiumOnly IsNot Nothing Then CheckPremiumOnly.Checked = False
        If CheckLockRoom IsNot Nothing Then CheckLockRoom.Checked = False

        If OldState <> LinkStates.Finished Then
            UpdatePanelVisibility()
        End If
        PageOnContentExit()
    End Sub

    ' 创建房间点击
    Private Sub Create_MouseLeftButtonUp() Handles PanSelectCreate.MouseLeftButtonUp
        SyncSettingsToLinkCore()
        IsServerSide = True
        TargetJoinCode = Nothing
        ManualHostPort = Nothing

        ' 如果按住 Shift，允许手动输入端口，否则全自动侦测
        If Keyboard.IsKeyDown(Key.LeftShift) OrElse Keyboard.IsKeyDown(Key.RightShift) Then
            Dim PortInput As String = MyMsgBoxInput("手动指定端口", "已按住 Shift 触发手动指定端口模式。" & vbCrLf & "请输入要映射的游戏或本地服务端口：",
                ValidateRules:=New Collection(Of Validate) From {New ValidateInteger(1024, 65535)},
                HintText:="端口号 (1024-65535)")
            If PortInput Is Nothing Then Return
            ManualHostPort = Val(PortInput)
        End If

        Logger.Info($"开始创建联机房间，手动端口: {If(ManualHostPort.HasValue, ManualHostPort.Value.ToString(), "自动侦测")}")
        ChangeState(LinkStates.Loading)
    End Sub

    ' 加入房间点击
    Public Shared Sub Join() Handles PanSelectJoin.MouseLeftButtonUp
        SyncSettingsToLinkCore()
        Dim DefaultText As String = ""
        If Settings.Get(Of Boolean)("LinkDetectClipboard") Then
            Dim Clip = ClipboardGetText()
            If Clip IsNot Nothing Then
                Dim Found = RoomCode.Find(Clip)
                If Found IsNot Nothing Then DefaultText = Found.Code
            End If
        End If

        Dim CodeInput As String = MyMsgBoxInput("输入联机邀请码", "请输入房主分享给你的 16 位联机邀请码：",
            HintText:="U/XXXX-XXXX-XXXX-XXXX",
            DefaultInput:=DefaultText)
        If String.IsNullOrWhiteSpace(CodeInput) Then Return

        Join(CodeInput.Trim())
    End Sub

    Public Shared Sub Join(Code As String)
        If String.IsNullOrWhiteSpace(Code) Then Return
        Dim Found = RoomCode.Find(Code)
        If Found Is Nothing Then
            Hint("无效的联机邀请码，请检查是否完整复制！", HintType.Red)
            Return
        End If

        IsServerSide = False
        TargetJoinCode = Found
        ManualHostPort = Nothing

        If FrmLinkMain Is Nothing Then FrmLinkMain = New PageLinkMain()
        FrmMain.PageChange(FormMain.PageType.Link)
        FrmLinkMain.ChangeState(LinkStates.Loading)
    End Sub

    Public Shared Function ValidateCodeFormat(Code As String) As String
        If String.IsNullOrWhiteSpace(Code) Then Return "邀请码不能为空"
        Dim Found = RoomCode.Find(Code)
        If Found Is Nothing Then Return "无效的邀请码格式"
        Return Nothing
    End Function

    Public Shared Function FixCodeFormat(Code As String) As String
        Dim Found = RoomCode.Find(Code)
        Return If(Found IsNot Nothing, Found.Code, Code)
    End Function

#End Region

#Region "加载页面 (Loading | Failed)"

    Private Sub SwitchToLoading(OldState As LinkStates)
        FailReason = "准备初始化"
        UpdateProgressBar(0)
        LabLoadTitle.Text = If(IsServerSide, "正在创建联机房间", "正在加入联机房间")
        UpdateLoadingPage("正在初始化核心链路……", "准备初始化")

        LinkLoader.Start(IsForceRestart:=True)
    End Sub

    Private Sub SwitchToFailed(OldState As LinkStates)
        LinkLoader.Cancel()
        ProcessStop()

        UpdateProgressBar(1)
        LabLoadTitle.Text = FailReason
        Dim ErrorMsg As String = "未知错误"
        If LinkLoader.Error IsNot Nothing Then
            ErrorMsg = LinkLoader.Error.Message
            If TypeOf LinkLoader.Error Is LinkException Then
                ErrorMsg = CType(LinkLoader.Error, LinkException).LocalizedMessage
            End If
        End If
        LabLoadDesc.Text = ErrorMsg
        Logger.Warn(LinkLoader.Error, "联机加载失败：" & LabLoadTitle.Text)
    End Sub

    Private Sub UpdateLoadingPage(Title As String, FailBrief As String)
        FailReason = FailBrief & "失败"
        Logger.Info($"联机流程：{Title}")
        RunInUi(
        Sub()
            If FrmLinkMain IsNot Nothing AndAlso FrmLinkMain.LabLoadDesc IsNot Nothing Then
                FrmLinkMain.LabLoadDesc.Text = Title
            End If
        End Sub)
    End Sub

    Private FailReason As String = "准备初始化"

    Private Sub BtnLoadCancel_Click() Handles BtnLoadCancel.Click
        ChangeState(LinkStates.Waiting)
    End Sub

    Private Sub CardLoad_MouseLeftButtonUp() Handles CardLoad.MouseLeftButtonUp
        If LinkState = LinkStates.Failed Then
            ChangeState(LinkStates.Loading)
        End If
    End Sub

    Private Sub UpdateProgressBar(Optional Value As Double = -1)
        If Value = -1 Then Value = LinkLoader.Progress
        Dim DisplayingProgress As Double = ColumnProgressA.Width.Value
        If Math.Round(Value - DisplayingProgress, 3) = 0 Then Return
        If DisplayingProgress <= Value Then
            Dim NewProgress As Double = If(Value = 1, 1, DisplayingProgress + (Value - DisplayingProgress) ^ 2 * 0.5)
            AniStart({
                AaGridLengthWidth(ColumnProgressA, NewProgress - ColumnProgressA.Width.Value, 200),
                AaGridLengthWidth(ColumnProgressB, (1 - NewProgress) - ColumnProgressB.Width.Value, 200)
            }, "Link Progress")
        Else
            ColumnProgressA.Width = New GridLength(Value, GridUnitType.Star)
            ColumnProgressB.Width = New GridLength(1 - Value, GridUnitType.Star)
            AniStop("Link Progress")
        End If
    End Sub

    Private Sub CardLoad_SizeChanged() Handles CardLoad.SizeChanged
        RectProgressClip.Rect = New Rect(0, 0, CardLoad.ActualWidth, 12)
    End Sub

#End Region

#Region "核心加载管道 (LinkLoader)"

    Private WithEvents LinkLoader As New LoaderCombo(Of Integer)("联机加载器", {
        New LoaderTask(Of Integer, Integer)("准备联机组件", AddressOf StepPrepareComponent) With {.ProgressWeight = 35},
        New LoaderTask(Of Integer, Integer)("建立网络通道", AddressOf StepEstablishNetwork) With {.ProgressWeight = 65}
    })

    ' 1. 准备/下载 EasyTier 核心组件
    Private Sub StepPrepareComponent(Task As LoaderTask(Of Integer, Integer))
        UpdateLoadingPage("正在检查联机网络组件……", "检查组件")
        SyncSettingsToLinkCore()

        If Not EasyTierPackage.IsInstalled() Then
            UpdateLoadingPage("正在下载联机核心组件（多源测速加速中）……", "下载组件")
            Dim progressReporter = New Progress(Of DownloadProgress)(
                Sub(p)
                    Task.Progress = Math.Min(0.99, p.Fraction)
                    RunInUi(Sub() UpdateProgressBar(Task.Progress * 0.35))
                End Sub)

            EasyTierPackage.InstallAsync(progressReporter, CancellationToken.None).GetAwaiter().GetResult()
        End If
        Task.Progress = 1.0
    End Sub

    ' 2. 建立网络服务（房主自动捕获端口/访客打洞与广播）
    Private Sub StepEstablishNetwork(Task As LoaderTask(Of Integer, Integer))
        SyncSettingsToLinkCore()

        Session = New LinkSession(SynchronizationContext.Current)
        AddHandler Session.Changed, AddressOf OnSessionChanged

        If IsServerSide Then
            If Not ManualHostPort.HasValue Then
                UpdateLoadingPage("正在等待单人游戏【对局域网开放】……", "侦测游戏端口")
            Else
                UpdateLoadingPage($"正在绑定指定端口 {ManualHostPort.Value}……", "绑定端口")
            End If

            Session.StartHost(ManualHostPort)
        Else
            UpdateLoadingPage($"正在连接房间 {TargetJoinCode.Code.Substring(0, 10)}……", "加入房间")
            Session.StartJoin(TargetJoinCode)
        End If

        ' 等待会话就绪或失败
        Dim TimeoutCounter As Integer = 0
        While Session.Stage = LinkStage.Working AndAlso Task.State <> LoadState.Canceled
            Thread.Sleep(500)
            TimeoutCounter += 1
            Task.Progress = Math.Min(0.95, 0.1 + (TimeoutCounter * 0.03))
            RunInUi(Sub() UpdateProgressBar(0.35 + Task.Progress * 0.65))
        End While

        If Session.Stage = LinkStage.Failed Then
            Dim ex = Session.Error
            If ex IsNot Nothing Then Throw ex
            Throw New Exception("建立联机房间失败")
        End If

        If Session.Stage <> LinkStage.Ready Then
            Throw New OperationCanceledException("联机操作已被取消")
        End If

        Task.Progress = 1.0
    End Sub

    Public Shared Sub SyncSettingsToLinkCore()
        ' 同步 PCL2 的设置到 LinkSettings
        Try
            ' 1. 网络与节点
            Dim allowRelay As Boolean = Settings.Get(Of Boolean)("LinkAllowRelay")
            LinkSettings.DirectOnly = Not allowRelay
            LinkSettings.HideAddress = Settings.Get(Of Boolean)("LinkHideAddress")
            LinkSettings.ShareWithLan = Settings.Get(Of Boolean)("LinkShareWithLan")
            Dim customNodes As String = Settings.Get(Of String)("LinkCustomNodes")
            LinkSettings.CustomPeers = If(customNodes, "").Trim()

            ' 2. 房间与玩家
            LinkSettings.PlayerName = Settings.Get(Of String)("LinkPlayerName")
            LinkSettings.LanEntryName = Settings.Get(Of String)("LinkLanName")
            LinkSettings.LocalPort = Settings.Get(Of Integer)("LinkLocalPort")
            LinkSettings.AutoOpenLan = Settings.Get(Of Boolean)("LinkAutoOpenLan")

            ' 3. 模组同步
            LinkSettings.ShareMods = Settings.Get(Of Boolean)("LinkShareMods")
            LinkSettings.ServeMods = Settings.Get(Of Boolean)("LinkServeMods")

            ' 4. 隐私安全
            LinkSettings.ReadGameAccount = Settings.Get(Of Boolean)("LinkReadGameAccount")
            LinkSettings.ShowSkins = Settings.Get(Of Boolean)("LinkShowSkins")

            ' 5. 同步启动器当前登录账号的正版身份与 UUID
            Dim loginType = Settings.Get(Of McLoginType)("LoginType")
            If loginType = McLoginType.Ms Then
                Dim msName As String = Settings.Get(Of String)("CacheMsV2Name")
                Dim msUuid As String = Settings.Get(Of String)("CacheMsV2Uuid")
                If String.IsNullOrEmpty(msName) AndAlso McLoginLoader IsNot Nothing AndAlso McLoginLoader.State = LoadState.Finished Then
                    msName = McLoginLoader.Output.Name
                    msUuid = McLoginLoader.Output.Uuid
                End If
                If Not String.IsNullOrEmpty(msName) Then
                    LinkSettings.LauncherAccountName = msName
                    LinkSettings.LauncherAccountUuid = If(msUuid, "").Replace("-", "").ToLowerInvariant()
                    LinkSettings.LauncherAccountIsPremium = True
                    Logger.Info($"已同步启动器微软正版凭据: {msName} ({LinkSettings.LauncherAccountUuid})")
                Else
                    LinkSettings.LauncherAccountName = ""
                    LinkSettings.LauncherAccountUuid = Nothing
                    LinkSettings.LauncherAccountIsPremium = False
                End If
            Else
                Dim legacyName = Settings.Get(Of String)("LoginLegacyName")
                Dim name = If(legacyName IsNot Nothing, legacyName.ToString().BeforeFirst("¨"), "")
                LinkSettings.LauncherAccountName = name
                LinkSettings.LauncherAccountUuid = Nothing
                LinkSettings.LauncherAccountIsPremium = False
            End If
        Catch ex As Exception
            Logger.Warn(ex, "同步联机设置失败")
        End Try
    End Sub

    Private Sub OnSessionChanged(sender As Object, e As EventArgs)
        RunInUi(AddressOf Update)
    End Sub

#End Region

#Region "完成页面 (Finished)"

    Private Sub SwitchToFinished(OldState As LinkStates)
        UpdatePanelVisibility()

        ' 房主控制栏与玩家列表初始化
        PanHostControls.Visibility = If(IsServerSide, Visibility.Visible, Visibility.Collapsed)
        CheckPremiumOnly.Checked = False
        CheckLockRoom.Checked = False
        If Session IsNot Nothing AndAlso IsServerSide Then
            Session.PremiumOnly = False
            Session.IsLocked = False
        End If
        CardPlayerList.Visibility = Visibility.Visible

        If IsServerSide Then
            LabFinishTitle.Text = "房间已就绪"
            LabFinishDesc.Text = $"邀请码：{Session?.Code?.Code}{vbCrLf}把邀请码发给好友，即可通过 PCL2 直接加入！"
            BtnFinishExit.Text = "关闭房间"
            BtnFinishPing.ToolTip = "本地监听"
            BtnFinishCopy.Visibility = Visibility.Visible
            BtnFinishCopy.Text = "复制邀请码"

            BtnFinishPort.Visibility = Visibility.Visible
            LabFinishPort.Visibility = Visibility.Visible
            BtnFinishIp.Visibility = Visibility.Collapsed
            LabFinishIp.Visibility = Visibility.Collapsed
            LabFinishPort.Text = If(Session IsNot Nothing, Session.MinecraftPort.ToString(), "-")

            If Settings.Get(Of Boolean)("LinkAutoCopyCode") Then
                CopyInviteCode()
            End If
        Else
            LabFinishTitle.Text = "已加入房间"
            LabFinishDesc.Text = $"局域网隧道已建立！{vbCrLf}请在 Minecraft【多人游戏】列表底部，直接点击局域网世界加入！"
            BtnFinishExit.Text = "离开房间"
            BtnFinishPing.ToolTip = "与房主的延迟"
            BtnFinishCopy.Visibility = Visibility.Collapsed

            BtnFinishPort.Visibility = Visibility.Collapsed
            LabFinishPort.Visibility = Visibility.Collapsed
            BtnFinishIp.Visibility = Visibility.Visible
            LabFinishIp.Visibility = Visibility.Visible
            LabFinishIp.Text = If(Session IsNot Nothing, $"127.0.0.1:{Session.LocalPort}", "-")
        End If

        Update()
    End Sub

    ' 房主选项变动（仅正版 / 锁定房间）
    Private Sub CheckHostOptions_Change(sender As Object, user As Boolean) Handles CheckPremiumOnly.Change, CheckLockRoom.Change
        If user AndAlso Session IsNot Nothing AndAlso IsServerSide Then
            Session.PremiumOnly = CheckPremiumOnly.Checked
            Session.IsLocked = CheckLockRoom.Checked
            If sender Is CheckLockRoom Then
                Hint(If(CheckLockRoom.Checked, "房间已锁定，禁止新玩家加入", "房间已解锁，允许新玩家加入"), HintType.Blue)
            ElseIf sender Is CheckPremiumOnly Then
                Hint(If(CheckPremiumOnly.Checked, "已开启仅正版验证模式", "已关闭仅正版验证模式"), HintType.Blue)
            End If
        End If
    End Sub

    ' 点击人数按钮展开/折叠玩家列表
    Private Sub BtnFinishPlayer_Click(sender As Object, e As MouseButtonEventArgs) Handles BtnFinishPlayer.MouseLeftButtonUp
        If CardPlayerList IsNot Nothing Then
            CardPlayerList.Visibility = If(CardPlayerList.Visibility = Visibility.Visible, Visibility.Collapsed, Visibility.Visible)
        End If
    End Sub

    Private Sub BtnFinishExit_Click() Handles BtnFinishExit.Click
        TryExit(False, False)
    End Sub

    Public Function TryExit(Silent As Boolean, Closing As Boolean) As Boolean
        If LinkState = LinkStates.Waiting OrElse LinkState = LinkStates.Failed Then Return False
        If Not Silent Then
            Dim Msg = If(IsServerSide, "确定要关闭联机房间吗？所有访客都将断开连接！", "确定要离开联机房间吗？")
            If MyMsgBox(Msg, "退出联机", "确定", "取消", IsWarn:=IsServerSide) = 2 Then Return True
        End If
        ChangeState(LinkStates.Waiting)
        Return False
    End Function

    Private Sub BtnFinishCopy_Click() Handles BtnFinishCopy.Click
        CopyInviteCode()
    End Sub

    Private Sub CopyInviteCode()
        If Session?.Code Is Nothing Then Return
        Dim TextToCopy = $"【Minecraft 联机邀请】我在使用 PCL 启动器联机，房间邀请码：{Session.Code.Code} （复制此消息打开 PCL 即可一键加入）"
        ClipboardSet(TextToCopy, False)
        Settings.Set("LinkLastAutoJoinInviteCode", Session.Code.Code)
        Hint("已复制邀请码到剪贴板！发给好友即可加入！", HintType.Green)
    End Sub

    Private Sub BtnFinishIp_MouseLeftButtonUp(sender As Object, e As MouseButtonEventArgs) Handles BtnFinishIp.MouseLeftButtonUp
        If Session Is Nothing Then Return
        Dim Addr = $"127.0.0.1:{Session.LocalPort}"
        ClipboardSet(Addr, False)
        Hint($"已复制局域网直连地址：{Addr}", HintType.Green)
    End Sub

    ' 实时刷新完成页各项指标与成员列表
    Private Sub Update() Handles Me.Loaded
        If LinkState <> LinkStates.Finished OrElse Session Is Nothing Then Return

        ' 1. 延迟
        If IsServerSide Then
            LabFinishPing.Text = $"{Session.ConnectedNodes} 节点"
            BtnFinishPing.ToolTip = $"已连接公共中继节点：{Session.ConnectedNodes} 个"
        Else
            If Session.HostPeer IsNot Nothing AndAlso Session.HostPeer.LatencyMs.HasValue Then
                LabFinishPing.Text = $"{Session.HostPeer.LatencyMs.Value:0} ms"
            Else
                LabFinishPing.Text = "连接中"
            End If
            BtnFinishPing.ToolTip = If(Session.HostPeer?.IsDirect = True, "与房主 P2P 直连", "通过中继节点转发")
        End If

        ' 2. 人数与列表
        Dim count = If(Session.Players IsNot Nothing, Session.Players.Count, 0)
        LabFinishPlayer.Text = $"{Math.Max(1, count)} 人"
        LabPlayerListCount.Text = $"{count} 人在线"
        RefreshPlayerList()

        ' 3. 提示卡片 (HintFinish)
        If IsServerSide Then
            If Session.DirectPeers > 0 Then
                HintFinish.Text = $"已与 {Session.DirectPeers} 位访客建立 P2P 直连通道，延迟极低。"
                HintFinish.Theme = MyHint.Themes.Blue
            ElseIf Session.RelayedPeers > 0 Then
                HintFinish.Text = "当前有访客通过中继节点传输，可在设置中切换或添加优质节点。"
                HintFinish.Theme = MyHint.Themes.Yellow
            Else
                HintFinish.Text = "房间已准备就绪，正在等待好友输入邀请码加入……"
                HintFinish.Theme = MyHint.Themes.Blue
            End If
        Else
            If Session.HostPeer?.IsDirect = True Then
                HintFinish.Text = "已与房主成功建立 P2P 直连通道，延迟接近局域网！"
                HintFinish.Theme = MyHint.Themes.Blue
            Else
                HintFinish.Text = "双方网络受限未能直连，当前正在通过公共中继节点进行加密转发。"
                HintFinish.Theme = MyHint.Themes.Yellow
            End If
        End If
    End Sub

    ''' <summary>
    ''' 渲染房间玩家列表（支持权限视差、离线灰色标识、右键临时移出与拉黑）。
    ''' </summary>
    Private Sub RefreshPlayerList()
        If PanPlayerItems Is Nothing Then Return
        PanPlayerItems.Children.Clear()

        If Session Is Nothing OrElse Session.Players Is Nothing OrElse Session.Players.Count = 0 Then
            Dim emptyLab As New TextBlock With {
                .Text = "暂无其他玩家在线",
                .FontSize = 12,
                .HorizontalAlignment = HorizontalAlignment.Center,
                .Margin = New Thickness(0, 10, 0, 10)
            }
            emptyLab.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
            PanPlayerItems.Children.Add(emptyLab)
            Return
        End If

        For Each player In Session.Players
            Dim targetPlayer = player
            Dim row As New Grid With {
                .Margin = New Thickness(0, 3, 0, 3),
                .Background = Brushes.Transparent,
                .Height = 28
            }
            row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})
            row.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})

            Dim nameStack As New StackPanel With {
                .Orientation = Orientation.Horizontal,
                .VerticalAlignment = VerticalAlignment.Center
            }

            ' 状态圆点
            Dim dot As New Border With {
                .Width = 7,
                .Height = 7,
                .CornerRadius = New CornerRadius(3.5),
                .Margin = New Thickness(2, 0, 8, 0),
                .VerticalAlignment = VerticalAlignment.Center
            }
            If player.Kind = PlayerKind.Host Then
                dot.SetResourceReference(Border.BackgroundProperty, "ColorBrush3")
            Else
                dot.Background = New SolidColorBrush(Color.FromRgb(&H4C, &HAF, &H50))
            End If
            nameStack.Children.Add(dot)

            ' 玩家名称展示（权限视差）
            ' 房主视角：显示 昵称 [游戏内玩家名]（若有游戏内玩家名）
            ' 普通玩家视角：仅显示 昵称
            Dim labName As New TextBlock With {
                .FontSize = 13,
                .VerticalAlignment = VerticalAlignment.Center
            }
            labName.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")

            If IsServerSide AndAlso Not String.IsNullOrWhiteSpace(player.GameName) AndAlso player.GameName <> player.Name Then
                labName.Text = $"{player.Name} [{player.GameName}]"
            Else
                labName.Text = player.Name
            End If
            nameStack.Children.Add(labName)

            ' 角色标签（房主 / 我）
            If player.Kind = PlayerKind.Host Then
                Dim badgeHost As New Border With {
                    .CornerRadius = New CornerRadius(3),
                    .Margin = New Thickness(6, 0, 0, 0),
                    .Padding = New Thickness(4, 1, 4, 1),
                    .VerticalAlignment = VerticalAlignment.Center
                }
                badgeHost.SetResourceReference(Border.BackgroundProperty, "ColorBrush7")
                Dim txtHost As New TextBlock With {
                    .Text = "房主",
                    .FontSize = 10.5,
                    .FontWeight = FontWeights.Medium
                }
                txtHost.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush2")
                badgeHost.Child = txtHost
                nameStack.Children.Add(badgeHost)
            ElseIf player.Kind = PlayerKind.Local Then
                Dim badgeMe As New Border With {
                    .CornerRadius = New CornerRadius(3),
                    .Margin = New Thickness(6, 0, 0, 0),
                    .Padding = New Thickness(4, 1, 4, 1),
                    .VerticalAlignment = VerticalAlignment.Center
                }
                badgeMe.SetResourceReference(Border.BackgroundProperty, "ColorBrush7")
                Dim txtMe As New TextBlock With {
                    .Text = "我",
                    .FontSize = 10.5
                }
                txtMe.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
                badgeMe.Child = txtMe
                nameStack.Children.Add(badgeMe)
            End If

            ' 离线灰色标识（核心需求：仅房主可见！）
            ' 若玩家是离线模式加入（无有效正版 UUID），在后方添加一个离线的灰色标识。
            If IsServerSide AndAlso String.IsNullOrEmpty(player.Uuid) Then
                Dim badgeOffline As New Border With {
                    .CornerRadius = New CornerRadius(3),
                    .Margin = New Thickness(6, 0, 0, 0),
                    .Padding = New Thickness(4, 1, 4, 1),
                    .VerticalAlignment = VerticalAlignment.Center,
                    .Background = New SolidColorBrush(Color.FromArgb(&H20, &H88, &H88, &H88))
                }
                Dim txtOffline As New TextBlock With {
                    .Text = "离线",
                    .FontSize = 10.5,
                    .Foreground = New SolidColorBrush(Color.FromRgb(&H88, &H88, &H88))
                }
                badgeOffline.Child = txtOffline
                nameStack.Children.Add(badgeOffline)
            End If

            Grid.SetColumn(nameStack, 0)
            row.Children.Add(nameStack)

            ' 延迟信息
            Dim labPing As New TextBlock With {
                .FontSize = 11,
                .VerticalAlignment = VerticalAlignment.Center,
                .HorizontalAlignment = HorizontalAlignment.Right,
                .Margin = New Thickness(0, 0, 4, 0)
            }
            labPing.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
            If player.LatencyMs.HasValue Then
                labPing.Text = $"{player.LatencyMs.Value:0} ms"
            ElseIf player.Kind = PlayerKind.Host Then
                labPing.Text = If(IsServerSide, "本地", "房主")
            Else
                labPing.Text = "-"
            End If
            Grid.SetColumn(labPing, 1)
            row.Children.Add(labPing)

            ' 详细悬停 ToolTip
            If IsServerSide Then
                Dim tip = "昵称：" & player.Name
                If Not String.IsNullOrEmpty(player.GameName) Then tip &= vbCrLf & "游戏内名称：" & player.GameName
                tip &= vbCrLf & "账号验证：" & If(String.IsNullOrEmpty(player.Uuid), "离线模式（无正版 UUID）", "微软正版 (" & player.Uuid & ")")
                tip &= vbCrLf & "设备特征码：" & player.MachineId
                If player.LatencyMs.HasValue Then tip &= vbCrLf & "延迟：" & player.LatencyMs.Value.ToString("0") & " ms"
                If player.Kind <> PlayerKind.Host Then
                    tip &= vbCrLf & "（右键弹出菜单可移出或拉黑玩家）"
                End If
                row.ToolTip = tip
            Else
                Dim tip = "昵称：" & player.Name
                If player.Kind = PlayerKind.Host Then tip &= vbCrLf & "角色：房主"
                row.ToolTip = tip
            End If

            ' 仅房主有权对访客挂载右键操作菜单（移出房间 / 永久拉黑）
            If IsServerSide AndAlso player.Kind <> PlayerKind.Host Then
                Dim menu As New ContextMenu()

                ' 移出房间 (1分钟)
                Dim itemKick As New MyMenuItem With {
                    .Header = "移出房间 (1分钟)",
                    .Icon = "F1 M 23.3428,25.205L 23.3805,25.4461C 23.9229,27.177 30.261,29.0992 38,29.0992C 45.7386,29.0992 52.0765,27.1771 52.6194,25.4463L 52.6571,25.205C 52.6571,23.3616 46.0949,21.3109 38,21.3109C 29.9051,21.3109 23.3428,23.3616 23.3428,25.205 Z M 23.3428,53.0204L 19.1571,26.2111C 19.0534,25.8817 19,25.5459 19,25.205C 19,20.9036 27.5066,17.4167 38,17.4167C 48.4934,17.4167 57,20.9036 57,25.205C 57,25.5459 56.9466,25.8818 56.8429,26.2112L 52.6571,53.0204L 52.5974,53.0204C 51.9241,56.1393 45.6457,58.5833 38,58.5833C 30.3543,58.5833 24.076,56.1393 23.4026,53.0204L 23.3428,53.0204 Z"
                }
                AddHandler itemKick.Click, Sub()
                    If Session IsNot Nothing Then
                        Session.RemovePlayer(targetPlayer, block:=False)
                        Hint("已将玩家 " & targetPlayer.Name & " 移出房间（1 分钟内禁止重连）", HintType.Green)
                    End If
                End Sub

                ' 永久拉黑玩家（带二次确认）
                Dim itemBlock As New MyMenuItem With {
                    .Header = "永久拉黑玩家",
                    .Icon = "F1 M 26.9166,22.1667L 37.9999,33.25L 49.0832,22.1668L 53.8332,26.9168L 42.7499,38L 53.8332,49.0834L 49.0833,53.8334L 37.9999,42.75L 26.9166,53.8334L 22.1666,49.0833L 33.25,38L 22.1667,26.9167L 26.9166,22.1667 Z "
                }
                AddHandler itemBlock.Click, Sub()
                    If Session IsNot Nothing Then
                        Dim confirmMsg = "确定要永久拉黑玩家“" & targetPlayer.Name & "”吗？" & vbCrLf & "该玩家的设备标识将被永久拦截，无法再次加入该房间。"
                        If MyMsgBox(confirmMsg, "拉黑玩家", "确定拉黑", "取消", IsWarn:=True) = 1 Then
                            Session.RemovePlayer(targetPlayer, block:=True)
                            Hint("已永久拉黑玩家 " & targetPlayer.Name, HintType.Green)
                        End If
                    End If
                End Sub

                menu.Items.Add(itemKick)
                menu.Items.Add(itemBlock)
                row.ContextMenu = menu
            End If

            PanPlayerItems.Children.Add(row)
        Next
    End Sub

#End Region

#Region "进程与生命周期回收"

    Public Shared Sub ProcessStop()
        Try
            If Session IsNot Nothing Then
                Session.Stop()
                Session.Shutdown()
                Session = Nothing
            End If
        Catch ex As Exception
            Logger.Warn(ex, "停止联机会话异常")
        End Try
    End Sub

#End Region

End Class
