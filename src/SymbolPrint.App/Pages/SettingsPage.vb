Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Reflection
Imports System.Windows.Forms
Imports SymbolPrint.Core.Model

Namespace App.Pages

    ''' <summary>
    ''' 高级设置页：把 AppSettings 里带 [Setting] 特性的参数全部渲染成可编辑表单。
    ''' 分组与标签来自特性本身（Core 是唯一事实来源），这里只负责"显示与写回"，不再重复维护一份字段清单。
    '''
    ''' 排版约定（避免出现"文字被切"）：
    '''   标签列固定 LabelW，控件列从 EditorX 起、宽 EditorW 且各种控件宽度统一；
    '''   说明文字在下一行、占满整行宽、按宽度自动换行，行高由它实测撑开；
    '''   行与行之间画 1px 分隔线，视觉上不挤。
    ''' </summary>
    Friend NotInheritable Class SettingsPage
        Inherits UserControl
        Implements IAppPage

        ' ---- 行几何：只在这里定义，渲染与重排共用同一套 ----
        Private Const LabelW As Integer = 190
        Private Const EditorX As Integer = 200
        Private Const EditorW As Integer = 320
        Private Const NumW As Integer = 120
        Private Const RowPadTop As Integer = 12
        Private Const RowPadBottom As Integer = 14

        Private NotInheritable Class Field
            Friend Group As String
            Friend Key As String
            Friend Entry As SettingEntry
            Friend Holder As Object
            Friend Prop As PropertyInfo
            Friend Editor As Control
            Friend EnumNames As String()
            Friend InEffect As Boolean = True
        End Class

        ''' <summary>
        ''' 取字段所属区段名，用于和 InEffectKeys 里的 "General.xxx" 对上。
        ''' 之前直接用 holder.GetType().Name，得到的是 "GeneralOptions.AutoBackupOriginal"，
        ''' 永远匹配不上白名单——于是连真在生效的参数都被标成「预留」，满屏都是"改了没用"。
        ''' </summary>
        Private Shared Function SectionOf(holder As Object) As String
            Dim n = holder.GetType().Name
            If n.EndsWith("Options", StringComparison.Ordinal) Then Return n.Substring(0, n.Length - 7)
            If n.EndsWith("Settings", StringComparison.Ordinal) Then Return n.Substring(0, n.Length - 8)
            Return n
        End Function

        ''' <summary>当前版本真正会读取的参数（其余为「预留」：配置文件里有，但没有任何代码读）。</summary>
        ' 当前版本真正会读取的参数。其余为「预留」：配置文件里有，但没有任何代码读。
        ' 每接一个就加到这里，界面立刻从「预留」变成正常可调。
        Private Shared ReadOnly InEffectKeys As New HashSet(Of String)(StringComparer.Ordinal) From {
            "General.AutoBackupOriginal", "General.BackupSuffix",
            "Crypto.Pbkdf2Iterations", "Crypto.Curve",
            "Compression.BrotliQuality", "Compression.Algorithm",
            "Compression.SkipIfLarger", "Compression.SealBytesMax", "Compression.MinSizeToCompress",
            "Image.OpacityPercent", "Image.RotationDegrees",
            "Verify.StrictMode", "Verify.WarnIfNoSeal", "Verify.CheckOriginalHash",
            "Verify.CheckStampId", "Verify.CheckOwnHash",
            "Verify.ShowRawHex", "Verify.ReportFormat", "Verify.CopyReportToClipboard",
            "Progress.DelayMs", "Progress.ThrottleMs", "Progress.Enabled",
            "Progress.ShowIndeterminate", "Progress.ShowBytesDetail",
            "Ui.ShowStampDescription", "Ui.ShowAdvancedByDefault", "Ui.ConfirmBeforeOverwrite"
        }

        ''' <summary>枚举值的中文显示（写回时仍用英文名，保持配置文件可读）。</summary>
        Private Shared Function DisplayEnum(name As String) As String
            Select Case name
                Case "System" : Return "跟随系统"
                Case "Light" : Return "浅色"
                Case "Dark" : Return "深色"
                Case "Brotli" : Return "Brotli（推荐）"
                Case "Deflate" : Return "Deflate"
                Case "None" : Return "不压缩"
                Case "BottomRight" : Return "右下角"
                Case "BottomLeft" : Return "左下角"
                Case "TopRight" : Return "右上角"
                Case "TopLeft" : Return "左上角"
                Case "Center" : Return "居中"
                Case "WebView2" : Return "WebView2（浏览器内核）"
                Case "Gdi" : Return "GDI+"
                Case "ZeroWidthBinary" : Return "零宽字符（2 bit/字符）"
                Case "VariationSelector" : Return "Unicode 变体选择符"
                Case "FileEnd" : Return "文件末尾"
                Case "FileStart" : Return "文件开头"
                Case Else : Return name
            End Select
        End Function

        Private ReadOnly _owner As MainForm
        Private ReadOnly _groups As ListBox
        Private ReadOnly _host As Panel
        Private ReadOnly _showAdvanced As CheckBox
        Private ReadOnly _status As Label
        Private ReadOnly _saveBtn As Button
        Private ReadOnly _leftCard As Panel
        Private ReadOnly _rightCard As Panel

        Private ReadOnly _fields As New List(Of Field)()
        Private ReadOnly _groupOrder As New List(Of String)()
        Private ReadOnly _tip As New ToolTip()
        Private _currentGroup As String = ""
        ''' <summary>重建分组列表期间抑制 SelectionChanged，避免同一个操作渲染两遍。</summary>
        Private _suppressGroupEvent As Boolean

        Friend Sub New(owner As MainForm)
            _owner = owner
            BackColor = Theme.Bg
            DoubleBuffered = True

            ' ---------------- 标题：表格排，行高跟字体走 ----------------
            Dim head As New TableLayoutPanel() With {
                .Dock = DockStyle.Top, .ColumnCount = 1, .RowCount = 2, .BackColor = Theme.Bg,
                .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
                .Padding = New Padding(2, 4, 2, 8)
            }
            head.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            head.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            head.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            head.Controls.Add(New Label() With {
                .Text = "高级设置", .Font = Theme.FontTitle, .ForeColor = Theme.TextMain,
                .AutoSize = True, .Margin = New Padding(0, 0, 0, 2)
            }, 0, 0)
            Dim headSub As New Label() With {
                .Text = "所有可调参数都在这里；改完点「保存设置」写入程序目录 config\settings.json。",
                .Font = Theme.FontBase, .ForeColor = Theme.TextSub, .AutoSize = True, .Margin = New Padding(0)
            }
            head.Controls.Add(headSub, 0, 1)

            ' ---------------- 左：分组 ----------------
            Dim leftCard = Theme.CardPanel()
            _leftCard = leftCard
            leftCard.Dock = DockStyle.Left
            leftCard.Width = 172
            leftCard.Padding = New Padding(10, 12, 10, 12)

            _groups = New ListBox() With {
                .Dock = DockStyle.Fill, .BorderStyle = BorderStyle.None,
                .Font = New Font("Microsoft YaHei UI", 9.5F),
                .ItemHeight = 34, .BackColor = Theme.Card, .ForeColor = Theme.TextMain,
                .DrawMode = DrawMode.OwnerDrawFixed
            }
            AddHandler _groups.DrawItem, AddressOf DrawGroupItem
            AddHandler _groups.SelectedIndexChanged, Sub(s, e) SwitchGroup()

            Dim openCfg = Theme.GhostButton("打开配置目录", 144)
            openCfg.Dock = DockStyle.Bottom
            openCfg.Height = Math.Max(30, Theme.LineH + 10)
            openCfg.Margin = New Padding(0, 8, 0, 0)
            AddHandler openCfg.Click, Sub(s, e) OpenConfigFolder()

            leftCard.Controls.Add(_groups)
            leftCard.Controls.Add(openCfg)

            ' ---------------- 右：参数 ----------------
            Dim rightCard = Theme.CardPanel()
            _rightCard = rightCard
            rightCard.Dock = DockStyle.Fill
            rightCard.Padding = New Padding(18, 12, 18, 12)

            Dim bar As New Panel() With {.Dock = DockStyle.Top, .Height = Theme.LineH + 14, .BackColor = Color.Transparent}
            _showAdvanced = New CheckBox() With {
                .Text = "显示高级参数", .AutoSize = True, .Left = 0, .Top = 2,
                .ForeColor = Theme.TextSub
            }
            _tip.SetToolTip(_showAdvanced,
                            "默认只列常用参数。勾选后显示高级参数——它们会影响结果，" & Environment.NewLine &
                            "但一般不需要改（如迭代次数、备份后缀）。")
            AddHandler _showAdvanced.CheckedChanged, Sub(s, e)
                                                          If _suppressGroupEvent Then Return
                                                          _suppressGroupEvent = True
                                                          RefreshGroups()
                                                          _suppressGroupEvent = False
                                                          If _groups.SelectedIndex >= 0 Then
                                                              _currentGroup = CStr(_groups.Items(_groups.SelectedIndex))
                                                          End If
                                                          Rebuild()
                                                      End Sub
            bar.Controls.Add(_showAdvanced)

            _host = New Panel() With {.Dock = DockStyle.Fill, .AutoScroll = True, .BackColor = Theme.Card}
            AddHandler _host.Resize, Sub(s, e)
                                         ' 拖动期间不重排：行宽重算会触发 AutoScroll 与换行级联，
                                         ' 每帧跑一次就是明显卡顿；松手后 Relayout 统一补
                                         If UiState.IsUserResizing Then Return
                                         LayoutRows()
                                     End Sub

            rightCard.Controls.Add(_host)
            rightCard.Controls.Add(bar)

            ' ---------------- 底部：状态靠左（过长省略），按钮靠右 ----------------
            Dim bottom As New TableLayoutPanel() With {
                .Dock = DockStyle.Bottom, .ColumnCount = 2, .RowCount = 1, .BackColor = Theme.Bg,
                .Padding = New Padding(0, 6, 0, 8)
            }
            bottom.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            bottom.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))

            _status = New Label() With {
                .AutoSize = False, .Dock = DockStyle.Fill, .ForeColor = Theme.TextSub,
                .Font = Theme.FontBase, .Text = "", .TextAlign = ContentAlignment.MiddleLeft,
                .AutoEllipsis = True, .Margin = New Padding(0, 0, 12, 0)
            }

            Dim btns As New FlowLayoutPanel() With {
                .Dock = DockStyle.Fill, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
                .WrapContents = False, .BackColor = Theme.Bg, .Margin = New Padding(0)
            }
            Dim reloadBtn = Theme.GhostButton("重新载入", 100)
            reloadBtn.Margin = New Padding(0, 0, 8, 0)
            Dim resetBtn = Theme.GhostButton("恢复默认", 100)
            resetBtn.Margin = New Padding(0, 0, 8, 0)
            Dim saveBtn = Theme.PrimaryButton("保存设置", 120)
            saveBtn.Margin = New Padding(0)
            _saveBtn = saveBtn
            btns.Controls.Add(reloadBtn)
            btns.Controls.Add(resetBtn)
            btns.Controls.Add(saveBtn)

            bottom.Controls.Add(_status, 0, 0)
            bottom.Controls.Add(btns, 1, 0)

            AddHandler saveBtn.Click, Sub(s, e) SaveAll()
            AddHandler resetBtn.Click, Sub(s, e) ResetToDefaults()
            AddHandler reloadBtn.Click, Sub(s, e) ReloadFromDisk()

            Controls.Add(rightCard)
            Controls.Add(leftCard)
            Controls.Add(bottom)
            Controls.Add(head)

            Theme.WrapHint(headSub, Me)

            ' 「默认展开高级选项」：初始勾选状态来自设置。
            ' 必须在 _host/_fields 就绪之后再设，否则 CheckedChanged 会在半初始化状态下重建
            _suppressGroupEvent = True
            _showAdvanced.Checked = _owner.Settings.Ui.ShowAdvancedByDefault
            _suppressGroupEvent = False

            LoadFields()
        End Sub

        ' ==================== 字段发现 ====================

        Private Sub LoadFields()
            _fields.Clear()
            _groupOrder.Clear()

            Dim s = _owner.Settings
            For Each holder As Object In New Object() {s, s.General, s.Crypto, s.Compression, s.Carriers,
                                                       s.Image, s.Pdf, s.Verify, s.Progress, s.Ui}
                For Each p In holder.GetType().GetProperties(BindingFlags.Public Or BindingFlags.Instance)
                    Dim attr = TryCast(p.GetCustomAttribute(Of SettingAttribute)(), SettingAttribute)
                    If attr Is Nothing Then Continue For

                    Dim f As New Field()
                    f.Group = If(String.IsNullOrEmpty(attr.Group), "其他", attr.Group)
                    f.Key = p.Name
                    f.Holder = holder
                    f.Prop = p
                    f.Entry = New SettingEntry() With {
                        .Group = f.Group, .Label = attr.Label, .Key = p.Name, .Unit = attr.Unit,
                        .Help = attr.Help, .Advanced = attr.Advanced,
                        .MinValue = attr.MinValue, .MaxValue = attr.MaxValue,
                        .Choices = attr.Choices, .Value = p.GetValue(holder), .ValueType = p.PropertyType
                    }
                    f.InEffect = InEffectKeys.Contains(SectionOf(holder) & "." & p.Name)
                    _fields.Add(f)

                    If Not _groupOrder.Contains(f.Group) Then _groupOrder.Add(f.Group)
                Next
            Next

            _groups.Items.Clear()
            _suppressGroupEvent = True
            RefreshGroups()
            _suppressGroupEvent = False

            If _groups.Items.Count > 0 Then
                _currentGroup = CStr(_groups.Items(_groups.SelectedIndex))
            Else
                _currentGroup = ""
            End If
            Rebuild()
        End Sub

        ''' <summary>
        ''' 按当前勾选状态重建左侧分组列表：没勾「显示预留参数」时，
        ''' 只保留至少有一个"生效且非高级"字段的分组——否则点进去全是空页。
        ''' 重建期间抑制 SelectionChanged，由调用方统一 Rebuild 一次，避免连渲染两遍。
        ''' </summary>
        Private Sub RefreshGroups()
            Dim keep = _currentGroup
            Dim showAdv = _showAdvanced.Checked

            _groups.BeginUpdate()
            _groups.Items.Clear()
            For Each g In _groupOrder
                Dim hasVisible = _fields.Any(Function(f) f.Group = g AndAlso f.InEffect AndAlso
                                                          (showAdv OrElse Not f.Entry.Advanced))
                If hasVisible Then _groups.Items.Add(g)
            Next
            _groups.EndUpdate()

            Dim idx = If(keep = "", -1, _groups.Items.IndexOf(keep))
            If idx >= 0 Then
                _groups.SelectedIndex = idx
            ElseIf _groups.Items.Count > 0 Then
                _groups.SelectedIndex = 0
            End If
        End Sub

        Private Sub DrawGroupItem(sender As Object, e As DrawItemEventArgs)
            If e.Index < 0 Then Return
            Dim selected = (e.State And DrawItemState.Selected) = DrawItemState.Selected
            Using b As New SolidBrush(If(selected, Theme.AccentLight, Theme.Card))
                e.Graphics.FillRectangle(b, e.Bounds)
            End Using
            If selected Then
                Using b As New SolidBrush(Theme.Accent)
                    e.Graphics.FillRectangle(b, e.Bounds.X, e.Bounds.Y + 7, 3, e.Bounds.Height - 14)
                End Using
            End If
            Dim text = CStr(_groups.Items(e.Index))
            Using f As New Font("Microsoft YaHei UI", 9.5F, If(selected, FontStyle.Bold, FontStyle.Regular))
                Using b As New SolidBrush(If(selected, Theme.Accent, Theme.TextMain))
                    ' 垂直居中：用字号算基线，不写死 +8（DPI 一变就偏）
                    Dim th = TextRenderer.MeasureText(text, f).Height
                    Dim ty = e.Bounds.Y + Math.Max(0, (e.Bounds.Height - th) \ 2)
                    e.Graphics.DrawString(text, f, b, e.Bounds.X + 14, ty)
                End Using
            End Using
        End Sub

        Private Sub SwitchGroup()
            If _suppressGroupEvent Then Return
            If _groups.SelectedIndex < 0 Then Return
            _currentGroup = CStr(_groups.Items(_groups.SelectedIndex))
            Rebuild()
        End Sub

        ' ==================== 渲染 ====================

        ''' <summary>参数行的三件套：行容器、说明文字、编辑控件（窗口缩放时要重算高度）。</summary>
        Private Class RowParts
            Public Row As Panel
            Public Lbl As Label
            Public Help As Label
            Public Editor As Control
            Public IsLast As Boolean
            ' 淡入动画的目标色：动画期间会被临时改成接近底色，收尾要能精确还原
            Public TextColor As Color
            Public HelpColor As Color
            Public EditorColor As Color
        End Class

        Private ReadOnly _parts As New List(Of RowParts)()
        Private _rowsHooked As Boolean

        Private Sub Rebuild()
            If _host.IsDisposed Then Return
            _host.SuspendLayout()
            For Each c As Control In _host.Controls.Cast(Of Control)().ToList()
                c.Dispose()
            Next
            _host.Controls.Clear()
            _parts.Clear()

            ' 默认只列出真正会生效的参数。
            ' 之前把 54 个「预留」字段一并铺出来，满屏都是「当前版本不读取」，
            ' 用户改了没效果还以为程序坏了——这比少显示几个参数糟得多。
            ' 勾上「显示预留参数」才会把它们放出来，并逐行标注。
            ' 只显示「真的会被读取」的参数：InEffect=False 的一律不出现。
            ' 留着它们只会让人改了没效果、以为程序坏了。
            Dim showAdv = _showAdvanced.Checked
            Dim items = _fields.Where(Function(f) f.Group = _currentGroup AndAlso
                                                   f.InEffect AndAlso
                                                   (showAdv OrElse Not f.Entry.Advanced)).ToList()

            If items.Count = 0 Then
                Dim empty As New Label() With {
                    .Text = "这个分组在当前设置下没有可调参数。",
                    .ForeColor = Theme.TextFaint, .AutoSize = True, .Left = 4, .Top = 12
                }
                _host.Controls.Add(empty)
                _host.ResumeLayout()
                Return
            End If

            For i = 0 To items.Count - 1
                Dim f = items(i)
                Dim row As New Panel() With {.Left = 0, .Top = 0, .BackColor = Theme.Card}

                ' 标签：固定列宽，超出就省略（正常文案都在范围内）
                Dim lbl As New Label() With {
                    .Text = f.Entry.Label, .AutoSize = False, .Left = 0, .Top = RowPadTop,
                    .Width = LabelW, .Height = Theme.LineH + 2,
                    .ForeColor = If(f.InEffect, Theme.TextMain, Theme.TextFaint),
                    .Font = Theme.FontBase, .AutoEllipsis = True
                }

                ' 说明文字：占满整行宽、按宽度换行、行高由它撑开。
                ' 「预留」标记放在句首而不是接在句尾：句尾拼接会和说明文字混在同一行、
                ' 字号还不一样，视觉上就是一团挤在一起的字。
                Dim helpText = If(f.Entry.Help, "")
                If Not f.InEffect Then
                    helpText = "〔预留，当前版本不读取〕" & If(helpText = "", "该参数暂未生效。", helpText)
                End If
                Dim help As New Label() With {
                    .Text = helpText, .AutoSize = True, .Left = 0, .Top = RowPadTop + Theme.LineH + 7,
                    .ForeColor = Theme.TextFaint, .Font = New Font("Microsoft YaHei UI", 8.0F),
                    .MaximumSize = New Size(EditorW + 200, 0)
                }

                row.Controls.Add(lbl)
                row.Controls.Add(help)

                Dim editor = BuildEditor(f)
                editor.Left = EditorX
                editor.Top = RowPadTop - 1
                row.Controls.Add(editor)

                ' 分隔线（最后一行不画，避免和卡片边线叠在一起）
                Dim lastRow = (i = items.Count - 1)
                If Not lastRow Then
                    AddHandler row.Paint, Sub(s, ev)
                                              Using p As New Pen(Color.FromArgb(238, 241, 246), 1)
                                                  ev.Graphics.DrawLine(p, 0, row.Height - 1, row.Width, row.Height - 1)
                                              End Using
                                          End Sub
                End If

                _host.Controls.Add(row)
                _parts.Add(New RowParts With {
                    .Row = row, .Lbl = lbl, .Help = help, .Editor = editor, .IsLast = lastRow,
                    .TextColor = lbl.ForeColor, .HelpColor = help.ForeColor,
                    .EditorColor = If(CanTint(editor), editor.ForeColor, Color.Empty)
                })
            Next

            If Not _rowsHooked Then
                _rowsHooked = True
                ' Resize 处理器已在构造时挂上，这里只做一次性标记（避免重复挂导致重排风暴）
            End If
            LayoutRows()
            _host.ResumeLayout()
            FadeInRows()
        End Sub

        ' ==================== 动效 ====================
        '
        ' 这里只动「颜色」和「文字」，绝不碰尺寸/位置：
        ' 设置页的行高是按说明文字换行算出来的，任何一次布局变化都会连锁改行高，
        ' 之前“缩放就截断/卡顿”的根因正是这个，所以动效一律走 ForeColor 补间。

        ''' <summary>每次重建参数行时自增；旧动画发现世代变了就直接退出，不去改已销毁的控件。</summary>
        Private _fadeGen As Integer

        ' ComboBox 的 ForeColor 管不到下拉列表里的文字，强行淡入会出现"框里浅、下拉深"的割裂，直接跳过
        Private Shared Function CanTint(c As Control) As Boolean
            Return c IsNot Nothing AndAlso Not (TypeOf c Is ComboBox)
        End Function

        ''' <summary>
        ''' 参数行自上而下淡入：切换分组 / 勾选「显示高级参数」时，让用户看清"内容整体换掉了"，
        ''' 而不是一片文字瞬间跳变。行与行之间错开一点，形成流动方向感。
        ''' </summary>
        Private Sub FadeInRows()
            If _parts.Count = 0 Then Return

            _fadeGen += 1
            Dim gen = _fadeGen
            Dim n = _parts.Count
            ' 行数多时压缩错开量，保证整体时长不随行数膨胀（最多约 300ms）
            Dim stagger = Math.Min(0.09, 0.45 / n)
            Dim span = Math.Max(0.3, 1.0 - (n - 1) * stagger)

            ' 起始态：贴近卡片底色（等于"还没显影"）
            For i = 0 To n - 1
                ApplyRowTint(i, 0.0)
            Next

            Anim.Run(300,
                     Sub(t)
                         If gen <> _fadeGen Then Return
                         For i = 0 To n - 1
                             Dim local = Math.Max(0.0, Math.Min(1.0, (t - i * stagger) / span))
                             ApplyRowTint(i, Anim.EaseOut(local))
                         Next
                     End Sub,
                     Sub()
                         If gen <> _fadeGen Then Return
                         For i = 0 To n - 1
                             ApplyRowTint(i, 1.0)
                         Next
                     End Sub)
        End Sub

        ''' <summary>把第 i 行的文字颜色按进度 k（0=贴近底色，1=目标色）混合。</summary>
        Private Sub ApplyRowTint(i As Integer, k As Double)
            If i < 0 OrElse i >= _parts.Count Then Return
            Dim p = _parts(i)
            If p.Row Is Nothing OrElse p.Row.IsDisposed Then Return
            Try
                If p.Lbl IsNot Nothing AndAlso Not p.Lbl.IsDisposed Then
                    p.Lbl.ForeColor = Anim.Blend(Theme.Card, p.TextColor, k)
                End If
                If p.Help IsNot Nothing AndAlso Not p.Help.IsDisposed Then
                    p.Help.ForeColor = Anim.Blend(Theme.Card, p.HelpColor, k)
                End If
                If p.Editor IsNot Nothing AndAlso Not p.Editor.IsDisposed AndAlso p.EditorColor <> Color.Empty Then
                    p.Editor.ForeColor = Anim.Blend(Theme.Card, p.EditorColor, k)
                End If
            Catch
                ' 控件在动画中途被重建：直接放弃这一行
            End Try
        End Sub

        ''' <summary>保存成功后按钮短暂变绿写"已保存"，再渐回主色——比只改底部一行状态更容易被注意到。</summary>
        Private Sub FlashSaved()
            If _saveBtn Is Nothing OrElse _saveBtn.IsDisposed Then Return
            Dim label = _saveBtn.Text
            _saveBtn.Text = "已保存"
            Anim.Run(1300,
                     Sub(t)
                         If _saveBtn.IsDisposed Then Return
                         ' 前 25% 保持绿色给眼睛一个落点，之后渐回主色
                         Dim k = Anim.EaseOut(Math.Max(0.0, (t - 0.25) / 0.75))
                         _saveBtn.BackColor = Anim.Blend(Theme.Ok, Theme.Accent, k)
                     End Sub,
                     Sub()
                         If _saveBtn.IsDisposed Then Return
                         _saveBtn.BackColor = Theme.Accent
                         _saveBtn.Text = label
                     End Sub)
        End Sub

        ''' <summary>按当前可用宽度重排所有参数行：说明文字多换一行，行就跟着长高。</summary>
        ''' <remarks>
        ''' 注意两件事，都是「滚动条/截断」的实际来源：
        '''   1. 重排前必须把 AutoScrollPosition 归零，否则上次滚动的残留偏移会让子控件整体右移，撑出横向滚动条；
        '''   2. 竖滚动条出现/消失会改变 ClientSize.Width，所以要按最终 DisplayRectangle 再校一遍宽度。
        ''' </remarks>
        Private Sub LayoutRows()
            If _host.IsDisposed Then Return
            Using New Prof.T("settings.LayoutRows")
                _host.SuspendLayout()
            _host.AutoScrollPosition = Point.Empty
            ApplyRowWidth(RowAvail())
            _host.ResumeLayout()

            ' 二次校正：竖滚动条出现后可视宽度变小，行宽按真实显示区再收一次
            If _host.HorizontalScroll.Visible Then
                _host.SuspendLayout()
                ApplyRowWidth(RowAvail())
                _host.ResumeLayout()

                ' WinForms 的一个实际行为：窗口从大缩小时，已经出现的横向滚动条不会因为内容变窄而自动收起。
                ' 关掉再打开 AutoScroll 会强制重新计算滚动条；滚动位置先记住再还原，用户不会感觉跳动。
                If _host.HorizontalScroll.Visible Then
                    Dim keep = _host.AutoScrollPosition
                    _host.AutoScroll = False
                    _host.AutoScroll = True
                    _host.AutoScrollPosition = New Point(-keep.X, -keep.Y)
                    ApplyRowWidth(RowAvail())
                End If
            End If
            End Using
        End Sub

        ''' <summary>行的可用宽度：可视宽扣掉内边距和（可能出现的）竖滚动条。</summary>
        Private Function RowAvail() As Integer
            Dim pad = _host.Padding.Horizontal
            Dim bar = If(_host.VerticalScroll.Visible, SystemInformation.VerticalScrollBarWidth, 0)
            Return Math.Max(360, _host.ClientSize.Width - pad - bar)
        End Function

        Private Sub ApplyRowWidth(avail As Integer)
            Dim top = 2
            For Each r In _parts
                r.Row.Width = avail
                ' 说明文字宽度 = 整行宽（左 0 起），高度按实测换行结果
                r.Help.MaximumSize = New Size(avail, 0)
                Dim need = RowPadTop + Theme.LineH + 7 + r.Help.PreferredHeight + RowPadBottom
                r.Row.Height = Math.Max(need, RowPadTop + Theme.InputH + RowPadBottom)
                r.Row.Top = top
                top += r.Row.Height
            Next
        End Sub

        Private Function BuildEditor(f As Field) As Control
            Dim t = f.Entry.ValueType
            Dim val = f.Entry.Value

            If t Is GetType(Boolean) Then
                Dim cb As New CheckBox() With {
                    .Checked = CBool(If(val, False)), .AutoSize = True, .Text = ""
                }
                f.Editor = cb
                Return cb
            End If

            If t.IsEnum Then
                Dim cbo As New ComboBox() With {
                    .DropDownStyle = ComboBoxStyle.DropDownList, .Width = EditorW,
                    .Font = Theme.FontBase
                }
                Dim names = [Enum].GetNames(t)
                f.EnumNames = names
                For Each n In names
                    cbo.Items.Add(DisplayEnum(n))
                Next
                Dim idx = Array.IndexOf(names, val.ToString())
                cbo.SelectedIndex = If(idx >= 0, idx, 0)
                f.Editor = cbo
                Return cbo
            End If

            If f.Entry.Choices IsNot Nothing AndAlso f.Entry.Choices.Length > 0 Then
                Dim cbo2 As New ComboBox() With {
                    .DropDownStyle = ComboBoxStyle.DropDownList, .Width = EditorW, .Font = Theme.FontBase
                }
                cbo2.Items.AddRange(f.Entry.Choices)
                cbo2.SelectedItem = If(val Is Nothing, "", val.ToString())
                f.Editor = cbo2
                Return cbo2
            End If

            If t Is GetType(Integer) Then
                Dim n As New NumericUpDown() With {
                    .Width = NumW, .Minimum = CDec(Math.Max(Integer.MinValue + 1, f.Entry.MinValue)),
                    .Maximum = CDec(Math.Min(Integer.MaxValue, f.Entry.MaxValue)), .Value = CDec(CInt(val))
                }
                f.Editor = n
                Return n
            End If

            If t Is GetType(Long) Then
                Dim n2 As New NumericUpDown() With {
                    .Width = NumW, .Minimum = CDec(Math.Max(0, f.Entry.MinValue)),
                    .Maximum = CDec(Math.Min(CLng(Long.MaxValue / 2), f.Entry.MaxValue)), .Value = CDec(CLng(val))
                }
                f.Editor = n2
                Return n2
            End If

            If t Is GetType(Double) Then
                Dim n3 As New NumericUpDown() With {
                    .Width = NumW, .DecimalPlaces = 2, .Increment = CDec(0.1),
                    .Minimum = CDec(f.Entry.MinValue), .Maximum = CDec(f.Entry.MaxValue),
                    .Value = CDec(CDbl(val))
                }
                f.Editor = n3
                Return n3
            End If

            ' 字符串：宽度统一到 EditorW，长路径也放得下（内部再长就有光标滚动）
            Dim tb As New TextBox() With {
                .Width = EditorW, .Text = If(val Is Nothing, "", CStr(val)), .Font = Theme.FontBase
            }
            f.Editor = tb
            Return tb
        End Function

        ' ==================== 写回 ====================

        Private Sub SaveAll()
            Dim changed = 0
            For Each f In _fields
                If f.Editor Is Nothing Then Continue For
                Try
                    Dim newVal As Object = Nothing
                    Dim t = f.Entry.ValueType

                    If TypeOf f.Editor Is CheckBox Then
                        newVal = DirectCast(f.Editor, CheckBox).Checked
                    ElseIf TypeOf f.Editor Is NumericUpDown Then
                        Dim d = DirectCast(f.Editor, NumericUpDown).Value
                        If t Is GetType(Integer) Then newVal = CInt(d)
                        If t Is GetType(Long) Then newVal = CLng(d)
                        If t Is GetType(Double) Then newVal = CDbl(d)
                    ElseIf TypeOf f.Editor Is ComboBox Then
                        Dim s = CStr(DirectCast(f.Editor, ComboBox).SelectedItem)
                        If t.IsEnum Then
                            Dim ci = DirectCast(f.Editor, ComboBox).SelectedIndex
                            newVal = If(f.EnumNames IsNot Nothing AndAlso ci >= 0 AndAlso ci < f.EnumNames.Length,
                                        [Enum].Parse(t, f.EnumNames(ci)), [Enum].Parse(t, s))
                        Else
                            newVal = s
                        End If
                    ElseIf TypeOf f.Editor Is TextBox Then
                        newVal = DirectCast(f.Editor, TextBox).Text
                    End If

                    If newVal Is Nothing Then Continue For
                    If Not Object.Equals(f.Prop.GetValue(f.Holder), newVal) Then
                        f.Prop.SetValue(f.Holder, newVal)
                        changed += 1
                    End If
                Catch ex As Exception
                    _status.Text = "保存「" & f.Entry.Label & "」失败：" & ex.Message
                    Anim.FadeText(_status, Theme.Danger, 220)
                    Return
                End Try
            Next

            Try
                _owner.Settings.Save()
                _status.Text = $"已保存（{changed} 项有改动）：{AppSettings.DefaultPath()}"
                Anim.FadeText(_status, Theme.Ok, 260)
                FlashSaved()
            Catch ex As Exception
                _status.Text = "保存失败：" & ex.Message
                Anim.FadeText(_status, Theme.Danger, 260)
            End Try
        End Sub

        Private Sub ReloadFromDisk()
            Try
                Dim s = AppSettings.Load()
                CopySettings(s, _owner.Settings)
                LoadFields()
                _status.ForeColor = Theme.TextSub
                _status.Text = "已从磁盘重新载入。"
            Catch ex As Exception
                _status.ForeColor = Theme.Danger
                _status.Text = "载入失败：" & ex.Message
            End Try
        End Sub

        Private Sub ResetToDefaults()
            If MessageBox.Show("把所有参数恢复为默认值？当前设置会被覆盖（不会立即写盘，点「保存设置」才生效）。",
                               "SymbolPrint", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) <> DialogResult.OK Then Return
            CopySettings(New AppSettings(), _owner.Settings)
            LoadFields()
            _status.ForeColor = Theme.Warn
            _status.Text = "已恢复默认值，记得点「保存设置」。"
        End Sub

        ''' <summary>把 src 的全部公开属性浅拷贝到 dst（子对象按引用整体替换）。</summary>
        Private Shared Sub CopySettings(src As AppSettings, dst As AppSettings)
            For Each p In GetType(AppSettings).GetProperties(BindingFlags.Public Or BindingFlags.Instance)
                If Not p.CanWrite Then Continue For
                p.SetValue(dst, p.GetValue(src))
            Next
        End Sub

        Private Sub OpenConfigFolder()
            Try
                Dim dir = IO.Path.GetDirectoryName(AppSettings.DefaultPath())
                If Not IO.Directory.Exists(dir) Then IO.Directory.CreateDirectory(dir)
                Process.Start(New ProcessStartInfo(dir) With {.UseShellExecute = True})
            Catch ex As Exception
                MessageBox.Show("无法打开配置目录：" & ex.Message, "SymbolPrint", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            End Try
        End Sub

        Public Sub OnShown() Implements IAppPage.OnShown
        End Sub

        ''' <summary>拖动结束后的统一重排（拖动期间行宽重算被冻结，松手补一次）。</summary>
        Public Sub Relayout() Implements IAppPage.Relayout
            LayoutRows()
        End Sub
    End Class

End Namespace
