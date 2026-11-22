Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Windows.Forms
Imports App.Pages
Imports SymbolPrint.Core.Model

Namespace App

    ''' <summary>
    ''' 主窗口：左侧导航 + 右侧页面容器。
    ''' 页面之间需要共享"当前章"，所以章文件状态挂在 MainForm 上，各页面订阅事件。
    ''' </summary>
    Friend NotInheritable Class MainForm
        Inherits Form

        Private ReadOnly _nav As Panel
        Private ReadOnly _content As Panel
        Private ReadOnly _navItems As New List(Of NavItem)
        Private ReadOnly _pages As New Dictionary(Of String, Control)
        Private _current As String = ""
        Private _dragOverlay As PictureBox
        Private _dragShot As Bitmap

        Private _stampPage As StampPage
        Private _verifyPage As VerifyPage
        Private _makePage As MakeSealPage
        Private _settingsPage As SettingsPage

        ''' <summary>全局配置（便携式，存于程序目录 config\settings.json）。</summary>
        Friend ReadOnly Property Settings As AppSettings = AppSettings.Load()

        ''' <summary>当前选中的章文件路径（空 = 未选择）。</summary>
        Friend Property SealPath As String = ""
        ''' <summary>当前章对象（已解锁）。</summary>
        Friend Property CurrentSeal As StampFile
        ''' <summary>章变化时通知各页面刷新。</summary>
        Friend Event SealChanged As EventHandler

        ''' <summary>按屏幕可用区域挑一个既不局促、也不越界的默认窗口尺寸。</summary>
        Private Shared Function PreferredWindowSize() As Size
            ' 默认按 1920×1080 开；屏幕放不下时退回可用区的 96%，再兜底最小值。
            ' 之前是 1840×1120，在 1080p 上偏高，容易顶到任务栏。
            Dim wa = Screen.PrimaryScreen.WorkingArea
            Dim w, h As Integer
            If wa.Width >= 1920 AndAlso wa.Height >= 1080 Then
                ' 屏幕装得下就精确按 1920×1080 开
                w = 1920
                h = 1080
            Else
                ' 装不下（1080p 屏去掉任务栏约 1040 高）就占满可用区的 96%
                w = CInt(wa.Width * 0.96)
                h = CInt(wa.Height * 0.96)
            End If
            w = Math.Max(w, 1200) : w = Math.Min(w, wa.Width)
            h = Math.Max(h, 860) : h = Math.Min(h, wa.Height)
            Return New Size(w, h)
        End Function

        Private ReadOnly _startupArgs As String()

        Friend Sub New(Optional args As String() = Nothing)
            _startupArgs = args
            ' 启动时把「载体」设置下发给各载体（文本码点 / RTF 组名 / ZIP 注释上限 / 优先级）
            SymbolPrint.Core.Service.CarrierRegistry.Configure(Settings.Carriers)
            Text = "SymbolPrint — 电子盖章"
            MinimumSize = New Size(1120, 820)
            Size = PreferredWindowSize()
            StartPosition = FormStartPosition.CenterScreen

            ' 「记住窗口位置」：上次在哪就在哪（先校验还在某块屏幕上，免得拔了副屏看不见窗口）
            If Settings.Ui.RememberWindowBounds Then
                Dim st = AppUiState.Load()
                If st.BoundsVisible() Then
                    StartPosition = FormStartPosition.Manual
                    Bounds = New Rectangle(st.X, st.Y, st.Width, st.Height)
                End If
            End If
            BackColor = Theme.Bg
            Font = Theme.FontBase
            DoubleBuffered = True
            ' 用系统图标里最接近的（盾牌），避免额外资源依赖
            Try
                Icon = Theme.AppIcon()
            Catch
            End Try

            _nav = BuildNav()
            _content = New Panel() With {.Dock = DockStyle.Fill, .BackColor = Theme.Bg, .Padding = New Padding(18, 12, 18, 12)}

            Controls.Add(_content)
            Controls.Add(_nav)
            Controls.Add(BuildHeader())

            BuildPages()
            ' 所有页面建完后统一刷一遍输入控件配色（数字框/路径框/日志框都是直接 New 的）
            Theme.ThemeInputs(Me)
            ShowPage("stamp")

            AddHandler Shown, Sub(s, e)
                                  HandleStartupArgs()
                                  RestoreLastFiles()
                                  RunAuditIfRequested()
                              End Sub
        End Sub

        ''' <summary>「启动时打开上次文件」：只恢复仍然存在的文件，缺的直接丢掉。</summary>
        Private Sub RestoreLastFiles()
            If Not Settings.Ui.ReopenLastFile Then Return
            If _startupArgs IsNot Nothing AndAlso (Array.IndexOf(_startupArgs, "--audit") >= 0 OrElse
                                                   Array.IndexOf(_startupArgs, "--sweep") >= 0) Then Return
            Try
                Dim st = AppUiState.Load()
                If st.LastFiles Is Nothing OrElse st.LastFiles.Count = 0 Then Return
                Dim alive = st.LastFiles.Where(Function(f) IO.File.Exists(f)).ToArray()
                If alive.Length > 0 Then
                    _stampPage.PrefillFiles(alive, "", Nothing, Nothing)
                End If
            Catch
                ' 恢复失败不影响启动
            End Try
        End Sub

        ''' <summary>
        ''' ESC 取消：只在「进度 → ESC 键取消」开着、且当前页确实有任务在跑时吃掉按键，
        ''' 否则照常传给下面的控件（不该因为按了 ESC 就什么都不响应）。
        ''' </summary>
        Protected Overrides Function ProcessCmdKey(ByRef msg As Message, keyData As Keys) As Boolean
            If keyData = (Keys.Escape Or Keys.KeyCode) AndAlso Settings.Progress.CancelOnEsc Then
                If _pages.ContainsKey(_current) Then
                    Dim c = TryCast(_pages(_current), ICancelablePage)
                    If c IsNot Nothing AndAlso c.CancelCurrent() Then Return True
                End If
            End If
            Return MyBase.ProcessCmdKey(msg, keyData)
        End Function

        ''' <summary>关闭时按设置保存窗口位置与上次文件；任何一个开关关着就不写。</summary>
        Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
            Try
                Dim st = AppUiState.Load()
                If Settings.Ui.RememberWindowBounds Then
                    Dim b = If(WindowState = FormWindowState.Normal, Bounds, RestoreBounds)
                    If b.Width >= 400 AndAlso b.Height >= 300 Then
                        st.HasBounds = True
                        st.X = b.X
                        st.Y = b.Y
                        st.Width = b.Width
                        st.Height = b.Height
                    End If
                End If

                If Settings.Ui.ReopenLastFile Then
                    st.LastFiles = _stampPage.Files.ToList()
                Else
                    st.LastFiles.Clear()
                End If
                st.Save()
            Catch
                ' 状态存不下不影响退出
            End Try
            MyBase.OnFormClosing(e)
        End Sub

        ''' <summary>
        ''' --dragshot [输出png]：抓「拖动过程中」的那一帧。
        ''' 做法：先放大到 1720 并完整布局；再缩到 1180 但不泵消息（模拟拖动中布局尚未完成的一瞬），
        ''' 立刻 PrintWindow 把当前表面画到文件。这是唯一能看见「用户看到的裁断」的办法。
        ''' </summary>
        Private Sub RunDragShot(outFile As String)
            ' 按 --size 指定目标小尺寸，默认 1180x860
            Dim targetW = 1180, targetH = 860
            Dim sizeArg = ArgValue("--size")
            If sizeArg <> "" Then
                Dim xy = sizeArg.ToLowerInvariant().Split("x"c)
                If xy.Length = 2 Then
                    Integer.TryParse(xy(0), targetW)
                    Integer.TryParse(xy(1), targetH)
                End If
            End If
            Dim pageArg = ArgValue("--page")
            If pageArg <> "" Then ShowPage(pageArg)

            Size = New Size(1720, 1000)
            Application.DoEvents()
            ' 走真实拖动路径：先拍快照、隐藏真实控件，再改尺寸（不泵消息，拿到中间态）
            UiState.Begin()
            BeginDragSnapshot()
            Size = New Size(targetW, targetH)
            Dim bmp = CaptureWindow()
            Try
                bmp.Save(outFile, Imaging.ImageFormat.Png)
            Catch
            End Try
            bmp.Dispose()
            EndDragSnapshot()
            Dim curPage = TryCast(If(_pages.ContainsKey(_current), _pages(_current), Nothing), IAppPage)
            If curPage IsNot Nothing Then curPage.Relayout()
            PerformLayout()
            UiState.Finish()
            ' 再抓一张「松手后」的状态，确认快照已撤掉、真实控件恢复且布局正确
            Application.DoEvents()
            Dim after As Bitmap = CaptureWindow()
            Try
                after.Save(IO.Path.ChangeExtension(outFile, Nothing) & "_after.png", Imaging.ImageFormat.Png)
            Catch
            End Try
            after.Dispose()
            Close()
        End Sub

        <Runtime.InteropServices.DllImport("user32.dll")>
        Private Shared Function PrintWindow(hwnd As IntPtr, hdc As IntPtr, flags As UInteger) As Boolean
        End Function

        <Runtime.InteropServices.DllImport("user32.dll")>
        Private Shared Function GetWindowDC(hwnd As IntPtr) As IntPtr
        End Function

        ''' <summary>把窗口当前表面复制成位图（用 PrintWindow 的 FULLCONTENT 模式，不受遮挡影响）。</summary>
        Private Function CaptureWindow() As Bitmap
            Dim bmp As New Bitmap(Width, Height)
            Using g = Drawing.Graphics.FromImage(bmp)
                Dim hdc = g.GetHdc()
                Try
                    PrintWindow(Handle, hdc, 2UI)
                Finally
                    g.ReleaseHdc(hdc)
                End Try
            End Using
            Return bmp
        End Function

        ''' <summary>
        ''' --sweepfine：按 8px 步长逐档扫描，只为回答「哪个具体宽度会裁断」。
        ''' 80px 步长的粗扫会跳过中间宽度，而裁断往往只在某些特定宽度出现（正好差一个换行）。
        ''' 输出只列有问题的那几档，避免刷屏。
        ''' </summary>
        Private Sub RunSweepFine()
            Dim outPath = ArgValue("--sweepfine")
            If String.IsNullOrEmpty(outPath) OrElse outPath.StartsWith("--") Then
                outPath = IO.Path.Combine(IO.Path.GetTempPath(), "symbolprint-sweepfine.txt")
            End If

            Dim sb As New Text.StringBuilder()
            sb.AppendLine($"SymbolPrint 细粒度宽度扫描（步长 8）  {DateTime.Now:yyyy-MM-dd HH:mm:ss}")

            For Each key In New String() {"stamp", "verify", "make", "settings"}
                If Not _pages.ContainsKey(key) Then Continue For
                ShowPage(key)
                Application.DoEvents()

                Dim bad As New List(Of String)()
                Dim checkedCount = 0
                For w = 1112 To 1880 Step 8
                    Dim h = Math.Max(820, CInt(Math.Round(w * 0.62)))
                    Size = New Size(w, h)
                    Application.DoEvents()
                    Dim issues = UiAudit.Run(Me)
                    checkedCount += 1
                    If issues.Count > 0 Then
                        bad.Add($"  宽 {w}：{issues.Count} 项")
                        For Each line In issues
                            bad.Add("      " & line)
                        Next
                    End If
                Next

                sb.AppendLine()
                sb.AppendLine($"=== {key}  扫描 {checkedCount} 档，问题 {If(bad.Count = 0, "0", bad.Count.ToString())} ===")
                For Each line In bad
                    sb.AppendLine(line)
                Next
            Next

            Try
                IO.File.WriteAllText(outPath, sb.ToString(), System.Text.Encoding.UTF8)
            Catch
            End Try
            Close()
        End Sub

        ''' <summary>
        ''' --sweep：按一串窗口宽度逐档扫描四个页面，记录「每档的问题数」和「单次重排耗时」。
        ''' 用来回答两个问题：界面从哪个宽度开始裁断？拖动缩放时卡在哪里（哪页、多少毫秒）。
        ''' 只在排查时用，正常运行不触发。
        ''' </summary>
        Private Sub RunSweep()
            Dim outPath = ArgValue("--sweep")
            If String.IsNullOrEmpty(outPath) OrElse outPath.StartsWith("--") Then
                outPath = IO.Path.Combine(IO.Path.GetTempPath(), "symbolprint-sweep.txt")
            End If

            Dim widths = New Integer() {1120, 1200, 1280, 1360, 1440, 1520, 1600, 1720, 1840}
            Dim sb As New Text.StringBuilder()
            sb.AppendLine($"SymbolPrint 宽度扫描  {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            sb.AppendLine($"最小尺寸 {MinimumSize.Width}x{MinimumSize.Height}")
            sb.AppendLine()
            sb.AppendLine("宽度  | 页面      | 问题 | 重排耗时")

            Dim worst As New Dictionary(Of String, Integer)()
            For Each w In widths
                Dim h = Math.Max(MinimumSize.Height, CInt(Math.Round(w * 0.62)))
                Size = New Size(w, h)
                Application.DoEvents()

                For Each key In New String() {"stamp", "verify", "make", "settings"}
                    If Not _pages.ContainsKey(key) Then Continue For
                    Dim sw = Diagnostics.Stopwatch.StartNew()
                    ShowPage(key)
                    PerformLayout()
                    Application.DoEvents()
                    sw.Stop()

                    Dim issues = UiAudit.Run(Me)
                    sb.AppendLine($"{w,5} | {key,-9} | {issues.Count,4} | {sw.ElapsedMilliseconds,4} ms")
                    For Each line In issues
                        sb.AppendLine("        " & line)
                    Next
                    If Not worst.ContainsKey(key) OrElse issues.Count > worst(key) Then worst(key) = issues.Count
                Next
            Next

            sb.AppendLine()
            sb.AppendLine("---- 各页最差问题数 ----")
            For Each kv In worst
                sb.AppendLine($"  {kv.Key}: {kv.Value}")
            Next

            ' ---- 拖动缩放模拟：连续改变宽度，量每帧真实耗时（这是「卡」的唯一可信来源）----
            sb.AppendLine()
            sb.AppendLine("---- 拖动缩放模拟（从 1720 连续缩到 1120，40 帧）----")
            Prof.Enabled = True
            Size = New Size(1720, 1000)
            Application.DoEvents()
            For Each key In New String() {"stamp", "verify", "make", "settings"}
                If Not _pages.ContainsKey(key) Then Continue For
                ShowPage(key)
                Application.DoEvents()
                Size = New Size(1720, 1000)
                Application.DoEvents()

                Prof.Reset()
                Dim worstFrame As Long = 0
                Dim total As Long = 0
                ' 模拟真实拖动：拖动期间置位，页面按"冻结重排"路径跑；松手后再补一次完整重排
                BeginDragSnapshot()
                UiState.Begin()
                Dim swAll = Diagnostics.Stopwatch.StartNew()
                For i = 0 To 39
                    Dim w = 1720 - i * 15
                    Dim sw1 = Diagnostics.Stopwatch.StartNew()
                    Size = New Size(w, Math.Max(820, CInt(w * 0.62)))
                    Application.DoEvents()
                    sw1.Stop()
                    If sw1.ElapsedMilliseconds > worstFrame Then worstFrame = sw1.ElapsedMilliseconds
                    total += sw1.ElapsedMilliseconds
                Next
                swAll.Stop()
                UiState.Finish()
                Dim swEnd = Diagnostics.Stopwatch.StartNew()
                EndDragSnapshot()
                Dim appPage = TryCast(_pages(key), IAppPage)
                If appPage IsNot Nothing Then appPage.Relayout()
                Application.DoEvents()
                swEnd.Stop()
                sb.AppendLine($"  {key,-9} 拖动总 {swAll.ElapsedMilliseconds,5} ms  平均 {total / 40,4} ms/帧  最慢 {worstFrame,4} ms  松手重排 {swEnd.ElapsedMilliseconds,4} ms")
                sb.AppendLine(Prof.Dump())

                ' 归因实验：逐块隐藏 + 纯布局计时（不泵消息），最小值为准
                If key = "stamp" Then
                    Dim sp = TryCast(_pages(key), Pages.StampPage)
                    If sp IsNot Nothing Then
                        For Each t In sp.DiagTargets()
                            If t.Item2 Is Nothing Then Continue For
                            Dim oldVis = t.Item2.Visible
                            t.Item2.Visible = False
                            Dim best As Long = Long.MaxValue
                            For rep = 0 To 1
                                UiState.Begin()
                                Dim swX = Diagnostics.Stopwatch.StartNew()
                                For i = 0 To 9
                                    Size = New Size(1720 - i * 40, Math.Max(820, CInt((1720 - i * 40) * 0.62)))
                                Next
                                swX.Stop()
                                UiState.Finish()
                                Application.DoEvents()
                                If swX.ElapsedMilliseconds < best Then best = swX.ElapsedMilliseconds
                            Next
                            t.Item2.Visible = oldVis
                            Application.DoEvents()
                            sb.AppendLine($"      [纯布局] 隐藏 {t.Item1,-10} 10 次最快 {best,5} ms（{best \ 10,3} ms/次）")
                        Next
                    End If
                End If

                ' 实验：把页面里所有 AutoSize 的 TableLayoutPanel 临时关掉 AutoSize，看纯布局耗时变化
                If key = "stamp" Then
                    Dim tlps = FindControls(Of TableLayoutPanel)(_pages(key))
                    Dim changed As New List(Of TableLayoutPanel)()
                    For Each t In tlps
                        If t.AutoSize Then
                            t.AutoSize = False
                            changed.Add(t)
                        End If
                    Next
                    If changed.Count > 0 Then
                        Dim best2 As Long = Long.MaxValue
                        For rep = 0 To 1
                            UiState.Begin()
                            Dim swY = Diagnostics.Stopwatch.StartNew()
                            For i = 0 To 9
                                Size = New Size(1720 - i * 40, Math.Max(820, CInt((1720 - i * 40) * 0.62)))
                            Next
                            swY.Stop()
                            UiState.Finish()
                            Application.DoEvents()
                            If swY.ElapsedMilliseconds < best2 Then best2 = swY.ElapsedMilliseconds
                        Next
                        sb.AppendLine($"      [实验] 关掉 {changed.Count} 个 AutoSize 表格：10 次最快 {best2,5} ms（{best2 \ 10,3} ms/次）")
                        For Each t In changed
                            t.AutoSize = True
                        Next
                        Application.DoEvents()
                    End If
                End If

                If key = "stamp" Then
                    ' 先判别「布局 vs 绘制」：只改尺寸不泵消息，WinForms 不会真正重排/重绘
                    UiState.Begin()
                    Dim swNoPump = Diagnostics.Stopwatch.StartNew()
                    For i = 0 To 19
                        Size = New Size(1720 - i * 30, Math.Max(820, CInt((1720 - i * 30) * 0.62)))
                    Next
                    swNoPump.Stop()
                    Dim swPump = Diagnostics.Stopwatch.StartNew()
                    Application.DoEvents()
                    swPump.Stop()
                    UiState.Finish()
                    Application.DoEvents()
                    sb.AppendLine($"      [判别] 只改尺寸不泵消息 20 次：{swNoPump.ElapsedMilliseconds,5} ms" &
                                  $"（≈{swNoPump.ElapsedMilliseconds \ 20,3} ms/次，这是纯布局）")
                    sb.AppendLine($"      [判别] 之后一次 DoEvents：{swPump.ElapsedMilliseconds,5} ms（这是积压的绘制）")
                End If

                If key = "stamp" Then
                    Dim pageRoot = _pages(key)
                    Dim variants As New List(Of (String, Control))()
                    Dim g2 = FindControl(Of DataGridView)(pageRoot)
                    If g2 IsNot Nothing Then variants.Add(("DataGridView", g2))
                    Dim scs = FindControls(Of SplitContainer)(pageRoot)
                    For i = 0 To scs.Count - 1
                        variants.Add(($"SplitContainer#{i}", scs(i)))
                    Next
                    Dim rhs = FindControls(Of Panel)(pageRoot)
                    ' 只挑带 AutoScroll 的面板（右栏），它内部有卡片级联
                    For Each pn In rhs
                        If pn.AutoScroll Then variants.Add(("AutoScroll 面板", pn))
                    Next

                    For Each v In variants
                        If v.Item2 Is Nothing Then Continue For
                        Dim oldVis = v.Item2.Visible
                        v.Item2.Visible = False
                        Application.DoEvents()
                        Dim best As Long = Long.MaxValue
                        For rep = 0 To 2
                            UiState.Begin()
                            Size = New Size(1720, 1000)
                            Application.DoEvents()
                            Dim sw2 = Diagnostics.Stopwatch.StartNew()
                            For i = 0 To 19
                                Size = New Size(1720 - i * 30, Math.Max(820, CInt((1720 - i * 30) * 0.62)))
                                Application.DoEvents()
                            Next
                            sw2.Stop()
                            UiState.Finish()
                            If sw2.ElapsedMilliseconds < best Then best = sw2.ElapsedMilliseconds
                        Next
                        v.Item2.Visible = oldVis
                        Application.DoEvents()
                        sb.AppendLine($"      [归因] 隐藏 {v.Item1,-16} 20 帧最快 {best,5} ms（{best \ 20,4} ms/帧）")
                    Next
                End If
            Next
            Prof.Enabled = False

            Try
                IO.File.WriteAllText(outPath, sb.ToString(), System.Text.Encoding.UTF8)
            Catch
            End Try
            Close()
        End Sub

        ''' <summary>
        ''' --hovertest [输出txt]：用真实鼠标（移动光标）验证按钮悬停是否真的会变色。
        ''' 做法：窗口摆到屏幕内 → 光标移开拍一张 → 光标移到按钮中心拍一张 → 逐像素比对。
        ''' 这是唯一能证明「用户看到的悬停」成立的办法，纯代码驱动事件不算数。
        ''' </summary>
        Private Sub RunHoverTest()
            Dim outPath = ArgValue("--hovertest")
            If outPath = "" Then outPath = IO.Path.Combine(IO.Path.GetTempPath(), "hover.txt")
            Dim sb As New Text.StringBuilder()
            Dim oldPos = Cursor.Position
            Try
                StartPosition = FormStartPosition.Manual
                Bounds = New Rectangle(0, 0, 1280, 860)
                ' 必须真正前台 + 置顶：被别的窗口盖住时鼠标悬停事件根本不会派发到本窗口
                TopMost = True
                Activate()
                ShowPage("stamp")
                Application.DoEvents()
                PumpMs(600)

                Dim btns As New List(Of Button)()
                CollectButtons(_pages("stamp"), btns)
                sb.AppendLine($"stamp 页按钮 {btns.Count} 个，窗口 {Bounds.Width}x{Bounds.Height}")

                For Each want In New String() {"开始盖章", "添加文件"}
                    Dim b As Button = Nothing
                    For Each x In btns
                        If x.Text = want Then b = x : Exit For
                    Next
                    If b Is Nothing Then
                        sb.AppendLine($"  '{want}' 未找到")
                        Continue For
                    End If
                    ' DrawToBitmap 画的是整个窗口（含标题栏与边框），所以用「窗口左上角」为原点
                    Dim p0 = b.PointToScreen(Point.Empty)
                    Dim rel = New Rectangle(p0.X - Left, p0.Y - Top, b.Width, b.Height)
                    rel.Inflate(-4, -4)

                    Cursor.Position = New Point(Left + Width + 40, Top + 5)
                    PumpMs(420)
                    Dim offAvg = AvgColorOf(rel)

                    Dim c As Point = b.PointToScreen(New Point(b.Width \ 2, b.Height \ 2))
                    Cursor.Position = c
                    PumpMs(420)
                    Dim onAvg = AvgColorOf(rel)

                    Dim changed = If(offAvg Is Nothing OrElse onAvg Is Nothing, -1,
                                     CInt(Math.Abs(offAvg.Value.R - onAvg.Value.R) +
                                           Math.Abs(offAvg.Value.G - onAvg.Value.G) +
                                           Math.Abs(offAvg.Value.B - onAvg.Value.B)))
                    sb.AppendLine($"  '{want}' 常态 {CStr(If(offAvg.HasValue, offAvg.Value.ToString(), "?"))}" &
                                  $" -> 悬停 {CStr(If(onAvg.HasValue, onAvg.Value.ToString(), "?"))}" &
                                  $"  色差={changed}  BackColor={b.BackColor}  ticks={Anim.TickCount}")
                Next
            Catch ex As Exception
                sb.AppendLine("EX " & ex.ToString())
            End Try
            Cursor.Position = oldPos
            TopMost = False
            Try
                IO.File.WriteAllText(outPath, sb.ToString(), System.Text.Encoding.UTF8)
            Catch
            End Try
            Close()
        End Sub

        ''' <summary>把窗体当前外观画到位图，取指定矩形（客户区坐标）的平均色。</summary>
        Private Function AvgColorOf(rel As Rectangle) As Color?
            Try
                Using bmp As New Bitmap(Math.Max(1, Width), Math.Max(1, Height))
                    DrawToBitmap(bmp, New Rectangle(0, 0, bmp.Width, bmp.Height))
                    Dim r = Rectangle.Intersect(rel, New Rectangle(0, 0, bmp.Width, bmp.Height))
                    If r.Width < 4 OrElse r.Height < 4 Then Return Nothing
                    Dim sr = 0, sg = 0, sb2 = 0, n = 0
                    For y = r.Top To r.Bottom - 1 Step 2
                        For x = r.Left To r.Right - 1 Step 2
                            Dim p = bmp.GetPixel(x, y)
                            sr += p.R : sg += p.G : sb2 += p.B : n += 1
                        Next
                    Next
                    If n = 0 Then Return Nothing
                    Return Color.FromArgb(sr \ n, sg \ n, sb2 \ n)
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>
        ''' --animtest [输出txt]：验证高级设置页的淡入动画真的在跑。
        ''' 做法：切到设置页触发一次 Rebuild（含淡入），在 0 / 120 / 400ms 三个时刻
        ''' 采样参数区所有 Label 的平均亮度。亮度递减（越来越深）＝文字在浮现；
        ''' 三点都一样就是动画没生效——这种情况以前出现过（补间被静默作废）。
        ''' </summary>
        Private Sub RunAnimTest()
            Dim outPath = ArgValue("--animtest")
            If outPath = "" Then outPath = IO.Path.Combine(IO.Path.GetTempPath(), "anim.txt")
            Dim sb As New Text.StringBuilder()
            Try
                StartPosition = FormStartPosition.Manual
                Bounds = New Rectangle(0, 0, 1480, 900)
                ShowPage("stamp")
                Application.DoEvents()
                PumpMs(300)

                ShowPage("settings")          ' 这里会触发 Rebuild → FadeInRows
                Dim l0 = SampleLabelLum()
                PumpMs(120)
                Dim l1 = SampleLabelLum()
                PumpMs(320)
                Dim l2 = SampleLabelLum()

                sb.AppendLine($"设置页参数区文字平均亮度： 0ms={l0:F1}  120ms={l1:F1}  400ms={l2:F1}")
                sb.AppendLine($"淡入生效={l1 < l0 AndAlso l2 <= l1 + 1}（应看到亮度递减后稳定）")

                ' 保存按钮反馈：点一下，看按钮底色是否短暂变绿
                Dim btn As Button = Nothing
                For Each b As Button In FindButtons(_settingsPage)
                    If b.Text = "保存设置" Then btn = b : Exit For
                Next
                If btn IsNot Nothing Then
                    Dim before = btn.BackColor
                    btn.PerformClick()
                    PumpMs(120)
                    Dim mid = btn.BackColor
                    Dim midText = btn.Text
                    PumpMs(1400)
                    sb.AppendLine($"保存按钮：常态 {Hex(before.ToArgb())} → 反馈 {Hex(mid.ToArgb())}（文字「{midText}」）→ 复原 {Hex(btn.BackColor.ToArgb())}（文字「{btn.Text}」）")
                    sb.AppendLine($"反馈生效={before.ToArgb() <> mid.ToArgb()}")
                End If
            Catch ex As Exception
                sb.AppendLine("EX " & ex.ToString())
            End Try
            Try
                IO.File.WriteAllText(outPath, sb.ToString(), System.Text.Encoding.UTF8)
            Catch
            End Try
            Close()
        End Sub

        ''' <summary>参数区所有可见 Label 的文字颜色平均亮度（0=黑，255=白）。</summary>
        Private Function SampleLabelLum() As Double
            Dim sum As Double = 0
            Dim n As Integer = 0
            For Each l As Label In FindLabels(_settingsPage)
                If Not l.Visible OrElse String.IsNullOrWhiteSpace(l.Text) Then Continue For
                Dim c = l.ForeColor
                sum += 0.299 * c.R + 0.587 * c.G + 0.114 * c.B
                n += 1
            Next
            Return If(n = 0, -1.0, sum / n)
        End Function

        Private Iterator Function FindLabels(root As Control) As IEnumerable(Of Label)
            For Each ch As Control In root.Controls
                Dim l = TryCast(ch, Label)
                If l IsNot Nothing Then Yield l
                If ch.Controls.Count > 0 Then
                    For Each x In FindLabels(ch)
                        Yield x
                    Next
                End If
            Next
        End Function

        Private Iterator Function FindButtons(root As Control) As IEnumerable(Of Button)
            For Each ch As Control In root.Controls
                Dim b = TryCast(ch, Button)
                If b IsNot Nothing Then Yield b
                If ch.Controls.Count > 0 Then
                    For Each x In FindButtons(ch)
                        Yield x
                    Next
                End If
            Next
        End Function

        ''' <summary>泵消息若干毫秒：动画定时器要靠消息循环才会跑。</summary>
        Private Sub PumpMs(ms As Integer)
            Dim sw = Diagnostics.Stopwatch.StartNew()
            Do While sw.ElapsedMilliseconds < ms
                Application.DoEvents()
                Threading.Thread.Sleep(5)
            Loop
        End Sub

        Private Sub CollectButtons(c As Control, into As List(Of Button))
            For Each ch As Control In c.Controls
                Dim b = TryCast(ch, Button)
                If b IsNot Nothing AndAlso b.Visible Then into.Add(b)
                If ch.Controls.Count > 0 Then CollectButtons(ch, into)
            Next
        End Sub

        ''' <summary>
        ''' --audit [输出文件] [--size 宽x高] [--probe]：逐页跑布局自检，把文字截断 / 控件重叠 / 越界 / 横向溢出写成清单。
        ''' 这是给「改完界面自己先量一遍」用的，不参与正常流程。
        ''' </summary>
        ''' <summary>--uidump [txt]：导出所有数字框的实际尺寸，用于对比主题差异。</summary>
        Private Sub DumpNumberBoxes()
            Dim outPath = ArgValue("--uidump")
            If outPath = "" Then outPath = IO.Path.Combine(IO.Path.GetTempPath(), "uidump.txt")
            Dim sb As New Text.StringBuilder()
            sb.AppendLine($"dark={Theme.IsDark}  uiFont={Theme.FontBase.SizeInPoints}")
            Try
                For Each c In AllControls(Me)
                    If TypeOf c Is NumericUpDown Then
                        Dim n = DirectCast(c, NumericUpDown)
                        Dim edit As Control = Nothing
                        For Each ch As Control In n.Controls
                            If TypeOf ch Is TextBox Then edit = ch
                        Next
                        sb.AppendLine($"NUM w={n.Width} client={n.ClientSize.Width} " &
                                      $"editW={If(edit Is Nothing, -1, edit.Width)} " &
                                      $"editClient={If(edit Is Nothing, -1, edit.ClientSize.Width)} " &
                                      $"font={n.Font.SizeInPoints} text='{n.Text}'")
                    End If
                Next
            Catch ex As Exception
                sb.AppendLine("EX " & ex.Message)
            End Try
            Try
                IO.File.WriteAllText(outPath, sb.ToString(), System.Text.Encoding.UTF8)
            Catch
            End Try
            Close()
        End Sub

        Private Iterator Function AllControls(root As Control) As IEnumerable(Of Control)
            For Each c As Control In root.Controls
                Yield c
                If c.Controls.Count > 0 Then
                    For Each x In AllControls(c)
                        Yield x
                    Next
                End If
            Next
        End Function

        Private Sub RunAuditIfRequested()
            If _startupArgs Is Nothing Then Return
            If Array.IndexOf(_startupArgs, "--uidump") >= 0 Then
                DumpNumberBoxes()
                Return
            End If
            If Array.IndexOf(_startupArgs, "--animtest") >= 0 Then
                RunAnimTest()
                Return
            End If
            If Array.IndexOf(_startupArgs, "--hovertest") >= 0 Then
                RunHoverTest()
                Return
            End If
            Dim dsIdx = Array.FindIndex(_startupArgs, Function(a) String.Equals(a, "--dragshot", StringComparison.OrdinalIgnoreCase))
            If dsIdx >= 0 Then
                Dim outFile = If(dsIdx + 1 < _startupArgs.Length, _startupArgs(dsIdx + 1), IO.Path.Combine(IO.Path.GetTempPath(), "drag.png"))
                RunDragShot(outFile)
                Return
            End If
            If Array.IndexOf(_startupArgs, "--sweepfine") >= 0 Then
                RunSweepFine()
                Return
            End If
            If Array.IndexOf(_startupArgs, "--sweep") >= 0 Then
                RunSweep()
                Return
            End If
            Dim ai = Array.FindIndex(_startupArgs, Function(a) String.Equals(a, "--audit", StringComparison.OrdinalIgnoreCase))
            If ai < 0 Then Return

            Dim outPath = ""
            If ai + 1 < _startupArgs.Length AndAlso Not _startupArgs(ai + 1).StartsWith("--") Then outPath = _startupArgs(ai + 1)
            If outPath = "" Then outPath = IO.Path.Combine(IO.Path.GetTempPath(), "symbolprint-audit.txt")

            ' 指定尺寸，用来复现「窗口一缩小就截断」的场景
            Dim sizeArg = ArgValue("--size")
            Dim targetW As Integer = 0, targetH As Integer = 0
            If sizeArg <> "" Then
                Dim xy = sizeArg.ToLowerInvariant().Split("x"c)
                If xy.Length = 2 Then
                    If Integer.TryParse(xy(0), targetW) AndAlso Integer.TryParse(xy(1), targetH) Then
                        ' 先放大再缩到目标尺寸：这才是真实使用路径（打开时窗口大，之后被缩小）
                        Size = New Size(Math.Max(MinimumSize.Width, 1600), Math.Max(MinimumSize.Height, 1000))
                        Application.DoEvents()
                        Size = New Size(Math.Max(MinimumSize.Width, targetW), Math.Max(MinimumSize.Height, targetH))
                        Application.DoEvents()
                    End If
                End If
            End If

            Dim sb As New Text.StringBuilder()
            sb.AppendLine($"SymbolPrint 布局自检  {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            sb.AppendLine($"窗口 {Width}x{Height}  内容区 {_content.ClientSize.Width}x{_content.ClientSize.Height}")
            ' 把「支持的文件」过滤器原样打出来：.bin 是否出现在这里，直接决定用户能不能选中章文件
            sb.AppendLine("支持扩展名：" & String.Join(" ", SymbolPrint.Core.Service.CarrierRegistry.SupportedExtensions()))

            ' --probe：把每个可滚动容器的真实状态打出来（ClientSize / DisplayRectangle / 子控件最大右边 / 滚动条可见性），
            ' 用来回答「滚动条为什么会出现」，避免靠看截图猜。
            If ArgValue("--probe") <> "" OrElse Array.IndexOf(_startupArgs, "--probe") >= 0 Then
                sb.AppendLine()
                sb.AppendLine("---- 滚动容器探针 ----")
                For Each line In UiAudit.Probe(Me)
                    sb.AppendLine("  " & line)
                Next
            End If

            For Each key In New String() {"stamp", "verify", "make", "settings"}
                If Not _pages.ContainsKey(key) Then Continue For
                ShowPage(key)
                PerformLayout()
                Application.DoEvents()
                Dim issues = UiAudit.Run(Me)
                sb.AppendLine()
                sb.AppendLine($"=== {key}  问题 {issues.Count} 项 ===")
                For Each line In issues
                    sb.AppendLine("  " & line)
                Next
                If ArgValue("--probe") <> "" OrElse Array.IndexOf(_startupArgs, "--probe") >= 0 Then
                    For Each line In UiAudit.Probe(Me)
                        sb.AppendLine("  [probe] " & line)
                    Next
                End If
            Next

            Try
                IO.File.WriteAllText(outPath, sb.ToString(), System.Text.Encoding.UTF8)
            Catch
            End Try
            Close()
        End Sub

        ''' <summary>
        ''' 支持「右键 / 拖到 exe 上 / 命令行」直接验证：SymbolPrint.exe &lt;文件&gt; [章.bin]
        ''' 这是文件关联的自然入口，也用于自动化测试。
        ''' </summary>
        Private Function ArgValue(name As String) As String
            Dim i = Array.FindIndex(_startupArgs, Function(a) String.Equals(a, name, StringComparison.OrdinalIgnoreCase))
            If i >= 0 AndAlso i + 1 < _startupArgs.Length Then Return _startupArgs(i + 1)
            Return ""
        End Function

        Private Sub HandleStartupArgs()
            If _startupArgs Is Nothing OrElse _startupArgs.Length = 0 Then Return

            ' --stamp <文件/目录>... --seal <章.bin> [--pass <口令>] [--mark <svg|png>]：预填盖章页
            Dim si = Array.FindIndex(_startupArgs, Function(a) String.Equals(a, "--stamp", StringComparison.OrdinalIgnoreCase))
            If si >= 0 Then
                Dim fileList As New List(Of String)()
                Dim i = si + 1
                While i < _startupArgs.Length AndAlso Not _startupArgs(i).StartsWith("--")
                    If IO.File.Exists(_startupArgs(i)) Then
                        fileList.Add(_startupArgs(i))
                    ElseIf IO.Directory.Exists(_startupArgs(i)) Then
                        fileList.AddRange(IO.Directory.GetFiles(_startupArgs(i)))
                    End If
                    i += 1
                End While
                ShowPage("stamp")
                _stampPage.PrefillFiles(fileList, ArgValue("--seal"), ArgValue("--pass"), ArgValue("--mark"))

                ' --visual：预填完直接打开全屏可视化盖章（截图 / 冒烟测试用）
                If _startupArgs.Any(Function(a) String.Equals(a, "--visual", StringComparison.OrdinalIgnoreCase)) Then
                    BeginInvoke(New Action(Sub() _stampPage.OpenVisualSeal()))
                End If
                Return
            End If

            ' --page <stamp|verify|make|settings>：直接打开指定页（便于演示与自动化检查）
            Dim pi = Array.FindIndex(_startupArgs, Function(a) String.Equals(a, "--page", StringComparison.OrdinalIgnoreCase))
            If pi >= 0 AndAlso pi + 1 < _startupArgs.Length Then ShowPage(_startupArgs(pi + 1).ToLowerInvariant())

            ' --make-gen：做章页直接选中「程序生成」并生成一份（截图 / 冒烟测试用）
            If _startupArgs.Any(Function(a) String.Equals(a, "--make-gen", StringComparison.OrdinalIgnoreCase)) Then
                ShowPage("make")
                _makePage.ShowGeneratedSource()
            End If

            ' --editor：直接打开图案编辑器（空画布）
            Dim ei = Array.FindIndex(_startupArgs, Function(a) String.Equals(a, "--editor", StringComparison.OrdinalIgnoreCase))
            If ei >= 0 Then
                Dim script = ""
                If ei + 1 < _startupArgs.Length AndAlso Not _startupArgs(ei + 1).StartsWith("--") Then script = _startupArgs(ei + 1)
                Using f As New SvgEditorForm("")
                    f.AutoScript = script
                    Dim r = f.ShowDialog(Me)
                    ' 编辑器产出的图案直接带到做章页，省一次点选
                    If r = DialogResult.OK AndAlso Not String.IsNullOrEmpty(f.ResultSvg) Then
                        _makePage.SvgMarkup = f.ResultSvg
                        ShowPage("make")
                    End If
                End Using
                Return
            End If
            Dim files = _startupArgs.Where(Function(a) Not a.StartsWith("--") AndAlso IO.File.Exists(a)).ToArray()
            If files.Length = 0 Then Return

            Dim target = files(0)
            Dim sealFile = ""
            If files.Length > 1 Then sealFile = files(1)
            ShowPage("verify")
            _verifyPage.Prefill(target, sealFile)
        End Sub

        Private Function BuildHeader() As Control
            ' 标题 + 副标题 + 版本号：用表格排，行高跟着字体走；
            ' 以前是「绝对坐标 + Resize 里改 Left」，标题和副标题会互相压住。
            Dim h As New Panel() With {.Dock = DockStyle.Top, .BackColor = Theme.Card}
            Dim bar As New Panel() With {.Dock = DockStyle.Bottom, .Height = 1, .BackColor = Theme.Border}

            Dim titleFont As New Font("Microsoft YaHei UI", 13.0F, FontStyle.Bold)
            Dim titleH = TextRenderer.MeasureText("Ag", titleFont).Height
            h.Height = titleH + Theme.LineH + 26

            Dim row As New TableLayoutPanel() With {
                .Dock = DockStyle.Fill, .ColumnCount = 2, .RowCount = 1,
                .BackColor = Theme.Card, .Padding = New Padding(20, 8, 20, 6)
            }
            row.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            row.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))

            Dim texts As New TableLayoutPanel() With {
                .Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 2, .BackColor = Theme.Card,
                .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink, .Margin = New Padding(0)
            }
            texts.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            texts.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            texts.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            texts.Controls.Add(New Label() With {
                .Text = "SymbolPrint", .Font = titleFont, .ForeColor = Theme.TextMain,
                .AutoSize = True, .Margin = New Padding(0)
            }, 0, 0)
            texts.Controls.Add(New Label() With {
                .Text = "电子盖章 · 原始二进制 + 可验证", .Font = Theme.FontBase,
                .ForeColor = Theme.TextSub, .AutoSize = True, .Margin = New Padding(2, 0, 0, 0)
            }, 0, 1)

            Dim ver As String = "v" & Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3)
            Dim verLabel As New Label() With {
                .Text = ver, .Font = Theme.FontBase, .ForeColor = Theme.TextFaint,
                .AutoSize = False, .Dock = DockStyle.Fill, .TextAlign = ContentAlignment.TopRight,
                .Width = 80, .Margin = New Padding(8, 2, 0, 0)
            }

            row.Controls.Add(texts, 0, 0)
            row.Controls.Add(verLabel, 1, 0)
            h.Controls.Add(row)
            h.Controls.Add(bar)
            Return h
        End Function

        Private Function BuildNav() As Panel
            Dim p As New Panel() With {.Dock = DockStyle.Left, .Width = 176, .BackColor = Theme.Card, .Padding = New Padding(0, 10, 0, 0)}
            Dim right As New Panel() With {.Dock = DockStyle.Right, .Width = 1, .BackColor = Theme.Border}

            Dim items As (Key As String, Text As String, Hint As String)() = {
                ("stamp", "盖章", "把章写入文件"),
                ("verify", "验章", "检查文件与来源"),
                ("make", "做章", "生成 .bin 章文件"),
                ("settings", "高级设置", "参数与默认值")
            }

            Dim top As Integer = 8
            For Each it In items
                Dim ni As New NavItem(it.Key, it.Text, it.Hint)
                ni.Top = top
                ni.Left = 8
                ni.Width = 160
                top += 52
                AddHandler ni.Clicked, Sub(s, e) ShowPage(ni.Key)
                _navItems.Add(ni)
                p.Controls.Add(ni)
            Next

            ' 底部提示：当前章
            Dim sealInfo As New Label() With {
                .AutoSize = False,
                .Height = 56,
                .Width = 160,
                .Left = 8,
                .Dock = DockStyle.None,
                .Anchor = AnchorStyles.Bottom Or AnchorStyles.Left,
                .ForeColor = Theme.TextFaint,
                .Font = New Font("Microsoft YaHei UI", 8.0F),
                .Text = "当前章：未选择"
            }
            AddHandler p.Resize, Sub(s, e) sealInfo.Top = p.Height - 70
            sealInfo.Top = p.Height - 70
            AddHandler SealChanged, Sub(s, e)
                                        If CurrentSeal Is Nothing Then
                                            sealInfo.Text = "当前章：未选择"
                                        Else
                                            sealInfo.Text = "当前章：" & CurrentSeal.Label &
                                                            Environment.NewLine & CurrentSeal.StampIdShort &
                                                            If(CurrentSeal.IsLocked, "（已加密）", "")
                                        End If
                                    End Sub

            p.Controls.Add(sealInfo)
            p.Controls.Add(right)
            Return p
        End Function

        Private Sub BuildPages()
            _stampPage = New StampPage(Me) With {.Dock = DockStyle.Fill, .Visible = False}
            _verifyPage = New VerifyPage(Me) With {.Dock = DockStyle.Fill, .Visible = False}
            _makePage = New MakeSealPage(Me) With {.Dock = DockStyle.Fill, .Visible = False}
            _settingsPage = New SettingsPage(Me) With {.Dock = DockStyle.Fill, .Visible = False}

            _pages("stamp") = _stampPage
            _pages("verify") = _verifyPage
            _pages("make") = _makePage
            _pages("settings") = _settingsPage

            For Each pg In _pages.Values
                _content.Controls.Add(pg)
            Next
        End Sub

        Friend Sub ShowPage(key As String)
            If Not _pages.ContainsKey(key) Then Return
            _current = key
            For Each pg In _pages.Values
                pg.Visible = False
            Next
            _pages(key).Visible = True
            _pages(key).BringToFront()

            For Each ni In _navItems
                ni.Selected = String.Equals(ni.Key, key, StringComparison.OrdinalIgnoreCase)
            Next

            Dim appPage = TryCast(_pages(key), IAppPage)
            If appPage IsNot Nothing Then appPage.OnShown()
        End Sub

        ''' <summary>
        ''' 拖动缩放的生命周期钩子：进入时冻结「按比例重算分隔条 / 重排参数行」，退出时统一补一次。
        ''' 这是解决「拖动时界面元素被裁断 + 卡」的关键——卡是因为每帧都在整树重排，裁断是因为
        ''' 重排没跑完就被绘制，看到的是中间状态。
        ''' </summary>
        Private Const WM_ENTERSIZEMOVE As Integer = &H231
        Private Const WM_EXITSIZEMOVE As Integer = &H232

        Protected Overrides Sub WndProc(ByRef m As Message)
            If m.Msg = WM_ENTERSIZEMOVE Then
                UiState.Begin()
                BeginDragSnapshot()
            ElseIf m.Msg = WM_EXITSIZEMOVE Then
                ' 顺序：先撤快照把真实页面显示出来，再重排。
                ' 不能反过来——隐藏（Visible=False）的控件不会被 WinForms 真正布局，
                ' 先重排再显示会得到「右栏超出窗口、文字被切」的错误状态（实测复现）。
                EndDragSnapshot()
                Dim cur = TryCast(If(_pages.ContainsKey(_current), _pages(_current), Nothing), IAppPage)
                If cur IsNot Nothing Then
                    Try
                        cur.Relayout()
                    Catch
                    End Try
                End If
                PerformLayout()
                UiState.Finish()
            End If
            MyBase.WndProc(m)
        End Sub

        ''' <summary>
        ''' 拖动期间用「快照」代替实时布局。
        '''
        ''' 为什么要这样：实测窗口缩放的纯布局耗时 ~100ms/次（全树布局分摊，没有单点），
        ''' 一帧 100ms 意味着拖动时用户看到的都是「布局没跑完的中间状态」——右侧卡片被窗口边缘
        ''' 横切成半句话就是这么来的（见 .build/ui/drag_mid.png）。控件数量和嵌套决定了这个
        ''' 耗时短期内降不到 16ms，所以拖动时干脆不布局：把当前页面拍成一张位图贴在原位，
        ''' 让它随窗口缩放（Zoom 保持比例），松手后再恢复真实控件并重排一次。
        ''' 代价是拖动中画面略糊，但不会再出现半截文字。
        ''' </summary>
        Private Sub BeginDragSnapshot()
            If _current = "" OrElse Not _pages.ContainsKey(_current) Then Return
            Dim pg = _pages(_current)
            If pg.Width < 40 OrElse pg.Height < 40 Then Return
            Try
                Dim bmp As New Bitmap(pg.Width, pg.Height)
                pg.DrawToBitmap(bmp, New Rectangle(0, 0, pg.Width, pg.Height))
                _dragShot = bmp
                _dragOverlay = New PictureBox() With {
                    .Dock = DockStyle.Fill,
                    .SizeMode = PictureBoxSizeMode.Zoom,
                    .BackColor = Theme.Bg,
                    .Image = bmp
                }
                _content.Controls.Add(_dragOverlay)
                _dragOverlay.BringToFront()
                pg.Visible = False
            Catch
                EndDragSnapshot()
            End Try
        End Sub

        Private Sub EndDragSnapshot()
            If _dragOverlay Is Nothing Then Return
            Try
                _content.Controls.Remove(_dragOverlay)
                _dragOverlay.Dispose()
            Catch
            End Try
            _dragOverlay = Nothing
            If _dragShot IsNot Nothing Then
                _dragShot.Dispose()
                _dragShot = Nothing
            End If
            If _current <> "" AndAlso _pages.ContainsKey(_current) Then _pages(_current).Visible = True
        End Sub

        ''' <summary>在控件树里找第一个指定类型的控件（诊断用）。</summary>
        Private Shared Function FindControl(Of T As Control)(root As Control) As T
            For Each c As Control In root.Controls
                Dim hit = TryCast(c, T)
                If hit IsNot Nothing Then Return hit
                Dim deep = FindControl(Of T)(c)
                If deep IsNot Nothing Then Return deep
            Next
            Return Nothing
        End Function

        ''' <summary>在控件树里收集所有指定类型的控件（诊断用）。</summary>
        Private Shared Function FindControls(Of T As Control)(root As Control) As List(Of T)
            Dim out As New List(Of T)()
            Collect(root, out)
            Return out
        End Function

        Private Shared Sub Collect(Of T As Control)(root As Control, out As List(Of T))
            For Each c As Control In root.Controls
                Dim hit = TryCast(c, T)
                If hit IsNot Nothing Then out.Add(hit)
                Collect(c, out)
            Next
        End Sub

        ''' <summary>切到某页并触发刷新（供"从这里去那边"的跳转用）。</summary>
        Friend Sub NavigateTo(key As String)
            ShowPage(key)
        End Sub

        ''' <summary>设置当前章，并广播。</summary>
        Friend Sub SetSeal(path As String, seal As StampFile)
            SealPath = path
            CurrentSeal = seal
            RaiseEvent SealChanged(Me, EventArgs.Empty)
        End Sub

        ''' <summary>跨页面提示（统一放在状态栏/MB 风格）。</summary>
        Friend Sub Notify(text As String, Optional kind As NotifyKind = NotifyKind.Info)
            Select Case kind
                Case NotifyKind.Ok : UiLog.Write("[OK] " & text)
                Case NotifyKind.Warn : UiLog.Write("[WARN] " & text)
                Case NotifyKind.Failed : UiLog.Write("[ERROR] " & text)
                Case Else : UiLog.Write("[INFO] " & text)
            End Select
        End Sub

        Private Sub MainForm_DragEnter(sender As Object, e As DragEventArgs) Handles Me.DragEnter
            ' 页面自己处理拖放；这里只拦一下防止默认行为
        End Sub

    End Class

    Friend Enum NotifyKind
        [Info]
        [Ok]
        [Warn]
        Failed
    End Enum

    ''' <summary>页面统一接口：每次被切到前台时调用，用于刷新依赖外部状态的内容。</summary>
    ''' <summary>能取消正在进行的耗时操作（ESC 键用）。</summary>
    Friend Interface ICancelablePage
        ''' <summary>有操作在跑就取消并返回 True；闲着返回 False。</summary>
        Function CancelCurrent() As Boolean
    End Interface

    Friend Interface IAppPage
        Sub OnShown()
        ''' <summary>窗口尺寸变化结束后，重新算一遍页面内部布局（拖动期间被冻结的那部分）。</summary>
        Sub Relayout()
    End Interface

    ''' <summary>左侧导航项：选中时浅蓝底 + 左侧色条 + 主色文字。</summary>
    Friend NotInheritable Class NavItem
        Inherits Panel

        Private ReadOnly _text As String
        Private ReadOnly _hint As String
        Private _selected As Boolean
        Private _hover As Boolean
        ' 底色补间：鼠标扫过 / 切页时渐变，不硬切
        Private _bgCur As Color = Theme.Card
        Private _barT As Double = 0.0

        Friend Event Clicked As EventHandler

        Friend ReadOnly Property Key As String

        Friend Sub New(key As String, text As String, hint As String)
            Me.Key = key
            _text = text
            _hint = hint
            Height = 46
            Cursor = Cursors.Hand
            BackColor = Theme.Card
            DoubleBuffered = True

            AddHandler MouseEnter, Sub(s, e)
                                       _hover = True : AnimateBg()
                                   End Sub
            AddHandler MouseLeave, Sub(s, e)
                                       _hover = False : AnimateBg()
                                   End Sub
        End Sub

        Friend Property Selected As Boolean
            Get
                Return _selected
            End Get
            Set(value As Boolean)
                If _selected = value Then Return
                _selected = value
                AnimateBg()
                Dim from = _barT
                Dim [to] = If(value, 1.0, 0.0)
                Anim.RunFor(Me, 180, Sub(t)
                                            _barT = from + ([to] - from) * Anim.EaseOut(t)
                                            Invalidate()
                                        End Sub)
            End Set
        End Property

        Private Sub AnimateBg()
            Dim target = If(_selected, Theme.AccentLight, If(_hover, Color.FromArgb(247, 249, 252), Theme.Card))
            Dim from = _bgCur
            Anim.RunFor(Me, 140, Sub(t)
                                        _bgCur = Anim.Blend(from, target, Anim.EaseOut(t))
                                        Invalidate()
                                    End Sub)
        End Sub

        Protected Overrides Sub OnClick(e As EventArgs)
            MyBase.OnClick(e)
            RaiseEvent Clicked(Me, EventArgs.Empty)
        End Sub

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            Dim g = e.Graphics
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.ClearTypeGridFit

            Using b As New SolidBrush(_bgCur)
                Using path = Theme.RoundRect(New Rectangle(0, 0, Width - 1, Height - 1), 6)
                    g.FillPath(b, path)
                End Using
            End Using

            If _barT > 0.01 Then
                ' 指示条也跟着渐显：切换页面时不会「啪」地跳出来
                Dim inset = CInt((Height - 20) * (1.0 - _barT) / 2)
                Using b As New SolidBrush(Color.FromArgb(CInt(255 * _barT), Theme.Accent))
                    g.FillRectangle(b, 0, 10 + inset, 3, Math.Max(1, CInt((Height - 20) * _barT)))
                End Using
            End If

            Using f As New Font("Microsoft YaHei UI", 10.0F, If(_selected, FontStyle.Bold, FontStyle.Regular))
                Using b As New SolidBrush(If(_selected, Theme.Accent, Theme.TextMain))
                    g.DrawString(_text, f, b, 16, 6)
                End Using
            End Using
            Using b As New SolidBrush(Theme.TextFaint)
                g.DrawString(_hint, New Font("Microsoft YaHei UI", 7.5F), b, 17, 27)
            End Using
        End Sub
    End Class

End Namespace
