Imports System.Collections.ObjectModel
Imports System.Net
Imports System.Threading
Imports System.Threading.Tasks
Imports MeloongCore
Imports PCL
Imports PCLCS.Link
Imports PCLCS.Link.Contracts
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

    ' UI 刷新节流器（合并 200ms 内的密集网络事件，大幅降低主线程 Dispatcher 调度开销）
    Private ReadOnly _throttledUpdate As New RateLimitedWorker(Sub() RunInUi(AddressOf Update), 200, RateLimitMode.ImmediateThenMerge)

    ' 玩家行视觉缓存模型，用于增量对比渲染（List Diffing）
    Private Class PlayerRowHolder
        Public RowGrid As Grid
        Public LabPing As TextBlock
        Public BadgeRole As Border
        Public TxtRole As TextBlock
        Public TargetPlayer As PlayerProfile
        Public CurrentDto As LinkPlayerViewDto
    End Class

    Private Shared ReadOnly _playerRowHolders As New Dictionary(Of String, PlayerRowHolder)

    ' 版本审核与智能自愈状态
    Public Enum VersionCheckState
        Unknown
        Match
        LocalFound
        Missing
    End Enum

    Public Shared CurrentVersionState As VersionCheckState = VersionCheckState.Unknown
    Public Shared MatchedLocalInstance As ModMinecraft.McInstance = Nothing

    Private Const IconVersionMatch As String = "M512 64C264.6 64 64 264.6 64 512s200.6 448 448 448 448-200.6 448-448S759.4 64 512 64z m193.5 301.7l-210.6 292a31.8 31.8 0 0 1-25.7 13.3c-0.3 0-0.7 0-1 0a31.8 31.8 0 0 1-25.6-13.6l-105-148c-10-14.1-6.8-33.6 7.3-43.6 14.1-10 33.6-6.8 43.6 7.3l80.2 113.1 186.1-258.1c10.3-14.3 30.3-17.3 44.6-7 14.3 10.3 17.3 30.3 7 44.6z"
    Private Const IconVersionSwitch As String = "M512 64C264.6 64 64 264.6 64 512s200.6 448 448 448 448-200.6 448-448S759.4 64 512 64zm192 480H544v160l-192-208 192-208v160h160v96z"
    Private Const IconVersionDownload As String = "M512 64a448 448 0 1 0 448 448A448 448 0 0 0 512 64z m200 488H552v-216a40 40 0 0 0-80 0v216H312a20 20 0 0 0-15 33.3l199 220a20 20 0 0 0 29.8 0l199-220A20 20 0 0 0 712 552z"

    Public Shared Function GetLoaderString(inst As ModMinecraft.McInstance) As String
        If inst Is Nothing OrElse inst.Version Is Nothing Then Return "Vanilla"
        Dim loaders = inst.Version.ModLoaders
        If loaders.HasFlag(PCLCS.ModLoaders.Fabric) Then Return "Fabric"
        If loaders.HasFlag(PCLCS.ModLoaders.NeoForge) Then Return "NeoForge"
        If loaders.HasFlag(PCLCS.ModLoaders.Forge) Then Return "Forge"
        If loaders.HasFlag(PCLCS.ModLoaders.LiteLoader) Then Return "LiteLoader"
        Return "Vanilla"
    End Function

    Public Shared Function FindMatchingLocalInstance(targetVersion As GameVersionInfo) As ModMinecraft.McInstance
        If targetVersion Is Nothing OrElse targetVersion.McVersion = "Unknown" Then Return Nothing
        If ModMinecraft.McInstanceList Is Nothing Then Return Nothing
        Try
            For Each pair In ModMinecraft.McInstanceList.ToArray()
                If pair.Value Is Nothing Then Continue For
                For Each inst In pair.Value.ToArray()
                    If inst IsNot Nothing AndAlso inst.State <> ModMinecraft.McInstance.McInstanceState.Error Then
                        Dim instMcVer = inst.Version?.VanillaName
                        If String.Equals(instMcVer, targetVersion.McVersion, StringComparison.OrdinalIgnoreCase) Then
                            Dim instVerInfo = New GameVersionInfo(instMcVer, GetLoaderString(inst))
                            If targetVersion.IsCompatibleWith(instVerInfo) Then
                                Return inst
                            End If
                        End If
                    End If
                Next
            Next
        Catch ex As Exception
            Logger.Warn(ex, "检索本地匹配实例异常")
        End Try
        Return Nothing
    End Function

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
        ResetHostSecurityControls(False)

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

        Dim secEx As SecurityInterceptionException = Nothing
        If LinkLoader.Error IsNot Nothing Then
            secEx = TryCast(LinkLoader.Error, SecurityInterceptionException)
            If secEx Is Nothing AndAlso LinkLoader.Error.InnerException IsNot Nothing Then
                secEx = TryCast(LinkLoader.Error.InnerException, SecurityInterceptionException)
            End If
        End If

        If secEx IsNot Nothing Then
            LabLoadTitle.Text = "联机核心被安全软件拦截"
            ErrorMsg = $"{secEx.DiagnosticAdvice}{vbCrLf}核心文件：{secEx.BlockedPath}"
            LabLoadDesc.Text = ErrorMsg
            Logger.Warn(LinkLoader.Error, "联机核心被安全防护软件拦截：" & secEx.BlockedPath)
            ShowSecurityGuidance(secEx)
        Else
            If LinkLoader.Error IsNot Nothing Then
                ErrorMsg = LinkLoader.Error.Message
                If TypeOf LinkLoader.Error Is LinkException Then
                    ErrorMsg = CType(LinkLoader.Error, LinkException).LocalizedMessage
                End If
            End If
            LabLoadDesc.Text = ErrorMsg
            Logger.Warn(LinkLoader.Error, "联机加载失败：" & LabLoadTitle.Text)
        End If
    End Sub

    Private Sub ShowSecurityGuidance(ex As SecurityInterceptionException)
        Dim msg = $"检测到系统的杀毒防护软件（如 Windows Defender / 360 / 火绒）拦截或隔离了 PCL2 联机核心文件：{vbCrLf}{vbCrLf}" &
                  $"文件路径：{ex.BlockedPath}{vbCrLf}{vbCrLf}" &
                  $"【排查建议】：{vbCrLf}" &
                  $"1. 打开杀毒软件的「隔离区 / 拦截日志」，将该文件恢复并添加信任；{vbCrLf}" &
                  $"2. 将 PCL2 运行目录加入安全软件的「信任区 / 排除项」以防止再次被拦截。{vbCrLf}{vbCrLf}" &
                  $"是否将该组件路径复制到剪贴板，以便在杀毒软件中添加排除项？"
        If MyMsgBox(msg, "联机核心被拦截", "复制路径并查看教程", "我知道了", IsWarn:=True) = 1 Then
            ClipboardSet(ex.BlockedPath, False)
            OpenWebsite("https://github.com/EasyTier/EasyTier")
            Hint("已将核心组件文件路径复制到剪贴板！", HintType.Green)
        End If
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

            ' 3. 隐私安全
            LinkSettings.ReadGameAccount = Settings.Get(Of Boolean)("LinkReadGameAccount")
            LinkSettings.PremiumImmediateEvict = Settings.Get(Of Boolean)("LinkPremiumImmediateEvict")

            ' 4. 同步启动器当前登录账号的正版身份与 UUID
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

            ' 5. 同步启动器当前选中的 Minecraft 实例核心版本与模组加载器
            If ModMinecraft.McInstanceSelected IsNot Nothing Then
                LinkSettings.CurrentInstanceVersion = ModMinecraft.McInstanceSelected.Version?.VanillaName
                LinkSettings.CurrentInstanceLoader = GetLoaderString(ModMinecraft.McInstanceSelected)
            Else
                LinkSettings.CurrentInstanceVersion = Nothing
                LinkSettings.CurrentInstanceLoader = Nothing
            End If
        Catch ex As Exception
            Logger.Warn(ex, "同步联机设置失败")
        End Try
    End Sub

    Private Sub OnSessionChanged(sender As Object, e As EventArgs)
        _throttledUpdate.Start()
    End Sub

