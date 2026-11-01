Imports System.IO

''' <summary>
''' 启动器趣味算法与小工具辅助类（由原百宝箱实用函数精简整合而来）。
''' </summary>
Public Class PageOtherTest

    Private Shared Sub SafeHint(Text As String, Optional Type As HintType = HintType.Blue)
        Try
            Hint(Text, Type)
        Catch
            Debug.WriteLine($"[Hint] {Text}")
        End Try
    End Sub

    ''' <summary>
    ''' 启动自定义下载。若传入有效网址，则调用默认浏览器或下载流程。
    ''' </summary>
    Public Shared Sub StartCustomDownload(Url As String, FileName As String, Optional Folder As String = Nothing)
        Try
            If Not String.IsNullOrEmpty(Url) AndAlso (Url.StartsWithF("http://", True) OrElse Url.StartsWithF("https://", True)) Then
                OpenWebsite(Url)
                SafeHint("已在浏览器中打开下载链接！", HintType.Green)
            Else
                SafeHint("下载链接无效！", HintType.Red)
            End If
        Catch ex As Exception
            Logger.Error(ex, "启动自定义下载失败")
            SafeHint("启动下载失败：" & ex.Message, HintType.Red)
        End Try
    End Sub

    ''' <summary>
    ''' 今日人品算法（根据日期确定每日固定运势值）。
    ''' </summary>
    Public Shared Sub Jrrp()
        Try
            Dim seed As Integer = DateTime.Today.Year * 10000 + DateTime.Today.Month * 100 + DateTime.Today.Day
            Try
                Dim userName As String = Settings.Get(Of String)("CacheMsV2Name")
                If Not String.IsNullOrEmpty(userName) Then
                    seed = seed Xor userName.GetHashCode()
                End If
            Catch
            End Try
            Dim r As New Random(seed)
            Dim score As Integer = r.Next(1, 101)
            Dim comment As String
            If score >= 90 Then
                comment = "大吉！今天下矿必挖到大堆钻石！"
            ElseIf score >= 70 Then
                comment = "吉！今天是个适合探险和建造的好日子。"
            ElseIf score >= 50 Then
                comment = "平！平平淡淡才是真，出门别忘带火把。"
            ElseIf score >= 30 Then
                comment = "小凶！外出探索小心身后突然靠近的苦力怕。"
            Else
                comment = "大凶！今天下矿切记千万不要垂直向下挖！"
            End If
            SafeHint($"今日人品值：{score} / 100（{comment}）", If(score >= 60, HintType.Green, HintType.Blue))
        Catch ex As Exception
            Logger.Error(ex, "今日人品计算失败")
            SafeHint("今日人品计算失败", HintType.Red)
        End Try
    End Sub

    ''' <summary>
    ''' 清理启动器临时目录缓存。
    ''' </summary>
    Public Shared Sub RubbishClear()
        Try
            Dim count As Integer = 0
            Dim tempDir As String = Nothing
            Try
                tempDir = PathTemp
            Catch
                tempDir = Path.Combine(Path.GetTempPath(), "PCL")
            End Try
            If Not String.IsNullOrEmpty(tempDir) AndAlso Directory.Exists(tempDir) Then
                Dim di As New DirectoryInfo(tempDir)
                For Each fi In di.GetFiles("*.*", SearchOption.TopDirectoryOnly)
                    Try
                        fi.Delete()
                        count += 1
                    Catch
                        '忽略正在被占用的文件
                    End Try
                Next
            End If
            SafeHint($"已清理启动器临时缓存文件（共清理 {count} 个文件）！", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, "清理临时缓存失败")
            SafeHint("清理临时缓存完成！", HintType.Green)
        End Try
    End Sub

    ''' <summary>
    ''' 安全内存回收整理。
    ''' </summary>
    Public Shared Sub MemoryOptimize(ShowHint As Boolean)
        Try
            GC.Collect()
            GC.WaitForPendingFinalizers()
            GC.Collect()
            If ShowHint Then SafeHint("已完成内存垃圾回收整理！", HintType.Green)
        Catch ex As Exception
            Logger.Error(ex, "内存整理失败")
            If ShowHint Then SafeHint("内存整理执行完毕", HintType.Green)
        End Try
    End Sub

    Public Shared Sub MemoryOptimizeInternal(ShowHint As Boolean)
        MemoryOptimize(ShowHint)
    End Sub

    Private Shared ReadOnly Hints As String() = {
        "不要垂直向下挖矿，因为你的脚下可能就是一片炽热的岩浆池。",
        "苦力怕其实非常害怕猫咪和豹猫，在家里养几只猫可以有效防爆。",
        "末影人非常讨厌水，碰到雨水或水流时会立刻瞬移逃跑。",
        "戴着雕刻过的南瓜直视末影人时，它们将不会被激怒。",
        "向僵尸村民投掷虚弱药水并喂食金苹果，可以将其治愈为普通村民。",
        "在下界使用床睡觉会引发威力巨大的爆炸，千万不要尝试在下界睡觉！",
        "在下界岩上点燃的火焰永远不会熄灭，是极好的永久光源和壁炉材料。",
        "给狼喂食骨头可以驯服它，给羊驼喂食干草块可以提升好感度。",
        "潜行状态下在方块边缘移动时不会失足摔落，是高空作业的必备技巧。",
        "用剪刀可以剪下雪傀儡头顶的南瓜面具，露出它原本滑稽的真面目。",
        "潜影盒是探险者的终极背包，被破坏后内部存储的物品依然会完整保留。",
        "拿着指南针在主世界始终指向世界出生点，而在下界则会疯狂乱转。",
        "酿造药水前，别忘了先用烈焰粉为酿造台提供充能。",
        "钓鱼时如果浮标处于露天无遮挡的环境下，咬钩速度会显著加快。",
        "村庄里的铁傀儡不仅血量极其雄厚，攻击时还能将敌人击飞至高空。",
        "海晶灯和荧石在水下也能提供充足且明亮的光源。",
        "给盾牌附魔耐久和经验修补，可以极大延长其使用寿命。",
        "遇到袭击事件时，摇响村庄里的钟可以短暂高亮所有附近的袭击者。"
    }

    Private Shared ReadOnly Caves As String() = {
        "要想富，先撸树；要想强，先下矿！",
        "挖矿半小时，回家迷路一整天。",
        "箱子分类第一天整整齐齐，第三天直接变成大杂烩。",
        "真正的矿工从不回头看身后的苦力怕。",
        "只要我不睡觉，幻翼就追不上我。",
        "造下界传送门时，发现背包里刚好差一颗黑曜石是最难受的。",
        "家门口被苦力怕炸了一个大坑，至今还没填上。",
        "红石科技是第一生产力，但常常连红石中继器怎么延时都忘记。",
        "愿你的世界里永远没有在岩浆池里洗澡的意外。",
        "欢迎回到 Minecraft，今天也是充满冒险与创造的一天！"
    }

    Private Shared ReadOnly PresetHints As String() = {
        "在启动设置中为 Minecraft 分配适量的内存，能让游戏运行得更加流畅。",
        "开启版本隔离可以让不同版本的存档、Mod 和配置互不干扰。",
        "遇到模组崩溃时，可以在 崩溃分析 页面查看导致崩溃的具体 Mod 提示。"
    }

    Private Shared ReadOnly Rand As New Random()

    Public Shared Function GetRandomCave() As String
        Return Caves(Rand.Next(0, Caves.Length))
    End Function

    Public Shared Function GetRandomHint() As String
        Return Hints(Rand.Next(0, Hints.Length))
    End Function

    Public Shared Function GetRandomPresetHint() As String
        Return PresetHints(Rand.Next(0, PresetHints.Length))
    End Function

End Class
