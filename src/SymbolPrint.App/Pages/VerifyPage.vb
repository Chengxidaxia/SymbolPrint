Imports System
Imports System.Drawing
Imports System.IO
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports App.Controls
Imports SymbolPrint.Core.Model
Imports SymbolPrint.Core.Service
Imports SymbolPrint.Core.Util

Namespace App.Pages

    ''' <summary>
    ''' 验章页：选文件（可选带章）→ 九项检查 → 用配色给出结论。
    ''' 关键：没有章时必须明确写出"不能证明来源"，不能给出虚假的通过结论。
    ''' </summary>
    Friend NotInheritable Class VerifyPage
        Inherits UserControl
        Implements IAppPage

        Private ReadOnly _owner As MainForm

        Private ReadOnly _fileBox As TextBox
        Private ReadOnly _sealBox As TextBox
        Private ReadOnly _passBox As TextBox
        Private ReadOnly _verifyBtn As Button
        Private ReadOnly _banner As ResultBanner
        Private ReadOnly _checks As DataGridView
        Private ReadOnly _detail As DataGridView
        Private ReadOnly _progress As ProgressStrip
        Private ReadOnly _sealHint As Label

        Private _busy As Boolean

        Friend Sub New(owner As MainForm)
            _owner = owner
            BackColor = Theme.Bg
            DoubleBuffered = True
            AllowDrop = True

            ' ---------------- 标题 ----------------
            Dim head As New Panel() With {.Dock = DockStyle.Top, .Height = 52, .BackColor = Theme.Bg}
            head.Controls.Add(New Label() With {
                .Text = "验章", .Font = Theme.FontTitle, .ForeColor = Theme.TextMain, .AutoSize = True, .Left = 2, .Top = 2
            })
            head.Controls.Add(New Label() With {
                .Text = "逐项检查盖章记录、章身份与文件内容，并说明结论的适用范围。",
                .Font = Theme.FontBase, .ForeColor = Theme.TextSub, .AutoSize = True, .Left = 4, .Top = 31
            })

            ' ---------------- 输入卡片 ----------------
            Dim inputCard = Theme.CardPanel()
            inputCard.Dock = DockStyle.Top
            inputCard.Height = 132
            inputCard.Padding = New Padding(14)

            Dim fl = Theme.Label("文件", Theme.TextSub) : fl.SetBounds(0, 32, 34, 18)
            _fileBox = Theme.TextBox2()
            _fileBox.SetBounds(40, 28, 470, 26)
            _fileBox.Font = Theme.FontMono
            Dim filePick = Theme.GhostButton("选择…", 78)
            filePick.SetBounds(516, 26, 78, 30)

            Dim sl = Theme.Label("章", Theme.TextSub) : sl.SetBounds(0, 68, 34, 18)
            _sealBox = Theme.TextBox2()
            _sealBox.SetBounds(40, 64, 340, 26)
            _sealBox.Font = Theme.FontMono
            Dim sealPick = Theme.GhostButton("选择…", 78)
            sealPick.SetBounds(386, 62, 78, 30)

            _passBox = Theme.TextBox2()
            _passBox.SetBounds(472, 64, 130, 26)
            _passBox.UseSystemPasswordChar = True
            _passBox.PlaceholderText = "章口令（可选）"

            _verifyBtn = Theme.PrimaryButton("开始验证", 118)
            _verifyBtn.SetBounds(614, 60, 118, 32)

            _sealHint = New Label() With {
                .Text = "不提供章也可以验证：能确认记录未被改动，但无法证明它来自谁。",
                .ForeColor = Theme.TextFaint, .Font = New Font("Microsoft YaHei UI", 8.0F),
                .AutoSize = False, .Left = 0, .Top = 100, .Width = 700, .Height = 18
            }

            inputCard.Controls.Add(fl)
            inputCard.Controls.Add(_fileBox)
            inputCard.Controls.Add(filePick)
            inputCard.Controls.Add(sl)
            inputCard.Controls.Add(_sealBox)
            inputCard.Controls.Add(sealPick)
            inputCard.Controls.Add(_passBox)
            inputCard.Controls.Add(_verifyBtn)
            inputCard.Controls.Add(_sealHint)

            AddHandler filePick.Click, Sub(s, e) PickFile()
            AddHandler sealPick.Click, Sub(s, e) PickSeal()
            AddHandler _verifyBtn.Click, Sub(s, e) RunVerify()

            ' ---------------- 结论横幅 ----------------
            Dim bannerWrap As New Panel() With {.Dock = DockStyle.Top, .Height = 90, .BackColor = Theme.Bg, .Padding = New Padding(0, 4, 0, 8)}
            _banner = New ResultBanner() With {.Dock = DockStyle.Fill}
            bannerWrap.Controls.Add(_banner)

            ' ---------------- 主体：检查明细 + 详情 ----------------
            Dim leftCard = Theme.CardPanel()
            leftCard.Dock = DockStyle.Fill
            leftCard.Padding = New Padding(14)
            Dim leftTitle = Theme.Label("检查明细", Theme.TextMain, Theme.FontH2)
            leftTitle.Dock = DockStyle.Top
            leftTitle.Height = 26
            leftTitle.AutoSize = False

            _checks = New DataGridView() With {.Dock = DockStyle.Fill}
            Theme.StyleGrid(_checks)
            _checks.Columns.Add(MakeCol("part", "检查项", 22))
            _checks.Columns.Add(MakeCol("state", "结果", 12))
            _checks.Columns.Add(MakeCol("msg", "说明", 66))
            _checks.Columns("msg").DefaultCellStyle.WrapMode = DataGridViewTriState.True
            _checks.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells
            AddHandler _checks.CellFormatting, AddressOf OnCheckFormatting

            leftCard.Controls.Add(_checks)
            leftCard.Controls.Add(leftTitle)

            Dim rightCard = Theme.CardPanel()
            rightCard.Dock = DockStyle.Fill
            rightCard.Padding = New Padding(14)
            Dim rightTitle = Theme.Label("记录详情", Theme.TextMain, Theme.FontH2)
            rightTitle.Dock = DockStyle.Top
            rightTitle.Height = 26
            rightTitle.AutoSize = False

            _detail = New DataGridView() With {.Dock = DockStyle.Fill}
            Theme.StyleGrid(_detail)
            _detail.ColumnHeadersVisible = False
            _detail.Columns.Add(MakeCol("k", "", 30))
            _detail.Columns.Add(MakeCol("v", "", 70))
            _detail.Columns("v").DefaultCellStyle.WrapMode = DataGridViewTriState.True
            _detail.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells

            rightCard.Controls.Add(_detail)
            rightCard.Controls.Add(rightTitle)

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
                                         Using New Prof.T("verify.split.Resize")
                                             If applied Then Return
                                             If split.Width < 300 Then Return
                                             applied = True
                                             Try
                                                 split.SplitterDistance = CInt(split.Width * 0.58)
                                             Catch
                                             End Try
                                         End Using
                                     End Sub

            ' ---------------- 底部 ----------------
            ' 高度跟着 ProgressStrip 走（它内部要放状态行 + 进度条），写死 56 会把状态文字切一半
            Dim bottom As New Panel() With {.Dock = DockStyle.Bottom, .Height = Theme.LineH + 46, .BackColor = Theme.Bg}
            _progress = New ProgressStrip(owner.Settings.Progress.DelayMs, owner.Settings.Progress.ThrottleMs)
            _progress.Dock = DockStyle.Fill
            bottom.Controls.Add(_progress)

            Controls.Add(split)
            Controls.Add(bottom)
            Controls.Add(bannerWrap)
            Controls.Add(inputCard)
            Controls.Add(head)

            AddHandler DragEnter, Sub(s, e)
                                      e.Effect = If(e.Data IsNot Nothing AndAlso e.Data.GetDataPresent(DataFormats.FileDrop),
                                                    DragDropEffects.Copy, DragDropEffects.None)
                                  End Sub
            AddHandler DragDrop, Sub(s, e)
                                     If e.Data Is Nothing Then Return
                                     Dim items = TryCast(e.Data.GetData(DataFormats.FileDrop), String())
                                     If items Is Nothing OrElse items.Length = 0 Then Return
                                     ' 拖进来的可能是「文件 + 章」，按扩展名分开
                                     For Each it In items
                                         If Not File.Exists(it) Then Continue For
                                         If Path.GetExtension(it).ToLowerInvariant() = ".bin" AndAlso StampFile.HasMagic(it) Then
                                             _sealBox.Text = it
                                         Else
                                             _fileBox.Text = it
                                         End If
                                     Next
                                     RunVerify()
                                 End Sub
        End Sub

        Private Shared Function MakeCol(name As String, header As String, weight As Integer) As DataGridViewColumn
            Dim c As New DataGridViewTextBoxColumn()
            c.Name = name
            c.HeaderText = header
            c.FillWeight = weight
            c.SortMode = DataGridViewColumnSortMode.NotSortable
            Return c
        End Function

        Private Sub PickFile()
            Using d As New OpenFileDialog()
                d.Title = "选择要验证的文件"
                d.Filter = "所有文件|*.*"
                If d.ShowDialog(FindForm()) = DialogResult.OK Then
                    _fileBox.Text = d.FileName
                    RunVerify()
                End If
            End Using
        End Sub

        Private Sub PickSeal()
            Using d As New OpenFileDialog()
                d.Title = "选择章文件（可选）"
                d.Filter = "SymbolPrint 章|*.bin|所有文件|*.*"
                If d.ShowDialog(FindForm()) = DialogResult.OK Then
                    _sealBox.Text = d.FileName
                    RunVerify()
                End If
            End Using
        End Sub

        Private Async Sub RunVerify()
            If _busy Then Return
            Dim target = _fileBox.Text.Trim()
            If target = "" OrElse Not File.Exists(target) Then
                MessageBox.Show("请选择要验证的文件。", "SymbolPrint", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If

            Dim seal As StampFile = Nothing
            Dim sealPath = _sealBox.Text.Trim()
            If sealPath <> "" Then
                If Not File.Exists(sealPath) Then
                    MessageBox.Show("章文件不存在：" & sealPath, "SymbolPrint", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    Return
                End If
                Try
                    seal = StampFile.Load(sealPath, _passBox.Text)
                    If seal.IsLocked Then
                        _sealHint.ForeColor = Theme.Warn
                        _sealHint.Text = "章的私钥受口令保护，请在上方输入口令后再验证。"
                        Return
                    End If
                    _sealHint.ForeColor = Theme.TextFaint
                    _sealHint.Text = "不提供章也可以验证：能确认记录未被改动，但无法证明它来自谁。"
                Catch ex As Exception
                    MessageBox.Show("章文件无法读取：" & ex.Message, "SymbolPrint", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    Return
                End Try
            End If

            _busy = True
            _verifyBtn.Enabled = False
            _progress.BeginWork("验证中…")
            _checks.Rows.Clear()
            _detail.Rows.Clear()
            _banner.ShowResult(BannerKind.None, "正在验证…", "读取盖章记录并逐项核对。")

            Dim sink = _progress.Sink
            Try
                Dim r = Await Task.Run(Function() SealService.Verify(target, seal, sink))
                Render(r)
            Catch ex As OperationCanceledException
                _banner.ShowResult(BannerKind.None, "已取消", "本次验证被中止。")
            Catch ex As Exception
                _banner.ShowResult(BannerKind.Fail, "验证失败", ex.Message)
            Finally
                _busy = False
                _verifyBtn.Enabled = True
                _progress.EndWork("完成")
            End Try
        End Sub

        Private Sub Render(r As SealVerifyResult)
            ' ---- 明细表 ----
            _checks.Rows.Clear()
            For Each c In r.Checks
                _checks.Rows.Add(c.Part, StateText(c.State), c.Message)
            Next
            _checks.ClearSelection()

            ' ---- 详情 ----
            _detail.Rows.Clear()
            _detail.ClearSelection()
            AddDetail("文件", Path.GetFileName(_fileBox.Text))
            AddDetail("载体", If(r.CarrierId = "unknown", "（不支持的类型）", $"{r.CarrierName}（{r.CarrierId}）"))
            If r.HasRecord Then
                AddDetail("记录版本", "v" & r.RecordVersion.ToString())
                AddDetail("记录长度", r.BlobLength & " 字节（压缩后）")
                AddDetail("盖章时间", If(r.StampTime.HasValue,
                                         r.StampTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), "（未提供）"))
            Else
                AddDetail("盖章记录", "无")
            End If

            If r.StampProvided Then
                AddDetail("章标签", If(r.StampLabel = "", "（无）", r.StampLabel))
                AddDetail("章描述", If(r.StampDescription = "", "（无）", r.StampDescription))
                AddDetail("章指纹", r.StampIdShort)
            End If
            If r.SealBytesLength > 0 Then AddDetail("章字节", r.SealBytesLength & " 字节")
            If r.OriginalHashHex <> "" Then AddDetail("原文件 SHA3-512", r.OriginalHashHex)
            If r.VisualBurned Then AddDetail("图案", "盖章时已把图案烧进文件")

            ' ---- 结论 ----
            If r.AnyFailed Then
                Dim names = String.Join("、", r.FailureParts)
                _banner.ShowResult(BannerKind.Fail, "验证未通过",
                                   $"以下检查失败：{names}。这个文件要么被改动过，要么记录/章与文件不匹配 —— 不能当作有效盖章。")
            ElseIf Not r.HasRecord Then
                _banner.ShowResult(BannerKind.Warn, "没有找到盖章记录",
                                   "文件本身干净，但没有任何 SymbolPrint 记录。仅凭这个文件无法证明来源，也无法证明它从未被替换过。")
            ElseIf Not r.StampProvided Then
                _banner.ShowResult(BannerKind.Warn, "记录完整，但无法证明来源",
                                   "记录与文件内容自洽、未被改动。由于没有提供章文件，不能确认这个记录是不是由某个人/某台机器盖的。")
            Else
                _banner.ShowResult(BannerKind.Ok, "验证通过",
                                   $"文件内容与盖章时一致，且记录由章「{If(r.StampLabel = "", r.StampIdShort, r.StampLabel)}」签发。")
            End If
        End Sub

        Private Sub AddDetail(k As String, v As String)
            ' 长哈希不换行会被列宽裁掉，这里按 40 字符折行，保证完整可读
            If v IsNot Nothing AndAlso v.Length > 40 AndAlso Not v.Contains(Environment.NewLine) Then
                Dim sb As New Text.StringBuilder()
                For i = 0 To v.Length - 1 Step 40
                    If i > 0 Then sb.AppendLine()
                    sb.Append(v.Substring(i, Math.Min(40, v.Length - i)))
                Next
                v = sb.ToString()
            End If
            Dim idx = _detail.Rows.Add(k, v)
            _detail.Rows(idx).Cells(0).Style.ForeColor = Theme.TextSub
            If k.StartsWith("原文件") Then
                _detail.Rows(idx).Cells(1).Style.Font = Theme.FontMono
                _detail.Rows(idx).Cells(1).Style.ForeColor = Theme.TextSub
            End If
        End Sub

        Private Shared Function StateText(s As SealPartState) As String
            Select Case s
                Case SealPartState.Ok : Return "✓ 通过"
                Case SealPartState.Failed : Return "× 失败"
                Case SealPartState.Warning : Return "! 注意"
                Case Else : Return "– 跳过"
            End Select
        End Function

        Private Sub OnCheckFormatting(sender As Object, e As DataGridViewCellFormattingEventArgs)
            If e.RowIndex < 0 Then Return
            Dim stateCell = _checks.Rows(e.RowIndex).Cells(1).Value
            Dim s = TryCast(stateCell, String)
            If s Is Nothing Then Return

            Dim clr As Color
            If s.StartsWith("✓") Then
                clr = Theme.Ok
            ElseIf s.StartsWith("×") Then
                clr = Theme.Danger
            ElseIf s.StartsWith("!") Then
                clr = Theme.Warn
            Else
                clr = Theme.TextFaint
            End If

            If e.ColumnIndex = 1 Then
                e.CellStyle.ForeColor = clr
                e.CellStyle.Font = Theme.FontBold
            ElseIf s.StartsWith("×") Then
                e.CellStyle.ForeColor = Theme.Danger
            ElseIf s.StartsWith("!") Then
                e.CellStyle.ForeColor = Theme.TextMain
            End If
        End Sub

        ''' <summary>外部预填并立即验证（命令行/文件关联入口）。</summary>
        Friend Sub Prefill(filePath As String, sealPath As String)
            _fileBox.Text = If(filePath, "")
            If Not String.IsNullOrEmpty(sealPath) Then _sealBox.Text = sealPath
            RunVerify()
        End Sub

        Public Sub OnShown() Implements IAppPage.OnShown
            ' 从盖章页过来时，如果那边有选中的文件/章，自动带过来一次（只在空的时候）
            If _fileBox.Text = "" AndAlso _owner.CurrentSeal IsNot Nothing AndAlso _owner.SealPath <> "" Then
                _sealBox.Text = _owner.SealPath
            End If
        End Sub

        ''' <summary>拖动结束后的统一重排；本页左右分栏比例只在首次应用，这里无需再动。</summary>
        Public Sub Relayout() Implements IAppPage.Relayout
        End Sub
    End Class

End Namespace