#End Region

#Region "完成页面 (Finished)"

    Private Sub SwitchToFinished(OldState As LinkStates)
        UpdatePanelVisibility()

        ' 房主控制栏与玩家列表初始化
        PanHostControls.Visibility = If(IsServerSide, Visibility.Visible, Visibility.Collapsed)
        ResetHostSecurityControls(True)
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

    ' 房主选项变动（白名单模式 / 仅正版 / 锁定房间）
    Private Sub CheckHostOptions_Change(sender As Object, user As Boolean) Handles CheckWhitelistOnly.Change, CheckPremiumOnly.Change, CheckLockRoom.Change
        If user AndAlso Session IsNot Nothing AndAlso IsServerSide Then
            Session.WhitelistOnly = CheckWhitelistOnly.Checked
            Session.PremiumOnly = CheckPremiumOnly.Checked
            Session.IsLocked = CheckLockRoom.Checked

            Dim evictPremium As Boolean = Settings.Get(Of Boolean)("LinkPremiumImmediateEvict")
            Session.EnforceSecurityPolicies(evictPremium)

            If sender Is CheckLockRoom Then
                Hint(If(CheckLockRoom.Checked, "房间已锁定，禁止新玩家加入", "房间已解锁，允许新玩家加入"), HintType.Blue)
            ElseIf sender Is CheckPremiumOnly Then
                Hint(If(CheckPremiumOnly.Checked, "已开启仅正版验证模式", "已关闭仅正版验证模式"), HintType.Blue)
            ElseIf sender Is CheckWhitelistOnly Then
                Hint(If(CheckWhitelistOnly.Checked, "已开启白名单准入模式", "已关闭白名单准入模式"), HintType.Blue)
            End If
        End If
    End Sub

    ''' <summary>
    ''' 彻底重置房主准入控制台与底层 Session 策略标志，确保跨房间状态 100% 隔离
    ''' </summary>
    Private Sub ResetHostSecurityControls(Optional resetSession As Boolean = True)
        If CheckWhitelistOnly IsNot Nothing Then CheckWhitelistOnly.Checked = False
        If CheckPremiumOnly IsNot Nothing Then CheckPremiumOnly.Checked = False
        If CheckLockRoom IsNot Nothing Then CheckLockRoom.Checked = False

        If resetSession AndAlso Session IsNot Nothing AndAlso IsServerSide Then
            Session.WhitelistOnly = False
            Session.PremiumOnly = False
            Session.IsLocked = False
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

        ' 依托 C# 强类型契约一次性获取整页快照，杜绝跨语言晚期绑定
        Dim snapshot = Session.GetRoomSnapshot(IsServerSide)

        ' 1. 延迟
        LabFinishPing.Text = snapshot.PingText
        BtnFinishPing.ToolTip = snapshot.PingToolTip

        ' 2. 人数与列表
        LabFinishPlayer.Text = snapshot.PlayerCountSummary
        LabPlayerListCount.Text = snapshot.PlayerCountDetail
        RefreshPlayerList(snapshot.Players)

        ' 3. 提示卡片 (HintFinish)
        HintFinish.Text = snapshot.HintText
        HintFinish.Theme = CType(snapshot.HintTheme, MyHint.Themes)

        ' 4. 版本审核与智能自愈 (CardVersionCheck)
        RefreshVersionCard(snapshot.HostGameVersion)
    End Sub

    Private Sub RefreshVersionCard(hostVersion As GameVersionInfo)
        If CardVersionCheck Is Nothing Then Return

        If IsServerSide Then
            CurrentVersionState = VersionCheckState.Match
            MatchedLocalInstance = Nothing
            Dim verText = If(hostVersion IsNot Nothing AndAlso hostVersion.McVersion <> "Unknown", hostVersion.DisplayText, "正在检测……")
            LabVersionStateTitle.Text = $"当前游戏版本：{verText}"
            LabVersionStateDesc.Text = "已就绪，游戏核心版本与模组环境已向房间内所有好友广播。"
            PathVersionState.Data = CType((New GeometryConverter).ConvertFromString(IconVersionMatch), Geometry)
            PathVersionState.SetResourceReference(Shapes.Path.FillProperty, "ColorBrush3")
            BtnVersionAction.Visibility = Visibility.Collapsed
            Return
        End If

        ' 访客视角
        If hostVersion Is Nothing OrElse hostVersion.McVersion = "Unknown" Then
            CurrentVersionState = VersionCheckState.Unknown
            MatchedLocalInstance = Nothing
            LabVersionStateTitle.Text = "正在获取房主游戏版本……"
            LabVersionStateDesc.Text = "局域网通道已建立，正在与房主交换游戏核心版本信息。"
            PathVersionState.Data = CType((New GeometryConverter).ConvertFromString(IconVersionSwitch), Geometry)
            PathVersionState.SetResourceReference(Shapes.Path.FillProperty, "ColorBrush3")
            BtnVersionAction.Visibility = Visibility.Collapsed
            Return
        End If

        Dim currentInst = ModMinecraft.McInstanceSelected
        Dim currentVer = currentInst?.Version?.VanillaName
        Dim currentVerInfo = If(currentInst IsNot Nothing, New GameVersionInfo(currentVer, GetLoaderString(currentInst)), Nothing)
        Dim isCompatible = hostVersion.IsCompatibleWith(currentVerInfo)

        If isCompatible Then
            CurrentVersionState = VersionCheckState.Match
            MatchedLocalInstance = Nothing
            LabVersionStateTitle.Text = $"版本匹配：{hostVersion.DisplayText}"
            LabVersionStateDesc.Text = $"当前选中版本【{currentInst.Name}】与房主核心版本一致，可直接进入游戏！"
            PathVersionState.Data = CType((New GeometryConverter).ConvertFromString(IconVersionMatch), Geometry)
            PathVersionState.SetResourceReference(Shapes.Path.FillProperty, "ColorBrush3")
            BtnVersionAction.Text = "启动游戏"
            BtnVersionAction.ColorType = MyButton.ColorState.Highlight
            BtnVersionAction.Visibility = Visibility.Visible
        Else
            ' 本地检索自愈
            MatchedLocalInstance = FindMatchingLocalInstance(hostVersion)
            If MatchedLocalInstance IsNot Nothing Then
                CurrentVersionState = VersionCheckState.LocalFound
                LabVersionStateTitle.Text = $"版本不一致：房主为 {hostVersion.DisplayText}"
                LabVersionStateDesc.Text = $"当前选中【{If(currentInst?.Name, "未选择")}】。已在你的电脑中找到对应版本【{MatchedLocalInstance.Name}】！"
                PathVersionState.Data = CType((New GeometryConverter).ConvertFromString(IconVersionSwitch), Geometry)
                PathVersionState.SetResourceReference(Shapes.Path.FillProperty, "ColorBrush3")
                BtnVersionAction.Text = "一键切换版本"
                BtnVersionAction.ColorType = MyButton.ColorState.Highlight
                BtnVersionAction.Visibility = Visibility.Visible
            Else
                CurrentVersionState = VersionCheckState.Missing
                LabVersionStateTitle.Text = $"未安装对应版本：{hostVersion.DisplayText}"
                LabVersionStateDesc.Text = "你的电脑尚未安装与房主兼容的游戏版本，点击右侧可一键前往下载安装。"
                PathVersionState.Data = CType((New GeometryConverter).ConvertFromString(IconVersionDownload), Geometry)
                PathVersionState.SetResourceReference(Shapes.Path.FillProperty, "ColorBrushSemiYellow")
                BtnVersionAction.Text = "一键下载版本"
                BtnVersionAction.ColorType = MyButton.ColorState.Highlight
                BtnVersionAction.Visibility = Visibility.Visible
            End If
        End If
    End Sub

    Private Sub BtnVersionAction_Click() Handles BtnVersionAction.Click
        Select Case CurrentVersionState
            Case VersionCheckState.Match
                ModLaunch.McLaunchStart()
            Case VersionCheckState.LocalFound
                If MatchedLocalInstance IsNot Nothing Then
                    ModMinecraft.McInstanceSelected = MatchedLocalInstance
                    Hint($"已为你切换游戏版本至【{MatchedLocalInstance.Name}】！", HintType.Green)
                    Update()
                End If
            Case VersionCheckState.Missing
                FrmMain.PageChange(FormMain.PageType.Download, FormMain.PageSubType.DownloadInstall)
                Hint("已为你打开版本下载页面，请选择下载对应版本！", HintType.Blue)
        End Select
    End Sub

    ''' <summary>
    ''' 增量比对渲染房间玩家列表（基于 MachineId 复用已有 Grid/ContextMenu，杜绝高频 Visual 树销毁重建）。
    ''' </summary>
    Private Sub RefreshPlayerList(players As IReadOnlyList(Of LinkPlayerViewDto))
        If PanPlayerItems Is Nothing Then Return

        If players Is Nothing OrElse players.Count = 0 Then
            If _playerRowHolders.Count > 0 OrElse PanPlayerItems.Children.Count = 0 Then
                _playerRowHolders.Clear()
                PanPlayerItems.Children.Clear()
                Dim emptyLab As New TextBlock With {
                    .Text = "暂无其他玩家在线",
                    .FontSize = 12,
                    .HorizontalAlignment = HorizontalAlignment.Center,
                    .Margin = New Thickness(0, 10, 0, 10)
                }
                emptyLab.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
                PanPlayerItems.Children.Add(emptyLab)
            End If
            Return
        End If

        ' 若当前显示的是“暂无其他玩家在线”，清除该标签
        If _playerRowHolders.Count = 0 AndAlso PanPlayerItems.Children.Count > 0 Then
            PanPlayerItems.Children.Clear()
        End If

        ' 1. 移除已离开房间的玩家行
        Dim currentIds As New HashSet(Of String)
        For Each p In players
            currentIds.Add(p.MachineId)
        Next
        Dim toRemove As New List(Of String)
        For Each id In _playerRowHolders.Keys
            If Not currentIds.Contains(id) Then toRemove.Add(id)
        Next
        For Each id In toRemove
            PanPlayerItems.Children.Remove(_playerRowHolders(id).RowGrid)
            _playerRowHolders.Remove(id)
        Next

        ' 2. 增量更新或追加玩家行
        For i As Integer = 0 To players.Count - 1
            Dim dto = players(i)
            Dim holder As PlayerRowHolder = Nothing
            If _playerRowHolders.TryGetValue(dto.MachineId, holder) Then
                ' 【增量复用】：仅刷新文字与 ToolTip，绝不销毁已有控件与右键菜单
                holder.LabPing.Text = dto.PingText
                holder.RowGrid.ToolTip = dto.TooltipText
                holder.TargetPlayer = dto.RawProfile

                ' 角色标识或管理权限变动时原地动态更新徽章与右键菜单
                If holder.CurrentDto Is Nothing OrElse holder.CurrentDto.RoleBadgeText <> dto.RoleBadgeText OrElse holder.CurrentDto.CanManage <> dto.CanManage Then
                    If holder.TxtRole IsNot Nothing Then
                        holder.TxtRole.Text = dto.RoleBadgeText
                        holder.TxtRole.SetResourceReference(TextBlock.ForegroundProperty, dto.RoleBadgeFgBrushKey)
                        holder.BadgeRole.SetResourceReference(Border.BackgroundProperty, dto.RoleBadgeBgBrushKey)
                    End If
                    AttachPlayerContextMenu(holder, dto)
                End If
                holder.CurrentDto = dto
            Else
                ' 【新玩家】：构建行对象并存入缓存
                holder = CreatePlayerRow(dto)
                _playerRowHolders(dto.MachineId) = holder
                PanPlayerItems.Children.Insert(Math.Min(i, PanPlayerItems.Children.Count), holder.RowGrid)
            End If
        Next
    End Sub

    Private Function CreatePlayerRow(dto As LinkPlayerViewDto) As PlayerRowHolder
        Dim holder As New PlayerRowHolder With {
            .TargetPlayer = dto.RawProfile,
            .CurrentDto = dto
        }

        Dim row As New Grid With {
            .Margin = New Thickness(0, 3, 0, 3),
            .Background = Brushes.Transparent,
            .Height = 28,
            .ToolTip = dto.TooltipText
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
        If dto.IsHost Then
            dot.SetResourceReference(Border.BackgroundProperty, "ColorBrush3")
        Else
            dot.Background = New SolidColorBrush(Color.FromRgb(&H4C, &HAF, &H50))
        End If
        nameStack.Children.Add(dot)

        ' 玩家名称展示（权限视差已在 DTO 中完成预处理）
        Dim labName As New TextBlock With {
            .FontSize = 13,
            .VerticalAlignment = VerticalAlignment.Center,
            .Text = If(dto.SubName IsNot Nothing, $"{dto.DisplayName} {dto.SubName}", dto.DisplayName)
        }
        labName.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrush1")
        nameStack.Children.Add(labName)

        ' 角色身份徽章（[房主] / [管理] / [访客]）
        Dim badgeRole As New Border With {
            .CornerRadius = New CornerRadius(3),
            .Margin = New Thickness(6, 0, 0, 0),
            .Padding = New Thickness(4, 1, 4, 1),
            .VerticalAlignment = VerticalAlignment.Center
        }
        badgeRole.SetResourceReference(Border.BackgroundProperty, dto.RoleBadgeBgBrushKey)
        Dim txtRole As New TextBlock With {
            .Text = dto.RoleBadgeText,
            .FontSize = 10.5,
            .FontWeight = If(dto.IsHost, FontWeights.Medium, FontWeights.Normal)
        }
        txtRole.SetResourceReference(TextBlock.ForegroundProperty, dto.RoleBadgeFgBrushKey)
        badgeRole.Child = txtRole
        nameStack.Children.Add(badgeRole)

        ' 本人标记 [我]
        If dto.IsLocalMe Then
            Dim badgeMe As New Border With {
                .CornerRadius = New CornerRadius(3),
                .Margin = New Thickness(4, 0, 0, 0),
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

        ' 离线灰色标识
        If IsServerSide AndAlso dto.IsOfflineMode Then
            Dim badgeOffline As New Border With {
                .CornerRadius = New CornerRadius(3),
                .Margin = New Thickness(4, 0, 0, 0),
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
            .Margin = New Thickness(0, 0, 4, 0),
            .Text = dto.PingText
        }
        labPing.SetResourceReference(TextBlock.ForegroundProperty, "ColorBrushGray3")
        Grid.SetColumn(labPing, 1)
        row.Children.Add(labPing)

        holder.RowGrid = row
        holder.LabPing = labPing
        holder.BadgeRole = badgeRole
        holder.TxtRole = txtRole

        AttachPlayerContextMenu(holder, dto)

        Return holder
    End Function

    Private Sub AttachPlayerContextMenu(holder As PlayerRowHolder, dto As LinkPlayerViewDto)
        If Not dto.CanManage Then
            holder.RowGrid.ContextMenu = Nothing
            Return
        End If

        Dim menu As New ContextMenu()

        ' 设为管理员 / 撤销管理员（仅房主对访客/管理可用）
        If dto.CanSetAdmin Then
            Dim itemSetAdmin As New MyMenuItem With {
                .Header = "设为管理员",
                .Icon = "F1 M 38,19C 43.5228,19 48,23.4772 48,29C 48,34.5228 43.5228,39 38,39C 32.4772,39 28,34.5228 28,29C 28,23.4772 32.4772,19 38,19 Z M 38,43C 48.5,43 57,48 57,54L 19,54C 19,48 27.5,43 38,43 Z"
            }
            AddHandler itemSetAdmin.Click, Sub()
                If Session IsNot Nothing Then
                    Session.SetPlayerAdmin(holder.TargetPlayer.MachineId, True)
                    Hint("已将玩家 " & holder.TargetPlayer.Name & " 设为管理员", HintType.Green)
                End If
            End Sub
            menu.Items.Add(itemSetAdmin)
        ElseIf dto.CanRevokeAdmin Then
            Dim itemRevokeAdmin As New MyMenuItem With {
                .Header = "撤销管理员",
                .Icon = "F1 M 38,19C 43.5228,19 48,23.4772 48,29C 48,34.5228 43.5228,39 38,39C 32.4772,39 28,23.4772 32.4772,19 38,19 Z M 38,43C 48.5,43 57,48 57,54L 19,54C 19,48 27.5,43 38,43 Z"
            }
            AddHandler itemRevokeAdmin.Click, Sub()
                If Session IsNot Nothing Then
                    Session.SetPlayerAdmin(holder.TargetPlayer.MachineId, False)
                    Hint("已撤销玩家 " & holder.TargetPlayer.Name & " 的管理员权限", HintType.Green)
                End If
            End Sub
            menu.Items.Add(itemRevokeAdmin)
        End If

        ' 白名单切换（仅房主对非房主玩家可用）
        If dto.CanToggleWhitelist Then
            Dim itemWhitelist As New MyMenuItem With {
                .Header = If(dto.IsWhitelisted, "移出白名单", "加入白名单"),
                .Icon = "F1 M 38,14C 44.6274,14 50,19.3726 50,26C 50,32.6274 44.6274,38 38,38C 31.3726,38 26,32.6274 26,26C 26,19.3726 31.3726,14 38,14 Z M 38,42C 49.0457,42 58,47.3726 58,54L 18,54C 18,47.3726 26.9543,42 38,42 Z"
            }
            AddHandler itemWhitelist.Click, Sub()
                If Session IsNot Nothing Then
                    If dto.IsWhitelisted Then
                        Session.RemoveWhitelist(holder.TargetPlayer)
                        Hint("已将玩家 " & holder.TargetPlayer.Name & " 移出白名单", HintType.Green)
                    Else
                        Session.AddWhitelist(holder.TargetPlayer)
                        Hint("已将玩家 " & holder.TargetPlayer.Name & " 添加至常驻白名单", HintType.Green)
                    End If
                End If
            End Sub
            menu.Items.Add(itemWhitelist)
        End If

        ' 重置设备绑定（仅房主对已绑定特征码的离线白名单玩家可用）
        If dto.CanResetDeviceBinding Then
            Dim itemResetBinding As New MyMenuItem With {
                .Header = "重置设备绑定（允许换机连入）",
                .Icon = "F1 M 38,14C 44.6274,14 50,19.3726 50,26C 50,32.6274 44.6274,38 38,38C 31.3726,38 26,32.6274 26,26C 26,19.3726 31.3726,14 38,14 Z M 38,42C 49.0457,42 58,47.3726 58,54L 18,54C 18,47.3726 26.9543,42 38,42 Z"
            }
            AddHandler itemResetBinding.Click, Sub()
                If Session IsNot Nothing Then
                    Session.ResetWhitelistBinding(holder.TargetPlayer)
                    Hint("已重置玩家 " & holder.TargetPlayer.Name & " 的设备特征码，好友可使用新电脑重新连入", HintType.Green)
                End If
            End Sub
            menu.Items.Add(itemResetBinding)
        End If

        ' 移出房间 (1分钟)（房主与管理员均可执行）
        If dto.CanKick Then
            Dim itemKick As New MyMenuItem With {
                .Header = "移出房间 (1分钟)",
                .Icon = "F1 M 23.3428,25.205L 23.3805,25.4461C 23.9229,27.177 30.261,29.0992 38,29.0992C 45.7386,29.0992 52.0765,27.1771 52.6194,25.4463L 52.6571,25.205C 52.6571,23.3616 46.0949,21.3109 38,21.3109C 29.9051,21.3109 23.3428,23.3616 23.3428,25.205 Z M 23.3428,53.0204L 19.1571,26.2111C 19.0534,25.8817 19,25.5459 19,25.205C 19,20.9036 27.5066,17.4167 38,17.4167C 48.4934,17.4167 57,20.9036 57,25.205C 57,25.5459 56.9466,25.8818 56.8429,26.2112L 52.6571,53.0204L 52.5974,53.0204C 51.9241,56.1393 45.6457,58.5833 38,58.5833C 30.3543,58.5833 24.076,56.1393 23.4026,53.0204L 23.3428,53.0204 Z"
            }
            AddHandler itemKick.Click, Async Sub()
                If Session IsNot Nothing Then
                    Try
                        Await Session.KickPlayerAsync(holder.TargetPlayer)
                        Hint("已将玩家 " & holder.TargetPlayer.Name & " 移出房间（1 分钟内禁止重连）", HintType.Green)
                    Catch ex As Exception
                        Hint("移出玩家失败：" & ex.Message, HintType.Red)
                    End Try
                End If
            End Sub
            menu.Items.Add(itemKick)
        End If

        ' 永久拉黑玩家（带二次确认，仅房主可用）
        If dto.CanBlock Then
            Dim itemBlock As New MyMenuItem With {
                .Header = "永久拉黑玩家",
                .Icon = "F1 M 26.9166,22.1667L 37.9999,33.25L 49.0832,22.1668L 53.8332,26.9168L 42.7499,38L 53.8332,49.0834L 49.0833,53.8334L 37.9999,42.75L 26.9166,53.8334L 22.1666,49.0833L 33.25,38L 22.1667,26.9167L 26.9166,22.1667 Z "
            }
            AddHandler itemBlock.Click, Sub()
                If Session IsNot Nothing Then
                    Dim confirmMsg = "确定要永久拉黑玩家“" & holder.TargetPlayer.Name & "”吗？" & vbCrLf & "该玩家的设备特征码将被永久拦截，无法再次加入该房间。"
                    If MyMsgBox(confirmMsg, "拉黑玩家", "确定拉黑", "取消", IsWarn:=True) = 1 Then
                        Session.RemovePlayer(holder.TargetPlayer, block:=True)
                        Hint("已永久拉黑玩家 " & holder.TargetPlayer.Name, HintType.Green)
                    End If
                End If
            End Sub
            menu.Items.Add(itemBlock)
        End If

        holder.RowGrid.ContextMenu = menu
    End Sub

#End Region

#Region "进程与生命周期回收"

    Public Shared Sub ProcessStop()
        Try
            _playerRowHolders.Clear()
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
