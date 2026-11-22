Imports System
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.IO
Imports System.Text
Imports System.Windows.Forms

Namespace App

    ''' <summary>
    ''' 统一视觉：颜色、字体、圆角、常用控件工厂。所有页面只从这里取样式，避免各页自己调色导致不一致。
    ''' </summary>
    Friend Module Theme

        ''' <summary>
        ''' 程序图标：从 exe 自身资源里取（vbproj 的 ApplicationIcon 会把它编进 exe），
        ''' 这样标题栏、任务栏、资源管理器三处永远是同一枚图标，不会各写一份路径再走丢。
        ''' 取不到时退回系统图标，保证不会因为图标问题启动失败。
        ''' </summary>
        Friend Function AppIcon() As Icon
            Try
                Dim exe = Application.ExecutablePath
                If exe IsNot Nothing AndAlso File.Exists(exe) Then
                    Dim ic = Icon.ExtractAssociatedIcon(exe)
                    If ic IsNot Nothing Then Return ic
                End If
            Catch
            End Try
            Return SystemIcons.Shield
        End Function

        ' ---- 配色 ----
        ' 这些不是 ReadOnly：「界面 → 主题」允许在启动时选浅色/深色/跟随系统。
        ' 和字号一样，必须在创建任何窗体之前应用；运行中切换需要重启（自绘控件颜色已固化）。
        Friend Bg As Color = Color.FromArgb(244, 246, 249)
        Friend Card As Color = Color.White
        Friend Border As Color = Color.FromArgb(223, 227, 234)
        Friend BorderStrong As Color = Color.FromArgb(198, 204, 214)
        Friend TextMain As Color = Color.FromArgb(28, 32, 38)
        Friend TextSub As Color = Color.FromArgb(108, 117, 128)
        Friend TextFaint As Color = Color.FromArgb(152, 160, 172)

        Friend Accent As Color = Color.FromArgb(37, 99, 215)
        Friend AccentHover As Color = Color.FromArgb(28, 82, 186)
        Friend AccentLight As Color = Color.FromArgb(234, 241, 253)

        ' 状态色：涨红跌绿不适用，这里是"通过/警告/失败"
        Friend Ok As Color = Color.FromArgb(23, 138, 88)
        Friend OkLight As Color = Color.FromArgb(232, 246, 238)
        Friend Warn As Color = Color.FromArgb(176, 106, 4)
        Friend WarnLight As Color = Color.FromArgb(255, 246, 228)
        Friend Danger As Color = Color.FromArgb(190, 48, 48)
        Friend DangerLight As Color = Color.FromArgb(253, 236, 236)
        Friend InfoLight As Color = Color.FromArgb(238, 243, 250)
        ''' <summary>只读输入框/状态块的底色：比卡片略深一点，和可编辑输入区分开。</summary>
        Friend InputBg As Color = Color.FromArgb(250, 251, 253)
        ''' <summary>输入框聚焦时的底色。</summary>
        Friend InputBgFocus As Color = Color.White
        ''' <summary>表头底色。</summary>
        Friend HeaderBg As Color = Color.FromArgb(247, 249, 252)

        ''' <summary>当前是否深色（供个别需要判断明暗的地方用）。</summary>
        Friend IsDark As Boolean = False

        ''' <summary>
        ''' 切换到深色/浅色配色。必须在创建窗体之前调用一次。
        ''' 深色不是把浅色反相：底色用中性灰蓝（避免纯黑割裂），
        ''' 文字不用纯白（长文阅读刺眼），强调色提亮以保证在深底上的对比度。
        ''' </summary>
        Friend Sub ApplyTheme(dark As Boolean)
            IsDark = dark
            If dark Then
                Bg = Color.FromArgb(24, 26, 30)
                Card = Color.FromArgb(32, 35, 41)
                Border = Color.FromArgb(58, 62, 70)
                BorderStrong = Color.FromArgb(80, 86, 96)
                TextMain = Color.FromArgb(232, 235, 240)
                TextSub = Color.FromArgb(172, 179, 190)
                TextFaint = Color.FromArgb(126, 134, 146)

                Accent = Color.FromArgb(84, 150, 255)
                AccentHover = Color.FromArgb(112, 170, 255)
                AccentLight = Color.FromArgb(40, 56, 82)

                Ok = Color.FromArgb(72, 196, 140)
                OkLight = Color.FromArgb(30, 60, 46)
                Warn = Color.FromArgb(232, 168, 72)
                WarnLight = Color.FromArgb(64, 52, 30)
                Danger = Color.FromArgb(238, 106, 106)
                DangerLight = Color.FromArgb(72, 38, 38)
                InfoLight = Color.FromArgb(38, 48, 64)
                InputBg = Color.FromArgb(40, 44, 52)
                InputBgFocus = Color.FromArgb(52, 57, 66)
                HeaderBg = Color.FromArgb(38, 41, 48)
            Else
                Bg = Color.FromArgb(244, 246, 249)
                Card = Color.White
                Border = Color.FromArgb(223, 227, 234)
                BorderStrong = Color.FromArgb(198, 204, 214)
                TextMain = Color.FromArgb(28, 32, 38)
                TextSub = Color.FromArgb(108, 117, 128)
                TextFaint = Color.FromArgb(152, 160, 172)

                Accent = Color.FromArgb(37, 99, 215)
                AccentHover = Color.FromArgb(28, 82, 186)
                AccentLight = Color.FromArgb(234, 241, 253)

                Ok = Color.FromArgb(23, 138, 88)
                OkLight = Color.FromArgb(232, 246, 238)
                Warn = Color.FromArgb(176, 106, 4)
                WarnLight = Color.FromArgb(255, 246, 228)
                Danger = Color.FromArgb(190, 48, 48)
                DangerLight = Color.FromArgb(253, 236, 236)
                InfoLight = Color.FromArgb(238, 243, 250)
                InputBg = Color.FromArgb(250, 251, 253)
                InputBgFocus = Color.White
                HeaderBg = Color.FromArgb(247, 249, 252)
            End If
        End Sub

        ''' <summary>读注册表的「应用使用浅色主题」判断系统当前是不是深色。</summary>
        Friend Function SystemPrefersDark() As Boolean
            Try
                Using k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        "Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
                    If k IsNot Nothing Then
                        Dim v = k.GetValue("AppsUseLightTheme")
                        If v IsNot Nothing Then Return Convert.ToInt32(v) = 0
                    End If
                End Using
            Catch
            End Try
            Return False
        End Function

        ' 这些不是 ReadOnly：「界面 → 字号」允许在启动时改一次，
        ' 必须在创建任何窗体之前调 ApplyFontSize，否则已有控件仍持有旧字体对象。
        Friend FontBase As Font = New Font("Microsoft YaHei UI", 9.0F)
        Friend FontBold As Font = New Font("Microsoft YaHei UI", 9.0F, FontStyle.Bold)
        Friend FontTitle As Font = New Font("Microsoft YaHei UI", 15.0F, FontStyle.Bold)
        Friend FontH2 As Font = New Font("Microsoft YaHei UI", 11.0F, FontStyle.Bold)
        Friend FontMono As Font = New Font("Consolas", 9.0F)

        ' ---- 尺寸（全部由字体实测推出来，不写死像素）----
        ' 原因：DPI 放大时字体会跟着变大，但如果布局里写死 18px 行高、108px 列宽，
        ' 文字就会顶着边被切。凡是「装文字」的尺寸，都必须从这里取。
        Friend LineH As Integer = TextRenderer.MeasureText("测Ag", FontBase).Height
        Friend LineH2 As Integer = TextRenderer.MeasureText("测Ag", FontH2).Height
        Friend LineHSmall As Integer = TextRenderer.MeasureText("测Ag", New Font("Microsoft YaHei UI", 8.0F)).Height
        ''' <summary>单行输入框高度。</summary>
        Friend InputH As Integer = Math.Max(28, LineH + 12)
        ''' <summary>按钮高度。</summary>
        Friend ButtonH As Integer = Math.Max(32, LineH + 15)

        ''' <summary>
        ''' 按「界面 → 字号」重建字体与所有由字体推导的尺寸。
        ''' 只能在创建窗体/控件之前调用一次；调用之后新建的控件才会用上新字号。
        ''' </summary>
        Friend Sub ApplyFontSize(pt As Integer)
            If pt < 7 OrElse pt > 24 Then pt = 9
            Dim f = CSng(pt)
            FontBase = New Font("Microsoft YaHei UI", f)
            FontBold = New Font("Microsoft YaHei UI", f, FontStyle.Bold)
            FontTitle = New Font("Microsoft YaHei UI", f * 1.65F, FontStyle.Bold)
            FontH2 = New Font("Microsoft YaHei UI", f * 1.22F, FontStyle.Bold)
            LineH = TextRenderer.MeasureText("测Ag", FontBase).Height
            LineH2 = TextRenderer.MeasureText("测Ag", FontH2).Height
            LineHSmall = TextRenderer.MeasureText("测Ag", New Font("Microsoft YaHei UI", Math.Max(6.0F, f - 1.0F))).Height
            InputH = Math.Max(28, LineH + 12)
            ButtonH = Math.Max(32, LineH + 15)
        End Sub
        ''' <summary>纵向小节间距。</summary>
        Friend Const Gap As Integer = 12
        ''' <summary>文本可用宽度的保守估计（窄容器里也不会挤）。</summary>
        Friend Function TextW(text As String, Optional font As Font = Nothing) As Integer
            Return TextRenderer.MeasureText(text, If(font, FontBase), New Size(Integer.MaxValue, Integer.MaxValue),
                                            TextFormatFlags.NoPadding Or TextFormatFlags.SingleLine).Width
        End Function

        ''' <summary>圆角矩形路径（用于自绘控件与面板）。</summary>
        Friend Function RoundRect(rect As Rectangle, radius As Integer) As GraphicsPath
            Dim p As New GraphicsPath()
            Dim d = Math.Max(1, radius * 2)
            p.AddArc(rect.X, rect.Y, d, d, 180, 90)
            p.AddArc(rect.Right - d, rect.Y, d, d, 270, 90)
            p.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90)
            p.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90)
            p.CloseFigure()
            Return p
        End Function

        ''' <summary>主按钮（实心蓝）。宽度自动保证文字放得下，不会出现「…」。</summary>
        Friend Function PrimaryButton(text As String, Optional width As Integer = 120) As Button
            Dim b As New Button()
            b.Text = text
            b.Width = Math.Max(width, TextW(text, FontBold) + 34)
            b.Height = ButtonH
            b.FlatStyle = FlatStyle.Flat
            b.FlatAppearance.BorderSize = 0
            b.BackColor = Accent
            b.ForeColor = Color.White
            b.Font = FontBold
            b.Cursor = Cursors.Hand
            b.UseVisualStyleBackColor = False
            Anim.WireHover(b, Accent, AccentHover)
            AddHandler b.EnabledChanged, Sub(s, e) b.BackColor = If(b.Enabled, Accent, Color.FromArgb(186, 196, 214))
            Return b
        End Function

        ''' <summary>次要按钮（白底描边）。</summary>
        Friend Function GhostButton(text As String, Optional width As Integer = 120) As Button
            Dim b As New Button()
            b.Text = text
            b.Width = Math.Max(width, TextW(text) + 34)
            b.Height = ButtonH
            b.FlatStyle = FlatStyle.Flat
            b.FlatAppearance.BorderSize = 1
            b.FlatAppearance.BorderColor = BorderStrong
            b.BackColor = Card
            b.ForeColor = TextMain
            b.Cursor = Cursors.Hand
            b.UseVisualStyleBackColor = False
            Anim.WireHover(b, Card, Color.FromArgb(243, 246, 250))
            Return b
        End Function

        ''' <summary>白底卡片面板（1px 描边，无阴影，避免闪烁）。</summary>
        Friend Function CardPanel() As Panel
            Dim p As New Panel()
            p.BackColor = Card
            p.Padding = New Padding(16)
            AddHandler p.Paint, Sub(s, e)
                                    Dim g = e.Graphics
                                    g.SmoothingMode = SmoothingMode.AntiAlias
                                    Dim r As New Rectangle(0, 0, p.Width - 1, p.Height - 1)
                                    Using path = RoundRect(r, 8)
                                        Using pen As New Pen(Border, 1)
                                            g.DrawPath(pen, path)
                                        End Using
                                    End Using
                                End Sub
            Return p
        End Function

        Friend Function Label(text As String, Optional clr As Color? = Nothing, Optional font As Font = Nothing) As Label
            Dim l As New Label()
            l.Text = text
            l.AutoSize = True
            l.ForeColor = If(clr.HasValue, clr.Value, TextMain)
            l.Font = If(font IsNot Nothing, font, FontBase)
            l.BackColor = Color.Transparent
            Return l
        End Function


        ''' <summary>
        ''' 让一段说明文字随宿主宽度换行、随内容增高。文字永远不会被横向切掉。
        ''' 用法：建好 Label（AutoSize=True）后调一次，宿主 Resize 时会自动重算。
        ''' </summary>
        Friend Sub WrapHint(lbl As Label, host As Control, Optional inset As Integer = 10)
            Dim upd As Action = Sub()
                                    Dim w = Math.Max(160, host.ClientSize.Width - host.Padding.Horizontal - inset)
                                    lbl.MaximumSize = New Size(w, 0)
                                End Sub
            AddHandler host.Resize, Sub(s, e) upd()
            upd()
        End Sub

        ''' <summary>纵向堆叠用的小标题（行高跟着字体走）。</summary>
        Friend Function SectionTitle(text As String) As Label
            Return New Label() With {
                .Text = text, .Font = FontH2, .ForeColor = TextMain,
                .AutoSize = True, .Margin = New Padding(0, 0, 0, 8)
            }
        End Function

        Friend Function Hint(text As String) As Label
            Dim l = Label(text, TextSub)
            l.MaximumSize = New Size(900, 0)
            Return l
        End Function

        Friend Function TextBox2(Optional multiline As Boolean = False, Optional height As Integer = 30) As TextBox
            Dim t As New TextBox()
            t.Font = FontBase
            t.BorderStyle = BorderStyle.FixedSingle
            ' 底色跟主题走：浅色下是极浅灰、深色下是比卡片略亮的深灰，
            ' 聚焦时再提亮一档——深色主题里如果还用白色，会是一块刺眼的白砖
            t.BackColor = InputBg
            t.ForeColor = TextMain
            AddHandler t.Enter, Sub(s, e)
                                    If Not t.ReadOnly Then t.BackColor = InputBgFocus
                                End Sub
            AddHandler t.Leave, Sub(s, e)
                                    If Not t.ReadOnly Then t.BackColor = InputBg
                                End Sub
            If multiline Then
                t.Multiline = True
                t.Height = height
                t.ScrollBars = ScrollBars.Vertical
                t.AcceptsReturn = True
            Else
                t.Height = 26
            End If
            Return t
        End Function

        ''' <summary>
        ''' 把一棵控件树里的输入类控件统一刷成主题色。
        ''' 为什么用遍历而不是每个都走工厂：项目里有一批控件是直接 New 出来的
        ''' （数字框、路径框、日志框），逐个改工厂总会漏；遍历能一次覆盖，新增控件也自动跟着走。
        ''' </summary>
        Friend Sub ThemeInputs(root As Control)
            If root Is Nothing Then Return
            For Each c As Control In root.Controls
                Try
                    Select Case True
                        Case TypeOf c Is TextBox
                            Dim t = DirectCast(c, TextBox)
                            t.BackColor = If(t.ReadOnly, InputBg, InputBg)
                            t.ForeColor = TextMain
                        Case TypeOf c Is RichTextBox
                            Dim r = DirectCast(c, RichTextBox)
                            r.BackColor = InputBg
                            r.ForeColor = TextSub
                        Case TypeOf c Is NumericUpDown
                            Dim n = DirectCast(c, NumericUpDown)
                            n.BackColor = InputBg
                            n.ForeColor = TextMain
                            ' 已知问题：设了底色之后，深色下两位/三位数字会被裁成一位（72 → 7）。
                            ' 试过移除 BorderStyle、补宽度都不生效，说明是 UpDownEdit 重建后
                            ' 的 client 尺寸计算问题，需要单独查控件树，先不在这里打补丁。
                            ' 不要动 BorderStyle：改了会让内部编辑区的可用宽度重算，
                            ' 数字会被截成"7"而不是"72"
                        Case TypeOf c Is ComboBox
                            Dim cb = DirectCast(c, ComboBox)
                            cb.BackColor = InputBg
                            cb.ForeColor = TextMain
                            cb.FlatStyle = FlatStyle.Flat
                        Case TypeOf c Is ListBox
                            Dim lb = DirectCast(c, ListBox)
                            lb.BackColor = Card
                            lb.ForeColor = TextMain
                        Case TypeOf c Is CheckBox, TypeOf c Is RadioButton
                            c.ForeColor = TextMain
                    End Select
                Catch
                End Try
                If c.Controls.Count > 0 Then ThemeInputs(c)
            Next
        End Sub

        Friend Function Separator() As Panel
            Dim p As New Panel()
            p.Height = 1
            p.BackColor = Border
            p.Dock = DockStyle.Top
            Return p
        End Function

        ''' <summary>给 DataGridView 套统一外观。</summary>
        Friend Sub StyleGrid(g As DataGridView)
            g.BackgroundColor = Card
            g.BorderStyle = BorderStyle.None
            g.EnableHeadersVisualStyles = False
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None
            g.ColumnHeadersDefaultCellStyle.BackColor = HeaderBg
            g.ColumnHeadersDefaultCellStyle.ForeColor = TextSub
            g.ColumnHeadersDefaultCellStyle.Font = FontBold
            g.ColumnHeadersDefaultCellStyle.Padding = New Padding(8, 0, 0, 0)
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = HeaderBg
            g.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextSub
            g.ColumnHeadersHeight = 34
            ' 表头默认会按字符折行：列一窄就变成「大\n小」「写\n入\n位\n置」这种竖排碎字，
            ' 视觉上就是被裁断。禁止折行 + 给列最小宽度，宁可省略号也不要竖排。
            g.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
            g.DefaultCellStyle.Font = FontBase
            g.DefaultCellStyle.ForeColor = TextMain
            g.DefaultCellStyle.BackColor = Card
            g.DefaultCellStyle.SelectionBackColor = AccentLight
            g.DefaultCellStyle.SelectionForeColor = TextMain
            g.DefaultCellStyle.Padding = New Padding(8, 0, 0, 0)
            g.RowTemplate.Height = 28
            g.GridColor = Border
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal
            g.AllowUserToAddRows = False
            g.AllowUserToDeleteRows = False
            g.AllowUserToResizeRows = False
            g.ReadOnly = True
            g.RowHeadersVisible = False
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect
            g.MultiSelect = False
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        End Sub

    End Module

    ''' <summary>
    ''' 运行日志 + 崩溃记录。日志目录优先放在程序所在目录（便携），不可写时退回 %LOCALAPPDATA%。
    ''' 只写少量文本，不涉及大文件。
    ''' </summary>
    Friend Module UiLog

        Private ReadOnly _lock As New Object()
        Private _dir As String

        Friend ReadOnly Property LogPath As String
            Get
                EnsureDir()
                Return Path.Combine(_dir, "symbolprint.log")
            End Get
        End Property

        Private Sub EnsureDir()
            If _dir IsNot Nothing Then Return
            SyncLock _lock
                If _dir IsNot Nothing Then Return
                Dim candidates As String() = {
                    Path.Combine(AppContext.BaseDirectory, "logs"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SymbolPrint", "logs")
                }
                For Each c In candidates
                    Try
                        Directory.CreateDirectory(c)
                        Dim probe = Path.Combine(c, ".w")
                        File.WriteAllText(probe, "1")
                        File.Delete(probe)
                        _dir = c
                        Return
                    Catch
                    End Try
                Next
                _dir = Path.GetTempPath()
            End SyncLock
        End Sub

        Friend Sub Write(message As String)
            Try
                EnsureDir()
                Dim line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}"
                SyncLock _lock
                    File.AppendAllText(LogPath, line, Encoding.UTF8)
                End SyncLock
            Catch
                ' 日志失败不能影响主流程
            End Try
        End Sub

        Friend Sub WriteCrash(ex As Exception)
            Write("=== 未处理异常 ===" & Environment.NewLine & ex.ToString())
        End Sub

    End Module

End Namespace
