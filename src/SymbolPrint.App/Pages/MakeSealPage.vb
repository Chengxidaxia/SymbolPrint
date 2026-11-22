Imports System
Imports System.Drawing
Imports System.IO
Imports System.Text
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports CoreCrypto = SymbolPrint.Core.Crypto
Imports SymbolPrint.Core.Model
Imports SymbolPrint.Core.Util
Imports SymbolPrint.Core.Visual

Namespace App.Pages

    ''' <summary>
    ''' 做章页：填写标签/描述/章字节，选图案与口令，生成 .bin 章文件。
    ''' 布局约定（全程序统一）：文字行高一律取 Theme.LineH 等实测值，表格行用 AutoSize，
    ''' 不写死像素行高 —— 否则 DPI 一放大字就被切。
    ''' </summary>
    Friend NotInheritable Class MakeSealPage
        Inherits UserControl
        Implements IAppPage
        Implements ICancelablePage

        Private ReadOnly _owner As MainForm

        ' ---- 基础信息 ----
        Private ReadOnly _labelBox As TextBox
        Private ReadOnly _descBox As TextBox

        ' ---- 章字节（三种来源）----
        Private ReadOnly _srcText As RadioButton
        Private ReadOnly _srcFile As RadioButton
        Private ReadOnly _srcGen As RadioButton
        Private ReadOnly _srcHost As Panel
        Private ReadOnly _sealText As TextBox
        Private ReadOnly _filePanel As TableLayoutPanel
        Private ReadOnly _sealFileBox As TextBox
        Private ReadOnly _sealFilePick As Button
        Private ReadOnly _genPanel As TableLayoutPanel
        Private ReadOnly _genKind As ComboBox
        Private ReadOnly _genLen As NumericUpDown
        Private ReadOnly _genBtn As Button
        Private ReadOnly _genBox As TextBox

        ' ---- 口令与曲线 ----
        Private ReadOnly _passOn As CheckBox
        Private ReadOnly _pass1 As TextBox
        Private ReadOnly _pass2 As TextBox
        Private ReadOnly _curve As ComboBox

        ' ---- 图案 ----
        Private ReadOnly _svgPreview As PictureBox
        Private ReadOnly _svgInfo As Label
        Private ReadOnly _editorBtn As Button

        ' ---- 底部 ----
        Private ReadOnly _saveBtn As Button
        Private ReadOnly _progress As ProgressStrip
        Private ReadOnly _result As Label

        Private _svgMarkup As String = ""
        Private _genBytes As Byte() = Nothing
        Private _busy As Boolean
        Private _previewSeq As Integer

        Friend Sub New(owner As MainForm)
            _owner = owner
            BackColor = Theme.Bg
            DoubleBuffered = True

            ' ==================== 标题 ====================
            Dim head As New TableLayoutPanel() With {
                .Dock = DockStyle.Top, .ColumnCount = 1, .BackColor = Theme.Bg,
                .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
                .Padding = New Padding(2, 4, 2, 8)
            }
            head.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            Dim title As New Label() With {
                .Text = "做章", .Font = Theme.FontTitle, .ForeColor = Theme.TextMain,
                .AutoSize = True, .Margin = New Padding(0, 0, 0, 2)
            }
            Dim subtitle As New Label() With {
                .Text = "生成一个 .bin 章文件：内含 ECC 密钥、章字节、描述与图案。私钥可用口令保护。",
                .Font = Theme.FontBase, .ForeColor = Theme.TextSub, .AutoSize = True, .Margin = New Padding(0)
            }
            head.Controls.Add(title, 0, 0)

            ' ==================== 左侧：基本信息 ====================
            Dim leftCard = Theme.CardPanel()
            leftCard.Dock = DockStyle.Fill
            ' 右边比左边多留 10px：输入框右边界不贴卡片边框，视觉上才透气
            leftCard.Padding = New Padding(20, 16, 30, 16)

            Dim leftScroll As New Panel() With {.Dock = DockStyle.Fill, .AutoScroll = True, .BackColor = Theme.Card}
            Dim lt As New TableLayoutPanel() With {
                .Dock = DockStyle.Top, .ColumnCount = 1, .BackColor = Theme.Card,
                .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink, .Margin = New Padding(0)
            }
            lt.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))

            Dim h1 = SectionTitle("基本信息")

            Dim l1 = FieldLabel("标签（显示用）")
            _labelBox = Theme.TextBox2()
            _labelBox.Dock = DockStyle.Fill
            _labelBox.Margin = New Padding(0, 0, 0, Theme.Gap)
            _labelBox.PlaceholderText = "例如：合同专用章"

            Dim l2 = FieldLabel("描述（写进记录，验章时显示）")
            _descBox = Theme.TextBox2(True, Math.Max(72, Theme.LineH * 3 + 12))
            _descBox.Dock = DockStyle.Fill
            _descBox.Margin = New Padding(0, 0, 0, Theme.Gap)
            _descBox.PlaceholderText = "例如：仅用于 XX 项目内部文件，不代表法律效力"

            Dim l3 = FieldLabel("章字节（任意二进制内容）")

            Dim srcRow As New FlowLayoutPanel() With {
                .Dock = DockStyle.Fill, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
                .WrapContents = True, .BackColor = Theme.Card, .Margin = New Padding(0, 0, 0, 8)
            }
            _srcText = MakeRadio("文本", True)
            _srcFile = MakeRadio("从文件读取", False)
            _srcGen = MakeRadio("程序生成", False)
            srcRow.Controls.Add(_srcText)
            srcRow.Controls.Add(_srcFile)
            srcRow.Controls.Add(_srcGen)

            ' 三种来源共用一个固定高度的宿主面板：切换来源时高度不变，界面不会跳
            ' 不能用 Dock=Fill：外层是 AutoSize 表格，它按 PreferredSize 排行高，
            ' 而 Panel 的 PreferredSize 不含子控件，行会被压成默认 32px（章字节框只剩一行半）。
            _srcHost = New Panel() With {
                .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right,
                .Height = Math.Max(132, Theme.LineH * 6),
                .BackColor = Theme.Card, .Margin = New Padding(0, 0, 0, Theme.Gap)
            }

            _sealText = Theme.TextBox2(True, _srcHost.Height)
            _sealText.Dock = DockStyle.Fill
            _sealText.PlaceholderText = "这一段会作为「章字节」原样加密进记录（UTF-8 编码）"

            _filePanel = New TableLayoutPanel() With {
                .Dock = DockStyle.Fill, .ColumnCount = 2, .RowCount = 1,
                .BackColor = Theme.Card, .Visible = False, .Margin = New Padding(0)
            }
            _filePanel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            _filePanel.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
            _sealFileBox = Theme.TextBox2()
            _sealFileBox.Dock = DockStyle.Top
            _sealFileBox.ReadOnly = True
            _sealFileBox.BackColor = Color.FromArgb(250, 251, 253)
            _sealFileBox.Font = Theme.FontMono
            _sealFileBox.Margin = New Padding(0, 0, 10, 0)
            _sealFilePick = Theme.GhostButton("选择文件…")
            _sealFilePick.Dock = DockStyle.Top
            _sealFilePick.Margin = New Padding(0)
            _filePanel.Controls.Add(_sealFileBox, 0, 0)
            _filePanel.Controls.Add(_sealFilePick, 1, 0)

            _genPanel = New TableLayoutPanel() With {
                .Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 2,
                .BackColor = Theme.Card, .Visible = False, .Margin = New Padding(0)
            }
            _genPanel.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            _genPanel.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
            Dim genTop As New FlowLayoutPanel() With {
                .Dock = DockStyle.Fill, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
                .WrapContents = True, .BackColor = Theme.Card, .Margin = New Padding(0, 0, 0, 6)
            }
            _genKind = New ComboBox() With {
                .DropDownStyle = ComboBoxStyle.DropDownList, .Width = Theme.TextW("编号序列（可读）") + 40,
                .Margin = New Padding(0, 0, 8, 0), .Font = Theme.FontBase
            }
            _genKind.Items.AddRange(New Object() {"JSON 清单", "随机字节", "UUID", "编号序列"})
            _genKind.SelectedIndex = 0
            _genLen = New NumericUpDown() With {
                .Width = 84, .Minimum = 8, .Maximum = 4096, .Value = 32, .Increment = 8,
                .Margin = New Padding(0, 0, 8, 0), .Font = Theme.FontBase, .Visible = False
            }
            _genBtn = Theme.GhostButton("重新生成")
            _genBtn.Margin = New Padding(0)
            genTop.Controls.Add(_genKind)
            genTop.Controls.Add(_genLen)
            genTop.Controls.Add(_genBtn)
            _genBox = Theme.TextBox2(True, 60)
            _genBox.Dock = DockStyle.Fill
            _genBox.ReadOnly = True
            _genBox.BackColor = Color.FromArgb(250, 251, 253)
            _genBox.Font = Theme.FontMono
            _genBox.Margin = New Padding(0)
            _genPanel.Controls.Add(genTop, 0, 0)
            _genPanel.Controls.Add(_genBox, 0, 1)

            _srcHost.Controls.Add(_sealText)
            _srcHost.Controls.Add(_filePanel)
            _srcHost.Controls.Add(_genPanel)

            _passOn = New CheckBox() With {
                .Text = "用口令保护私钥（推荐）", .AutoSize = True, .ForeColor = Theme.TextMain,
                .Margin = New Padding(0, 0, 0, 6)
            }

            Dim passRow As New TableLayoutPanel() With {
                .Dock = DockStyle.Top, .ColumnCount = 2, .RowCount = 1,
                .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
                .BackColor = Theme.Card, .Margin = New Padding(0, 0, 0, Theme.Gap)
            }
            passRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
            passRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
            _pass1 = Theme.TextBox2()
            _pass1.Dock = DockStyle.Fill
            _pass1.UseSystemPasswordChar = True
            _pass1.PlaceholderText = "口令"
            _pass1.Enabled = False
            _pass1.Margin = New Padding(0, 0, 10, 0)
            _pass2 = Theme.TextBox2()
            _pass2.Dock = DockStyle.Fill
            _pass2.UseSystemPasswordChar = True
            _pass2.PlaceholderText = "再输一次"
            _pass2.Enabled = False
            _pass2.Margin = New Padding(0)
            passRow.Controls.Add(_pass1, 0, 0)
            passRow.Controls.Add(_pass2, 1, 0)

            Dim curveRow As New FlowLayoutPanel() With {
                .Dock = DockStyle.Fill, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
                .WrapContents = True, .BackColor = Theme.Card, .Margin = New Padding(0, 0, 0, 8)
            }
            Dim l4 = FieldLabel("曲线")
            l4.Margin = New Padding(0, 6, 8, 0)
            _curve = New ComboBox() With {
                .DropDownStyle = ComboBoxStyle.DropDownList, .Width = 150, .Font = Theme.FontBase,
                .Margin = New Padding(0, 0, 12, 0)
            }
            _curve.Items.AddRange(New Object() {"NIST P-256", "NIST P-384", "NIST P-521"})
            Select Case If(owner.Settings.Crypto.Curve, "").ToLowerInvariant()
                Case "nistp384", "p-384", "p384" : _curve.SelectedIndex = 1
                Case "nistp521", "p-521", "p521" : _curve.SelectedIndex = 2
                Case Else : _curve.SelectedIndex = 0
            End Select
            Dim curveHint As New Label() With {
                .Text = "曲线越长越安全、签名越大；P-256 已足够", .ForeColor = Theme.TextFaint,
                .AutoSize = True, .Margin = New Padding(0, 5, 0, 0)
            }
            curveRow.Controls.Add(l4)
            curveRow.Controls.Add(_curve)
            curveRow.Controls.Add(curveHint)

            Dim footHint As New Label() With {
                .Text = "章字节可以是任意内容：一段授权说明、一个序列号，或由程序生成的一份清单。" & Environment.NewLine &
                        "口令一旦忘记无法恢复私钥，请把 .bin 与口令分开保存。",
                .ForeColor = Theme.TextFaint, .AutoSize = True, .Margin = New Padding(0, 6, 0, 0)
            }

            For Each c As Control In New Control() {h1, l1, _labelBox, l2, _descBox, l3, srcRow, _srcHost,
                                                     _passOn, passRow, curveRow, footHint}
                lt.Controls.Add(c)
            Next
            leftScroll.Controls.Add(lt)
            leftCard.Controls.Add(leftScroll)

            ' ==================== 右侧：图案 ====================
            Dim rightCard = Theme.CardPanel()
            rightCard.Dock = DockStyle.Fill
            rightCard.Padding = New Padding(20, 16, 20, 16)

            Dim rightTable As New TableLayoutPanel() With {
                .Dock = DockStyle.Fill, .ColumnCount = 1, .BackColor = Theme.Card, .Margin = New Padding(0)
            }
            rightTable.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))

            Dim rt As New Label() With {
                .Text = "章图案（可选）", .Font = Theme.FontH2, .ForeColor = Theme.TextMain,
                .AutoSize = True, .Margin = New Padding(0, 0, 0, 6)
            }
            Dim rh As New Label() With {
                .Text = "存在章里，换电脑也能用；盖章时可选择是否贴到图片 / PDF 上。",
                .ForeColor = Theme.TextSub, .AutoSize = True, .Margin = New Padding(0, 0, 0, 10)
            }
            _svgPreview = New PictureBox() With {
                .Dock = DockStyle.Fill, .SizeMode = PictureBoxSizeMode.Zoom,
                .BackColor = Color.FromArgb(249, 250, 252), .BorderStyle = BorderStyle.FixedSingle,
                .Margin = New Padding(0, 0, 0, 8), .MinimumSize = New Size(120, 120)
            }
            _svgInfo = New Label() With {
                .Text = "尚未设置图案。", .ForeColor = Theme.TextFaint, .Font = New Font("Microsoft YaHei UI", 8.0F),
                .AutoSize = True, .Margin = New Padding(0, 0, 0, 10)
            }

            Dim btnRow As New FlowLayoutPanel() With {
                .Dock = DockStyle.Fill, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
                .WrapContents = True, .BackColor = Theme.Card, .Margin = New Padding(0, 0, 0, 10)
            }
            _editorBtn = Theme.PrimaryButton("打开图案编辑器")
            _editorBtn.Margin = New Padding(0, 0, 8, 6)
            Dim importBtn = Theme.GhostButton("导入 SVG…")
            importBtn.Margin = New Padding(0, 0, 8, 6)
            Dim clearSvgBtn = Theme.GhostButton("清除")
            clearSvgBtn.Margin = New Padding(0, 0, 0, 6)
            btnRow.Controls.Add(_editorBtn)
            btnRow.Controls.Add(importBtn)
            btnRow.Controls.Add(clearSvgBtn)

            Dim svgHint As New Label() With {
                .Text = "也可以用系统自带的画图 / Illustrator 做好 SVG 再导入。",
                .ForeColor = Theme.TextFaint, .AutoSize = True, .Margin = New Padding(0)
            }

            rightTable.Controls.Add(rt, 0, 0)
            rightTable.Controls.Add(rh, 0, 1)
            rightTable.Controls.Add(_svgPreview, 0, 2)
            rightTable.Controls.Add(_svgInfo, 0, 3)
            rightTable.Controls.Add(btnRow, 0, 4)
            rightTable.Controls.Add(svgHint, 0, 5)
            ' 除预览行以外全部 AutoSize（高度跟着字体走），预览行吃掉剩余高度
            rightTable.RowCount = 6
            rightTable.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            rightTable.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            rightTable.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
            rightTable.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            rightTable.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            rightTable.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            rightCard.Controls.Add(rightTable)

            AddHandler importBtn.Click, Sub(s, e) ImportSvg()
            AddHandler clearSvgBtn.Click, Sub(s, e)
                                              _svgMarkup = ""
                                              _svgPreview.Image = Nothing
                                              _svgInfo.Text = "尚未设置图案。"
                                          End Sub
            AddHandler _editorBtn.Click, Sub(s, e) OpenEditor()

            ' ==================== 底部 ====================
            Dim bottom As New TableLayoutPanel() With {
                .Dock = DockStyle.Bottom, .ColumnCount = 1, .BackColor = Theme.Bg, .Margin = New Padding(0),
                .Padding = New Padding(0, 6, 0, 10)
            }
            bottom.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            bottom.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            bottom.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            bottom.RowCount = 2

            Dim btnBar As New TableLayoutPanel() With {
                .Dock = DockStyle.Fill, .ColumnCount = 3, .RowCount = 1,
                .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
                .BackColor = Theme.Bg, .Margin = New Padding(0)
            }
            btnBar.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
            btnBar.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
            btnBar.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            _saveBtn = Theme.PrimaryButton("生成并保存章")
            _saveBtn.Margin = New Padding(0, 0, 10, 6)
            Dim openExisting = Theme.GhostButton("已有章文件？去盖章页载入")
            openExisting.Margin = New Padding(0, 0, 12, 6)
            _result = New Label() With {
                .AutoSize = False, .Dock = DockStyle.Fill, .ForeColor = Theme.TextSub,
                .Font = Theme.FontBase, .Text = "", .TextAlign = ContentAlignment.MiddleLeft,
                .Margin = New Padding(0, 0, 0, 6), .AutoEllipsis = True
            }
            btnBar.Controls.Add(_saveBtn, 0, 0)
            btnBar.Controls.Add(openExisting, 1, 0)
            btnBar.Controls.Add(_result, 2, 0)

            _progress = New ProgressStrip(owner.Settings.Progress.DelayMs, owner.Settings.Progress.ThrottleMs, owner.Settings.Progress) With {.Dock = DockStyle.Fill, .Margin = New Padding(0)}

            ' 状态与进度在上，动作按钮在下：先看"在做什么"，再决定点哪个
            bottom.Controls.Add(_progress, 0, 0)
            bottom.Controls.Add(btnBar, 0, 1)
            head.Controls.Add(subtitle, 0, 1)
            head.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            head.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            head.RowCount = 2

            AddHandler _saveBtn.Click, Sub(s, e) SaveSeal()
            AddHandler openExisting.Click, Sub(s, e) _owner.ShowPage("stamp")

            ' ==================== 组装 ====================
            Dim split As New SplitContainer() With {
                .Dock = DockStyle.Fill, .Orientation = Orientation.Vertical,
                .SplitterWidth = 14, .BackColor = Theme.Bg, .BorderStyle = BorderStyle.None
            }
            split.Panel1.Controls.Add(leftCard)
            split.Panel2.Controls.Add(rightCard)
            split.Panel1.BackColor = Theme.Bg
            split.Panel2.BackColor = Theme.Bg

            Dim applied = False
            AddHandler split.Resize, Sub(s, e)
                                         Using New Prof.T("make.split.Resize")
                                             If applied Then Return
                                             If split.Width < 300 Then Return
                                             applied = True
                                             Try
                                                 split.SplitterDistance = CInt(split.Width * 0.55)
                                             Catch
                                             End Try
                                         End Using
                                     End Sub

            Controls.Add(split)
            Controls.Add(bottom)
            Controls.Add(head)

            ' 长句说明按卡片宽度换行；宽度变了只改 MaximumSize，不重建控件
            WrapTo(subtitle, Me)
            WrapTo(rh, rightCard)
            WrapTo(svgHint, rightCard)
            WrapTo(footHint, leftCard)

            AddHandler _srcText.CheckedChanged, Sub(s, e) UpdateSealSource()
            AddHandler _srcFile.CheckedChanged, Sub(s, e) UpdateSealSource()
            AddHandler _srcGen.CheckedChanged, Sub(s, e) UpdateSealSource()
            AddHandler _passOn.CheckedChanged, Sub(s, e)
                                                   _pass1.Enabled = _passOn.Checked
                                                   _pass2.Enabled = _passOn.Checked
                                                   If _passOn.Checked Then _pass1.Focus()
                                               End Sub
            AddHandler _sealFilePick.Click, Sub(s, e) PickSealBytesFile()
            AddHandler _genBtn.Click, Sub(s, e) GenerateBytes()
            AddHandler _genKind.SelectedIndexChanged, Sub(s, e)
                                                          _genLen.Visible = (_genKind.SelectedIndex = 1)
                                                          GenerateBytes()
                                                      End Sub
            AddHandler _genLen.ValueChanged, Sub(s, e)
                                                 If _genKind.SelectedIndex = 1 Then GenerateBytes()
                                             End Sub

            UpdateSealSource()
        End Sub

        ' ==================== 小工具 ====================

        Private Shared Function SectionTitle(text As String) As Label
            Return New Label() With {
                .Text = text, .Font = Theme.FontH2, .ForeColor = Theme.TextMain,
                .AutoSize = True, .Margin = New Padding(0, 0, 0, 10)
            }
        End Function

        Private Shared Function FieldLabel(text As String) As Label
            Return New Label() With {
                .Text = text, .ForeColor = Theme.TextSub, .AutoSize = True,
                .Margin = New Padding(0, 0, 0, 4)
            }
        End Function

        Private Shared Function MakeRadio(text As String, checked As Boolean) As RadioButton
            Return New RadioButton() With {
                .Text = text, .AutoSize = True, .Checked = checked, .ForeColor = Theme.TextMain,
                .Margin = New Padding(0, 0, 20, 4)
            }
        End Function

        ''' <summary>让一段说明文字按宿主宽度换行，高度自动长——永远不会被横向切掉。</summary>
        ''' <remarks>
        ''' 换行会改 MaximumSize → Label 重新排版 → 父容器 AutoSize → 行高变化，是一串级联布局。
        ''' 拖动窗口时每帧都跑这一串会明显卡，所以在拖动期间跳过，松手后由 Relayout 统一补一次。
        ''' </remarks>
        Private Shared ReadOnly WrapUpdaters As New List(Of Action)()

        Private Shared Sub WrapTo(lbl As Label, host As Control)
            Dim upd As Action = Sub()
                                    Using New Prof.T("make.WrapTo")
                                        Dim w = host.ClientSize.Width - host.Padding.Horizontal - 8
                                        If w < 200 Then w = 200
                                        If lbl.MaximumSize.Width <> w Then lbl.MaximumSize = New Size(w, 0)
                                    End Using
                                End Sub
            WrapUpdaters.Add(upd)
            AddHandler host.Resize, Sub(s, e)
                                        If UiState.IsUserResizing Then Return
                                        upd()
                                    End Sub
            upd()
        End Sub

        Friend Property SvgMarkup As String
            Get
                Return _svgMarkup
            End Get
            Set(value As String)
                _svgMarkup = If(value, "")
                RenderPreview()
            End Set
        End Property

        Private Sub UpdateSealSource()
            _sealText.Visible = _srcText.Checked
            _filePanel.Visible = _srcFile.Checked
            _genPanel.Visible = _srcGen.Checked
            If _srcGen.Checked AndAlso _genBox.Text = "" Then GenerateBytes()
        End Sub

        ''' <summary>
        ''' 由程序生成章字节。生成的内容不是玩具：JSON 清单带标签、时间、随机 nonce，
        ''' 适合当「这段章是干什么用的」的自证材料；随机字节适合当密钥材料。
        ''' </summary>
        Private Sub GenerateBytes()
            Dim kind = _genKind.SelectedIndex
            Dim label = If(_labelBox.Text.Trim() = "", "未命名章", _labelBox.Text.Trim())
            Dim rnd = Security.Cryptography.RandomNumberGenerator.Create()
            Try
                Select Case kind
                    Case 0 ' JSON 清单
                        Dim nonce = HexOf(NewByte(rnd, 8))
                        Dim json = "{" & vbCrLf &
                                   "  ""v"": 1," & vbCrLf &
                                   "  ""app"": ""SymbolPrint""," & vbCrLf &
                                   "  ""label"": """ & JsonEscape(label) & """," & vbCrLf &
                                   "  ""created"": """ & DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz") & """," & vbCrLf &
                                   "  ""nonce"": """ & nonce & """" & vbCrLf &
                                   "}"
                        _genBytes = Encoding.UTF8.GetBytes(json)
                        _genBox.Text = json

                    Case 1 ' 随机字节
                        Dim n = CInt(_genLen.Value)
                        Dim buf = NewByte(rnd, n)
                        _genBytes = buf
                        _genBox.Text = "随机 " & n & " 字节（十六进制）：" & vbCrLf & HexOf(buf)

                    Case 2 ' UUID
                        Dim s = Guid.NewGuid().ToString()
                        _genBytes = Encoding.UTF8.GetBytes(s)
                        _genBox.Text = s

                    Case 3 ' 编号序列
                        Dim s = "SP-" & DateTime.Now.ToString("yyyyMMdd") & "-" & HexOf(NewByte(rnd, 4)).ToUpperInvariant()
                        _genBytes = Encoding.UTF8.GetBytes(s)
                        _genBox.Text = s
                End Select
            Finally
                rnd.Dispose()
            End Try
        End Sub

        Private Shared Function NewByte(rnd As Security.Cryptography.RandomNumberGenerator, n As Integer) As Byte()
            Dim b(n - 1) As Byte
            rnd.GetBytes(b)
            Return b
        End Function

        Private Shared Function HexOf(b As Byte()) As String
            Dim sb As New StringBuilder(b.Length * 2)
            For Each x In b
                sb.Append(x.ToString("x2"))
            Next
            Return sb.ToString()
        End Function

        Private Shared Function JsonEscape(s As String) As String
            Return s.Replace("\", "\\").Replace("""", "\""")
        End Function

        Private Sub PickSealBytesFile()
            Using d As New OpenFileDialog()
                d.Title = "选择作为章字节的文件"
                d.Filter = "所有文件|*.*"
                If d.ShowDialog(FindForm()) = DialogResult.OK Then _sealFileBox.Text = d.FileName
            End Using
        End Sub

        Private Sub ImportSvg()
            Using d As New OpenFileDialog()
                d.Title = "导入 SVG 图案"
                d.Filter = "SVG|*.svg"
                If d.ShowDialog(FindForm()) = DialogResult.OK Then
                    Try
                        SvgMarkup = File.ReadAllText(d.FileName, Encoding.UTF8)
                        _svgInfo.ForeColor = Theme.TextSub
                        _svgInfo.Text = $"{Path.GetFileName(d.FileName)}（{Comp.HumanSize(New FileInfo(d.FileName).Length)}）"
                    Catch ex As Exception
                        MessageBox.Show("读取 SVG 失败：" & ex.Message, "SymbolPrint", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    End Try
                End If
            End Using
        End Sub

        Private Sub OpenEditor()
            Using f As New SvgEditorForm(_svgMarkup)
                If f.ShowDialog(FindForm()) = DialogResult.OK Then
                    SvgMarkup = f.ResultSvg
                    _svgInfo.ForeColor = Theme.TextSub
                    _svgInfo.Text = "图案已更新（来自编辑器）"
                End If
            End Using
        End Sub

        Private Async Sub RenderPreview()
            Dim seq = Threading.Interlocked.Increment(_previewSeq)
            If String.IsNullOrWhiteSpace(_svgMarkup) Then
                _svgPreview.Image = Nothing
                Return
            End If
            Dim svg = _svgMarkup
            Try
                Dim png = Await Task.Run(Function() VisualMarkApplier.RenderSvgToPng(svg, 480))
                If seq <> _previewSeq Then Return ' 期间又改了，丢弃旧结果
                Using ms As New MemoryStream(png)
                    Using tmp = Drawing.Image.FromStream(ms)
                        Dim old = _svgPreview.Image
                        _svgPreview.Image = New Bitmap(tmp)
                        old?.Dispose()
                    End Using
                End Using
                If _svgInfo.ForeColor = Theme.TextFaint Then
                    _svgInfo.ForeColor = Theme.TextSub
                    _svgInfo.Text = "图案已就绪"
                End If
            Catch ex As Exception
                _svgInfo.ForeColor = Theme.Warn
                _svgInfo.Text = "图案渲染失败：" & ex.Message
            End Try
        End Sub

        ' ==================== 保存 ====================

        Private Async Sub SaveSeal()
            If _busy Then Return

            Dim labelText = _labelBox.Text.Trim()
            If labelText = "" Then
                MessageBox.Show("请填一个标签，方便以后认出这个章。", "SymbolPrint", MessageBoxButtons.OK, MessageBoxIcon.Information)
                _labelBox.Focus()
                Return
            End If

            Dim pwd = ""
            If _passOn.Checked Then
                pwd = _pass1.Text
                If pwd.Length < 4 Then
                    MessageBox.Show("口令至少 4 个字符（建议更长）。", "SymbolPrint", MessageBoxButtons.OK, MessageBoxIcon.Information)
                    Return
                End If
                If pwd <> _pass2.Text Then
                    MessageBox.Show("两次输入的口令不一致。", "SymbolPrint", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    Return
                End If
            End If

            ' 章字节
            Dim sealBytes As Byte()
            If _srcText.Checked Then
                sealBytes = Encoding.UTF8.GetBytes(_sealText.Text)
            ElseIf _srcFile.Checked Then
                Dim f = _sealFileBox.Text.Trim()
                If f = "" OrElse Not File.Exists(f) Then
                    MessageBox.Show("请选择作为章字节的文件（或改用文本 / 程序生成）。", "SymbolPrint", MessageBoxButtons.OK, MessageBoxIcon.Information)
                    Return
                End If
                Try
                    sealBytes = File.ReadAllBytes(f)
                Catch ex As Exception
                    MessageBox.Show("读取章字节文件失败：" & ex.Message, "SymbolPrint", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    Return
                End Try
            Else
                If _genBytes Is Nothing OrElse _genBytes.Length = 0 Then GenerateBytes()
                If _genBytes Is Nothing OrElse _genBytes.Length = 0 Then
                    MessageBox.Show("程序生成的内容为空，请点「重新生成」。", "SymbolPrint", MessageBoxButtons.OK, MessageBoxIcon.Information)
                    Return
                End If
                sealBytes = _genBytes
            End If

            Dim target As String
            Using d As New SaveFileDialog()
                d.Title = "保存章文件"
                d.Filter = "SymbolPrint 章|*.bin"
                d.FileName = MakeSafeFileName(labelText) & ".bin"
                If d.ShowDialog(FindForm()) <> DialogResult.OK Then Return
                target = d.FileName
            End Using

            Dim curveId As CoreCrypto.EcCurveId = CoreCrypto.EcCurveId.NistP256
            Select Case _curve.SelectedIndex
                Case 1 : curveId = CoreCrypto.EcCurveId.NistP384
                Case 2 : curveId = CoreCrypto.EcCurveId.NistP521
            End Select

            _busy = True
            _saveBtn.Enabled = False
            _result.Text = ""
            _progress.BeginWork("生成章文件…")

            Try
                Dim svg = _svgMarkup
                Dim desc = _descBox.Text.Trim()

                Dim seal = Await Task.Run(Function()
                                              Dim s As New StampFile()
                                              If curveId <> CoreCrypto.EcCurveId.NistP256 Then s.Keys = CoreCrypto.Ecies.GenerateKeys(curveId)
                                              s.Label = labelText
                                              s.Description = desc
                                              s.SealRawBytes = sealBytes
                                              s.SvgMarkup = svg
                                              s.CreatedAt = DateTimeOffset.Now
                                              If pwd <> "" Then s.SetPassphrase(pwd, _owner.Settings.Crypto.Pbkdf2Iterations)
                                              s.Save(target)
                                              Return s
                                          End Function)

                _result.Text = $"已保存：{Path.GetFileName(target)}    指纹 {seal.StampIdShort}" &
                               If(pwd <> "", "    私钥已加密", "    ⚠ 私钥未加密")
                Anim.FadeText(_result, Theme.Ok)

                _owner.SetSeal(target, seal)
                _progress.EndWork("章已生成")

                If pwd = "" Then
                    UiLog.Write("生成章（未加密私钥）：" & target)
                End If
            Catch ex As Exception
                _result.Text = "生成失败：" & ex.Message
                Anim.FadeText(_result, Theme.Danger)
                _progress.EndWork("失败")
            Finally
                _busy = False
                _saveBtn.Enabled = True
            End Try
        End Sub

        Private Shared Function MakeSafeFileName(s As String) As String
            Dim invalid = Path.GetInvalidFileNameChars()
            Dim sb As New StringBuilder()
            For Each ch In s
                sb.Append(If(Array.IndexOf(invalid, ch) >= 0, "_"c, ch))
            Next
            Dim r = sb.ToString().Trim()
            If r.Length > 60 Then r = r.Substring(0, 60)
            If r = "" Then r = "seal"
            Return r
        End Function

        ''' <summary>自动化/演示用：直接切到「程序生成」并生成一份，方便截图核对。</summary>
        Friend Sub ShowGeneratedSource()
            _srcGen.Checked = True
            UpdateSealSource()
            GenerateBytes()
        End Sub

        Public Sub OnShown() Implements IAppPage.OnShown
        End Sub

        ''' <summary>拖动结束后的统一重排：把所有说明文字的换行宽度补算一次。</summary>
        Public Sub Relayout() Implements IAppPage.Relayout
            For Each upd In WrapUpdaters
                Try
                    upd()
                Catch
                End Try
            Next
        End Sub

        ''' <summary>ESC 取消：把取消请求交给进度条内置的取消令牌。</summary>
        Friend Function CancelCurrent() As Boolean Implements ICancelablePage.CancelCurrent
            If _progress Is Nothing Then Return False
            Return _progress.CancelWork()
        End Function
    End Class

End Namespace
