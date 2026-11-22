Imports System
Imports System.Collections.Generic
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.IO
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports App.Controls
Imports SymbolPrint.Core.Carriers
Imports SymbolPrint.Core.Model
Imports SymbolPrint.Core.Service
Imports SymbolPrint.Core.Util
Imports SymbolPrint.Core.Visual

Namespace App.Pages

    ''' <summary>
    ''' 盖章页：选文件 → 选章 →（可选）贴图案 → 写入。
    ''' 所有耗时操作都在后台线程跑，进度与取消统一走 ProgressStrip。
    ''' 右栏卡片宽约 420，内部用固定坐标，保证在最小窗口（1040 宽）下也不重叠。
    ''' </summary>
    Friend NotInheritable Class StampPage
        Inherits UserControl
        Implements IAppPage

        Private Const CardInner As Integer = 14

        Private ReadOnly _owner As MainForm
        Private ReadOnly _grid As DataGridView
        Private ReadOnly _files As New List(Of String)

        Private ReadOnly _sealPath As TextBox
        Private ReadOnly _sealPass As TextBox
        Private ReadOnly _sealInfo As Label

        Private ReadOnly _visualOn As CheckBox
        Private ReadOnly _visualBtn As Button
        Private ReadOnly _markBox As TextBox
        Private ReadOnly _posX As NumericUpDown
        Private ReadOnly _posY As NumericUpDown
        Private ReadOnly _widthRatio As NumericUpDown
        Private ReadOnly _opacity As NumericUpDown
        Private ReadOnly _rotate As NumericUpDown
        Private ReadOnly _page As NumericUpDown
        Private ReadOnly _preview As MarkPreview

        Private _sealCard As Panel
        Private _visCard As Panel
        Private _sealTable As TableLayoutPanel
        Private _visTable As TableLayoutPanel
        Private _rightHost As Panel
        Private _body As SplitContainer
        Private _outer As SplitContainer
        Private _outerSplitSet As Boolean
        Private _autoScrollOff As Boolean
        Private ReadOnly _suspended As New List(Of Control)()

        Private ReadOnly _backup As CheckBox
        Private ReadOnly _tip As New ToolTip()

        Private ReadOnly _progress As ProgressStrip
        Private ReadOnly _startBtn As Button
        Private ReadOnly _logBox As RichTextBox

        Private _seal As StampFile
        Private _busy As Boolean

        Friend Sub New(owner As MainForm)
            _owner = owner
            BackColor = Theme.Bg
            DoubleBuffered = True

            ' ==================== 顶部标题 ====================
            Dim head As New TableLayoutPanel() With {
                .Dock = DockStyle.Top, .ColumnCount = 1, .RowCount = 2, .BackColor = Theme.Bg,
                .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
                .Padding = New Padding(2, 4, 2, 8)
            }
            head.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            head.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            head.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            head.Controls.Add(New Label() With {
                .Text = "盖章", .Font = Theme.FontTitle, .ForeColor = Theme.TextMain,
                .AutoSize = True, .Margin = New Padding(0, 0, 0, 2)
            }, 0, 0)
            Dim headSub As New Label() With {
                .Text = "把章写进文件的空白区：只追加、不覆盖已有内容，原文件照常打开。",
                .Font = Theme.FontBase, .ForeColor = Theme.TextSub,
                .AutoSize = True, .Margin = New Padding(0)
            }
            head.Controls.Add(headSub, 0, 1)

            ' ==================== 左侧：文件列表 ====================
            Dim leftCard = Theme.CardPanel()
            leftCard.Dock = DockStyle.Fill
            leftCard.Padding = New Padding(CardInner)

            Dim leftTable As New TableLayoutPanel() With {
                .Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 4, .BackColor = Color.Transparent
            }
            ' 行高跟着字体走：标题行/按钮行/提示行都 AutoSize，只有文件列表吃剩余空间
            leftTable.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            leftTable.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            leftTable.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
            leftTable.RowStyles.Add(New RowStyle(SizeType.AutoSize))

            Dim leftTitle = Theme.Label("待盖章文件", Theme.TextMain, Theme.FontH2)
            leftTitle.AutoSize = False
            leftTitle.Dock = DockStyle.Fill
            leftTitle.Height = Theme.LineH2 + 6
            leftTitle.TextAlign = ContentAlignment.MiddleLeft
            leftTitle.Margin = New Padding(0, 0, 0, 4)

            ' 用流式布局而不是手摆坐标：窗口变窄时按钮自动换行，不会被切掉
            Dim leftBar As New FlowLayoutPanel() With {
                .Dock = DockStyle.Fill, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
                .WrapContents = True, .BackColor = Color.Transparent, .Margin = New Padding(0, 0, 0, 6)
            }
            Dim addBtn = Theme.GhostButton("添加文件", 92)
            addBtn.Margin = New Padding(0, 0, 8, 4)
            Dim addDirBtn = Theme.GhostButton("添加文件夹", 100)
            addDirBtn.Margin = New Padding(0, 0, 8, 4)
            Dim delBtn = Theme.GhostButton("移除选中", 88)
            delBtn.Margin = New Padding(0, 0, 8, 4)
            ' 「删除章」作用于列表里选中的文件：把文件上已经盖的章摘掉（不是卸载当前章文件）
            Dim eraseBtn = Theme.GhostButton("删除章", 84)
            eraseBtn.Margin = New Padding(0, 0, 8, 4)
            Dim clearBtn = Theme.GhostButton("清空", 64)
            clearBtn.Margin = New Padding(0, 0, 0, 4)
            leftBar.Controls.Add(addBtn)
            leftBar.Controls.Add(addDirBtn)
            leftBar.Controls.Add(delBtn)
            leftBar.Controls.Add(eraseBtn)
            leftBar.Controls.Add(clearBtn)

            _grid = New DataGridView() With {.Dock = DockStyle.Fill, .Margin = New Padding(0)}
            Theme.StyleGrid(_grid)
            ' 批量操作（移除选中 / 删除章）需要一次选多行；只对本页开启，不影响验章页
            _grid.MultiSelect = True
            _grid.Columns.Add(MakeCol("name", "文件", 42))
            _grid.Columns.Add(MakeCol("size", "大小", 18))
            _grid.Columns.Add(MakeCol("carrier", "写入位置", 24))
            _grid.Columns.Add(MakeCol("state", "状态", 16))
            AddHandler _grid.CellFormatting, AddressOf OnCellFormatting
            AddHandler _grid.SelectionChanged, Sub(s, e)
                                                   If _preview IsNot Nothing Then UpdatePreviewSource()
                                               End Sub

            Dim dropHint As New Label() With {
                .Text = "也可以直接把文件或一批文件拖进这里",
                .Font = New Font("Microsoft YaHei UI", 8.0F), .ForeColor = Theme.TextFaint,
                .AutoSize = True, .Margin = New Padding(0, 4, 0, 0)
            }

            leftTable.Controls.Add(leftTitle, 0, 0)
            leftTable.Controls.Add(leftBar, 0, 1)
            leftTable.Controls.Add(_grid, 0, 2)
            leftTable.Controls.Add(dropHint, 0, 3)
            leftCard.Controls.Add(leftTable)

            AddHandler addBtn.Click, Sub(s, e) PickFiles()
            AddHandler addDirBtn.Click, Sub(s, e) PickFolder()
            AddHandler delBtn.Click, Sub(s, e) RemoveSelected()
            AddHandler eraseBtn.Click, Sub(s, e) EraseSealFromSelected()
            AddHandler clearBtn.Click, Sub(s, e)
                                           _files.Clear()
                                           RefreshGrid()
                                       End Sub

            ' ==================== 右侧：设置 ====================
            ' 右栏：普通 Panel + AutoScroll。卡片 Dock=Top、高度显式给定——
            ' 千万别用 TableLayoutPanel 的 AutoSize 行装卡片：空间不够时它会"压缩"行高，
            ' 表现就是把卡片最后一行切在外面，而且不给滚动条。
            Dim rightHost As New Panel() With {
                .Dock = DockStyle.Fill, .AutoScroll = True, .BackColor = Theme.Bg,
                .Padding = New Padding(0, 0, 2, 0)
            }
            _rightHost = rightHost
            ' Dock=Top 的卡片按 ClientSize 撑宽，但 AutoScroll 面板的 ClientSize 不含竖滚动条，
            ' 于是永远差一条滚动条的宽度 → 冒出横向滚动条。竖滚动条一出现就把右内边距补上。
            AddHandler rightHost.Resize, Sub(s, e)
                                             Using New Prof.T("stamp.rightHost.Resize")
                                                 SyncRightPadding()
                                             End Using
                                         End Sub
            AddHandler rightHost.Layout, Sub(s, e)
                                             Using New Prof.T("stamp.rightHost.Layout")
                                                 SyncRightPadding()
                                             End Using
                                         End Sub

            ' 拖动期间挂起整棵子树的布局：判别实验证明卡顿来自"布局"（134ms/次）而不是绘制（43ms/帧）。
            ' 只挂 rightHost 没用——它的子卡片（Dock=Top）和卡片里的 AutoSize 表格会各自继续重排。
            ' 所以这里把 rightHost / leftCard 及其 3 层子控件全部挂起，松手后统一恢复一次。
            AddHandler UiState.ResizeBegin, Sub(s, e)
                                                If Not _autoScrollOff Then
                                                    _autoScrollOff = True
                                                    SuspendDeep(rightHost)
                                                    SuspendDeep(leftCard)
                                                End If
                                            End Sub
            AddHandler UiState.ResizeEnd, Sub(s, e)
                                              If _autoScrollOff Then
                                                  _autoScrollOff = False
                                                  ResumeDeep()
                                                  SyncRightPadding()
                                              End If
                                          End Sub

            ' ---------- 卡片 1：印章文件 ----------
            Dim sealCard = Theme.CardPanel()
            _sealCard = sealCard
            sealCard.Dock = DockStyle.Top
            sealCard.Height = 150
            sealCard.Padding = New Padding(CardInner)
            sealCard.Margin = New Padding(0, 0, 0, 12)

            Dim sealTable As New TableLayoutPanel() With {
                .Dock = DockStyle.Top, .ColumnCount = 3, .RowCount = 4,
                .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
                .BackColor = Color.Transparent, .Margin = New Padding(0)
            }
            _sealTable = sealTable
            ' 表格排完就同步卡片高度（只在真的不一样时改，避免来回触发）
            AddHandler sealTable.Layout, Sub(s, e)
                                             Using New Prof.T("stamp.sealTable.Layout")
                                                 Dim want = sealTable.Height + CardInner * 2
                                                 If sealCard.Height <> want Then sealCard.Height = want
                                             End Using
                                         End Sub
            sealTable.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
            sealTable.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            sealTable.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
            For i = 0 To 3
                sealTable.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            Next

            Dim sealTitle = Theme.Label("印章文件", Theme.TextMain, Theme.FontH2)
            sealTitle.AutoSize = False
            sealTitle.Dock = DockStyle.Fill
            sealTitle.Height = Theme.LineH2 + 6
            sealTitle.TextAlign = ContentAlignment.MiddleLeft
            sealTitle.Margin = New Padding(0, 0, 0, 4)

            _sealPath = Theme.TextBox2()
            _sealPath.ReadOnly = True
            _sealPath.BackColor = Color.FromArgb(250, 251, 253)
            _sealPath.Font = Theme.FontMono
            _sealPath.Dock = DockStyle.Fill
            _sealPath.Margin = New Padding(0, 3, 8, 3)

            Dim sealPick = Theme.GhostButton("选择…", 80)
            sealPick.Dock = DockStyle.Top
            sealPick.Margin = New Padding(0, 4, 0, 4)

            Dim passLabel = Theme.Label("口令", Theme.TextSub)
            passLabel.AutoSize = True
            passLabel.Anchor = AnchorStyles.Left
            passLabel.TextAlign = ContentAlignment.MiddleLeft
            passLabel.Margin = New Padding(0, 0, 6, 0)

            _sealPass = Theme.TextBox2()
            _sealPass.UseSystemPasswordChar = True
            _sealPass.PlaceholderText = "未加密则留空"
            _sealPass.Dock = DockStyle.Fill
            _sealPass.Margin = New Padding(0, 3, 8, 3)

            Dim sealLoad = Theme.GhostButton("载入", 80)
            sealLoad.Dock = DockStyle.Top
            sealLoad.Margin = New Padding(0, 4, 0, 4)

            _sealInfo = New Label() With {
                .AutoSize = True, .Dock = DockStyle.Top,
                .Font = Theme.FontBase, .ForeColor = Theme.TextSub,
                .Text = "尚未选择章文件。", .Margin = New Padding(0, 4, 0, 0)
            }

            sealTable.Controls.Add(sealTitle, 0, 0)
            sealTable.SetColumnSpan(sealTitle, 3)
            sealTable.Controls.Add(_sealPath, 0, 1)
            sealTable.SetColumnSpan(_sealPath, 2)
            sealTable.Controls.Add(sealPick, 2, 1)
            sealTable.Controls.Add(passLabel, 0, 2)
            sealTable.Controls.Add(_sealPass, 1, 2)
            sealTable.Controls.Add(sealLoad, 2, 2)

            sealTable.Controls.Add(_sealInfo, 0, 3)
            sealTable.SetColumnSpan(_sealInfo, 3)
            sealCard.Controls.Add(sealTable)

            AddHandler sealPick.Click, Sub(s, e) PickSeal()
            AddHandler sealLoad.Click, Sub(s, e) LoadSeal()

            ' ---------- 卡片 2：可视化盖章 ----------
            Dim visCard = Theme.CardPanel()
            _visCard = visCard
            visCard.Dock = DockStyle.Top
            visCard.Padding = New Padding(CardInner)
            visCard.Margin = New Padding(0)

            ' 行高全部显式：AutoSize 行在「Percent 列」的表格里被测量成 0，
            ' 会把备份勾选和说明文字切到卡外。这里按字体实测值推出来，DPI 放大也同步。
            Dim rowTitle = Math.Max(30, Theme.LineH2 + 10)
            Dim rowCheck = Theme.LineH + 10
            Dim rowMark = Theme.InputH + 10
            Dim rowPreview = 248
            Dim rowHint = Theme.LineH * 4 + 8

            Dim visTable As New TableLayoutPanel() With {
                .Dock = DockStyle.Top, .ColumnCount = 2, .RowCount = 6,
                .Height = rowTitle + rowCheck + rowMark + rowPreview + rowCheck + rowHint,
                .BackColor = Color.Transparent, .Margin = New Padding(0)
            }
            _visTable = visTable
            visTable.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 220))
            visTable.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            visTable.RowStyles.Add(New RowStyle(SizeType.Absolute, rowTitle))
            visTable.RowStyles.Add(New RowStyle(SizeType.Absolute, rowCheck))
            visTable.RowStyles.Add(New RowStyle(SizeType.Absolute, rowMark))
            visTable.RowStyles.Add(New RowStyle(SizeType.Absolute, rowPreview))
            visTable.RowStyles.Add(New RowStyle(SizeType.Absolute, rowCheck))
            visTable.RowStyles.Add(New RowStyle(SizeType.Absolute, rowHint))

            Dim visTitle = Theme.Label("可视化盖章（可选）", Theme.TextMain, Theme.FontH2)
            visTitle.AutoSize = False
            visTitle.Dock = DockStyle.Fill
            visTitle.Height = Theme.LineH2 + 6
            visTitle.TextAlign = ContentAlignment.MiddleLeft
            visTitle.Margin = New Padding(0, 0, 0, 4)

            _visualBtn = Theme.PrimaryButton("可视化盖章…", 132)
            _visualBtn.Dock = DockStyle.Right
            _visualBtn.Height = 28
            _visualBtn.Margin = New Padding(0, 0, 0, 4)
            AddHandler _visualBtn.Click, Sub(s, e) OpenVisualSeal()

            _visualOn = New CheckBox() With {
                .Text = "在图片 / PDF 上贴出图案", .AutoSize = True, .Dock = DockStyle.Top,
                .TextAlign = ContentAlignment.MiddleLeft, .Margin = New Padding(0, 2, 0, 6),
                .ForeColor = Theme.TextMain, .Font = Theme.FontBase
            }

            ' 图案行：标签 | 输入框 | 选择按钮
            Dim markRow As New TableLayoutPanel() With {
                .Dock = DockStyle.Fill, .ColumnCount = 3, .RowCount = 1,
                .BackColor = Color.Transparent, .Margin = New Padding(0)
            }
            markRow.RowStyles.Add(New RowStyle(SizeType.Absolute, Theme.InputH + 8))
            markRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
            markRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            markRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))

            Dim markLabel = Theme.Label("图案（svg / png）", Theme.TextSub)
            markLabel.AutoSize = True
            markLabel.TextAlign = ContentAlignment.MiddleLeft
            markLabel.Margin = New Padding(0, 6, 8, 0)

            _markBox = Theme.TextBox2()
            _markBox.Font = Theme.FontMono
            _markBox.Dock = DockStyle.Fill
            _markBox.Margin = New Padding(0, 3, 8, 3)

            Dim markPick = Theme.GhostButton("选择…", 80)
            markPick.Dock = DockStyle.Top
            markPick.Margin = New Padding(0, 4, 0, 4)

            markRow.Controls.Add(markLabel, 0, 0)
            markRow.Controls.Add(_markBox, 1, 0)
            markRow.Controls.Add(markPick, 2, 0)

            ' 左侧参数表：标签 | 数值
            Dim paramTable As New TableLayoutPanel() With {
                .Dock = DockStyle.Top, .Height = 204, .ColumnCount = 2, .RowCount = 6,
                .BackColor = Color.Transparent, .Margin = New Padding(0, 4, 12, 0)
            }
            paramTable.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
            paramTable.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            For i = 0 To 5
                paramTable.RowStyles.Add(New RowStyle(SizeType.Absolute, 34))
            Next

            _posX = MakePercentBox(0.72)
            _posY = MakePercentBox(0.78)
            _widthRatio = MakePercentBox(0.22, 2, 100)
            _opacity = MakePercentBox(owner.Settings.Image.OpacityPercent / 100.0, 0, 100)
            _rotate = MakeNumberBox(owner.Settings.Image.RotationDegrees, -180, 180, 0)
            _page = MakeNumberBox(1, 1, 9999, 0)

            AddParamRow(paramTable, 0, "水平位置 %", _posX)
            AddParamRow(paramTable, 1, "垂直位置 %", _posY)
            AddParamRow(paramTable, 2, "章宽度 %", _widthRatio)
            AddParamRow(paramTable, 3, "不透明度 %", _opacity)
            AddParamRow(paramTable, 4, "旋转角 °", _rotate)
            AddParamRow(paramTable, 5, "PDF 页码", _page)

            _preview = New MarkPreview() With {
                .Dock = DockStyle.Fill, .Margin = New Padding(0, 6, 0, 0),
                .MinimumSize = New Size(220, 200)
            }
            AddHandler _preview.PositionChanged, Sub(s, e) SyncPreviewToInputs()

            _backup = New CheckBox() With {
                .Text = "贴图前备份原文件（" & If(String.IsNullOrEmpty(owner.Settings.General.BackupSuffix), ".bak", owner.Settings.General.BackupSuffix) & "）",
                .AutoSize = True, .Dock = DockStyle.Top,
                .TextAlign = ContentAlignment.MiddleLeft, .Margin = New Padding(0, 2, 0, 4),
                .Checked = owner.Settings.General.AutoBackupOriginal, .ForeColor = Theme.TextMain, .Font = Theme.FontBase
            }

            Dim visHint As New Label() With {
                .Text = "拖动预览里的章可调位置，拖右下角改大小；PDF 页码填 0 表示全部页。预览用当前选中文件的真实页面作底图，看到什么就贴成什么。",
                .ForeColor = Theme.TextFaint, .Font = New Font("Microsoft YaHei UI", 8.0F),
                .AutoSize = True, .Dock = DockStyle.Top, .Margin = New Padding(0, 4, 0, 0)
            }

            visTable.Controls.Add(visTitle, 0, 0)
            visTable.Controls.Add(_visualBtn, 1, 0)
            visTable.Controls.Add(_visualOn, 0, 1)
            visTable.SetColumnSpan(_visualOn, 2)
            visTable.Controls.Add(markRow, 0, 2)
            visTable.SetColumnSpan(markRow, 2)
            visTable.Controls.Add(paramTable, 0, 3)
            visTable.Controls.Add(_preview, 1, 3)
            visTable.Controls.Add(_backup, 0, 4)
            visTable.SetColumnSpan(_backup, 2)
            visTable.Controls.Add(visHint, 0, 5)
            visTable.SetColumnSpan(visHint, 2)

            visCard.Height = visTable.Height + CardInner * 2
            visCard.Controls.Add(visTable)
            AddHandler markPick.Click, Sub(s, e) PickMark()

            ' Dock=Top 时后添加的在上：先挂可视化卡，再把印章卡挂上去（印章卡在上）
            rightHost.Controls.Add(visCard)
            rightHost.Controls.Add(sealCard)

            ' ==================== 底部：进度 + 按钮 + 日志 ====================
            ' 底部日志区：Dock=Fill 放进外层 SplitContainer 的下半部分，高度可拖。
            ' 以前是 Dock.Bottom 写死 LineH*12+26（约 266px），右栏永远要滚动才能看到最后一行。
            Dim bottom As New Panel() With {.Dock = DockStyle.Fill, .BackColor = Theme.Bg}

            _progress = New ProgressStrip(owner.Settings.Progress.DelayMs, owner.Settings.Progress.ThrottleMs, owner.Settings.Progress)
            _progress.Dock = DockStyle.Top

            Dim actRow As New FlowLayoutPanel() With {
                .Dock = DockStyle.Fill, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
                .WrapContents = False, .BackColor = Theme.Bg, .Margin = New Padding(0, 4, 0, 6)
            }
            _startBtn = Theme.PrimaryButton("开始盖章", 128)
            _startBtn.Margin = New Padding(0, 0, 10, 0)
            Dim clearLogBtn = Theme.GhostButton("清空日志", 88)
            clearLogBtn.Margin = New Padding(0)
            actRow.Controls.Add(_startBtn)
            actRow.Controls.Add(clearLogBtn)

            _logBox = New RichTextBox() With {
                .ReadOnly = True, .ScrollBars = RichTextBoxScrollBars.Vertical, .DetectUrls = False,
                .WordWrap = True,
                .Font = Theme.FontMono, .BorderStyle = BorderStyle.FixedSingle,
                .BackColor = Color.FromArgb(250, 251, 253), .ForeColor = Theme.TextSub,
                .Dock = DockStyle.Fill
            }
            Dim logWrap As New Panel() With {.Dock = DockStyle.Fill, .Padding = New Padding(0, 4, 0, 2), .BackColor = Theme.Bg}
            logWrap.Controls.Add(_logBox)

            Dim bottomTable As New TableLayoutPanel() With {
                .Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 3, .BackColor = Theme.Bg
            }
            ' 三行：进度条（按内容）/ 按钮行（按内容）/ 日志（吃掉剩余高度）
            ' 注意顺序别写反：之前把「Percent 100」给了按钮行、日志给了 AutoSize，
            ' 结果是日志被压成一条缝、按钮行撑满还画到区域外面去。
            bottomTable.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            bottomTable.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            bottomTable.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
            bottomTable.Controls.Add(_progress, 0, 0)
            bottomTable.Controls.Add(actRow, 0, 1)
            bottomTable.Controls.Add(logWrap, 0, 2)

            bottom.Controls.Add(bottomTable)
            AddHandler _startBtn.Click, Sub(s, e) StartStamp()
            AddHandler clearLogBtn.Click, Sub(s, e) _logBox.Clear()

            ' ==================== 组装 ====================
            Dim body As New SplitContainer() With {
                .Dock = DockStyle.Fill, .Orientation = Orientation.Vertical,
                .SplitterWidth = 14, .BackColor = Theme.Bg, .BorderStyle = BorderStyle.None
            }
            body.Panel1.Controls.Add(leftCard)
            body.Panel2.Controls.Add(rightHost)
            body.Panel1.BackColor = Theme.Bg
            body.Panel2.BackColor = Theme.Bg
            _body = body

            ' 竖向分割：上半是文件列表 + 右栏，下半是日志；中间的分隔条可以拖，
            ' 想多看预览就把日志压小一点，想多看日志就往下拖。
            Dim outer As New SplitContainer() With {
                .Dock = DockStyle.Fill, .Orientation = Orientation.Horizontal,
                .SplitterWidth = 8, .BackColor = Theme.Bg, .BorderStyle = BorderStyle.None,
                .Panel1MinSize = 240, .Panel2MinSize = 92
            }
            outer.Panel1.Controls.Add(body)
            outer.Panel2.Controls.Add(bottom)
            outer.Panel1.BackColor = Theme.Bg
            outer.Panel2.BackColor = Theme.Bg
            _outer = outer

            ' 首次布局给日志区一个偏小的默认高度（约 5 行），把空间让给预览
            AddHandler outer.Resize, Sub(s, e)
                                         Using New Prof.T("stamp.outer.Resize")
                                             If UiState.IsUserResizing Then Return
                                             ApplyOuterSplit()
                                         End Using
                                     End Sub

            Controls.Add(outer)
            Controls.Add(head)

            ' 每次尺寸变化都重算（不是只算一次）：右栏固定约 780，其余给文件列表，
            ' 且两侧都留最小宽度，窗口再窄也不会把左边挤成一条缝。
            ' 构造阶段容器还没有真实宽度，设最小尺寸会因越界抛异常——首次布局完成后再设
            AddHandler body.Resize, Sub(s, e)
                                        Using New Prof.T("stamp.body.Resize")
                                            If UiState.IsUserResizing Then Return
                                            ApplyBodySplit()
                                        End Using
                                    End Sub


            Theme.WrapHint(headSub, Me)
            Theme.WrapHint(dropHint, leftCard)
            Theme.WrapHint(_sealInfo, sealCard)
            Theme.WrapHint(visHint, visCard)

            AllowDrop = True
            AddHandler DragEnter, AddressOf OnDragEnter
            AddHandler DragDrop, AddressOf OnDragDrop
            AddHandler _visualOn.CheckedChanged, Sub(s, e) UpdateVisualEnabled()
            For Each n As NumericUpDown In New NumericUpDown() {_posX, _posY, _widthRatio, _opacity, _rotate}
                AddHandler n.ValueChanged, Sub(s, e) SyncInputsToPreview()
            Next
            AddHandler _markBox.TextChanged, Sub(s, e)
                                                 If _visualOn.Checked Then UpdateVisualEnabled()
                                             End Sub

            RefreshGrid()
            UpdateVisualEnabled()
        End Sub

        ''' <summary>竖滚动条出现时补右内边距，避免多出一条横向滚动条。</summary>
        Private Sub SyncRightPadding()
            If _rightHost Is Nothing OrElse _rightHost.IsDisposed Then Return
            Dim want = If(_rightHost.VerticalScroll.Visible, SystemInformation.VerticalScrollBarWidth + 2, 2)
            If _rightHost.Padding.Right <> want Then
                _rightHost.Padding = New Padding(_rightHost.Padding.Left, _rightHost.Padding.Top, want, _rightHost.Padding.Bottom)
            End If
        End Sub

        ''' <summary>数值框 → 预览（拖动预览时走 SyncPreviewToInputs，方向相反）。</summary>
        Private Sub SyncInputsToPreview()
            If _preview Is Nothing Then Return
            _preview.OpacityValue = CDbl(_opacity.Value) / 100.0
            _preview.RotationDegrees = CDbl(_rotate.Value)
            _preview.UpdateFromInputs(CDbl(_posX.Value) / 100.0, CDbl(_posY.Value) / 100.0, CDbl(_widthRatio.Value) / 100.0)
        End Sub

        ' ==================== 控件工厂 ====================

        Private Shared Function MakeCol(name As String, header As String, fillWeight As Integer) As DataGridViewColumn
            Dim c As New DataGridViewTextBoxColumn()
            c.Name = name
            c.HeaderText = header
            c.FillWeight = fillWeight
            ' 列最小宽度：避免窗口一窄就把列压到只剩几个像素、表头挤成竖排
            c.MinimumWidth = Math.Max(56, TextRenderer.MeasureText(header, Theme.FontBold).Width + 22)
            c.SortMode = DataGridViewColumnSortMode.NotSortable
            Return c
        End Function

        ''' <summary>0–100 百分比输入框；initial 传 0–1 的比例。</summary>
        Private Shared Function MakePercentBox(initial As Double, Optional min As Double = 0, Optional max As Double = 100) As NumericUpDown
            Dim n As New NumericUpDown()
            n.Minimum = CDec(min) : n.Maximum = CDec(max)
            n.DecimalPlaces = 0
            n.Value = CDec(Math.Round(Math.Max(min, Math.Min(max, initial * 100))))
            n.Font = Theme.FontBase
            n.Dock = DockStyle.Fill
            n.Margin = New Padding(0, 4, 0, 4)
            n.TextAlign = HorizontalAlignment.Right
            Return n
        End Function

        Private Shared Function MakeNumberBox(initial As Integer, min As Integer, max As Integer, decimals As Integer) As NumericUpDown
            Dim n As New NumericUpDown()
            n.Minimum = min : n.Maximum = max
            n.DecimalPlaces = decimals
            n.Value = Math.Max(min, Math.Min(max, initial))
            n.Font = Theme.FontBase
            n.Dock = DockStyle.Fill
            n.Margin = New Padding(0, 4, 0, 4)
            n.TextAlign = HorizontalAlignment.Right
            Return n
        End Function

        ''' <summary>往参数表里加一行：左标签、右数值框。</summary>
        Private Shared Sub AddParamRow(t As TableLayoutPanel, row As Integer, text As String, box As Control)
            Dim l = Theme.Label(text, Theme.TextSub)
            l.AutoSize = False
            l.Dock = DockStyle.Fill
            l.TextAlign = ContentAlignment.MiddleLeft
            l.Margin = New Padding(0, 0, 6, 0)
            t.Controls.Add(l, 0, row)
            t.Controls.Add(box, 1, row)
        End Sub

        Private Sub AppendLog(msg As String)
            If _logBox Is Nothing OrElse _logBox.IsDisposed Then Return
            If _logBox.InvokeRequired Then
                _logBox.BeginInvoke(New Action(Of String)(AddressOf AppendLog), msg)
                Return
            End If
            Dim clr As Color = Theme.TextSub
            If msg.StartsWith("√", StringComparison.Ordinal) Then
                clr = Theme.Ok
            ElseIf msg.StartsWith("×", StringComparison.Ordinal) Then
                clr = Theme.Danger
            ElseIf msg.StartsWith("!", StringComparison.Ordinal) Then
                clr = Theme.Warn
            End If
            Dim line = $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}"
            _logBox.SelectionStart = _logBox.TextLength
            _logBox.SelectionLength = 0
            _logBox.SelectionColor = clr
            _logBox.AppendText(line)
            _logBox.SelectionStart = _logBox.TextLength
            _logBox.ScrollToCaret()
        End Sub

        ' ==================== 文件列表 ====================

        Private Sub PickFiles()
            Using d As New OpenFileDialog()
                d.Title = "选择要盖章的文件"
                d.Multiselect = True
                d.Filter = BuildFilter()
                If d.ShowDialog(FindForm()) = DialogResult.OK Then AddFiles(d.FileNames)
            End Using
        End Sub

        Private Shared Function BuildFilter() As String
            Dim exts = CarrierRegistry.SupportedExtensions()
            Dim list = String.Join(";", exts.Select(Function(e) "*" & e))
            Return "支持的文件|" & list & "|所有文件|*.*"
        End Function

        Private Sub PickFolder()
            Using d As New FolderBrowserDialog()
                d.Description = "选择文件夹（只加入受支持的格式，不递归子目录）"
                If d.ShowDialog(FindForm()) = DialogResult.OK Then
                    Dim exts = New HashSet(Of String)(CarrierRegistry.SupportedExtensions(), StringComparer.OrdinalIgnoreCase)
                    Dim found = Directory.EnumerateFiles(d.SelectedPath).
                        Where(Function(f) exts.Contains(Path.GetExtension(f).ToLowerInvariant())).
                        Take(2000).ToArray()
                    AddFiles(found)
                End If
            End Using
        End Sub

        Private Sub AddFiles(paths As IEnumerable(Of String))
            Dim added = 0
            For Each p In paths
                If String.IsNullOrWhiteSpace(p) OrElse Not File.Exists(p) Then Continue For
                If _files.Any(Function(x) String.Equals(x, p, StringComparison.OrdinalIgnoreCase)) Then Continue For
                _files.Add(p)
                added += 1
            Next
            If added > 0 Then
                RefreshGrid()
                AppendLog($"已加入 {added} 个文件，共 {_files.Count} 个。")
            End If
        End Sub

        Private Sub RemoveSelected()
            Dim idx = New List(Of Integer)()
            For Each row As DataGridViewRow In _grid.SelectedRows
                idx.Add(row.Index)
            Next
            idx.Sort()
            idx.Reverse()
            For Each i In idx
                If i >= 0 AndAlso i < _files.Count Then _files.RemoveAt(i)
            Next
            RefreshGrid()
        End Sub

        ''' <summary>文件里是否已有本程序的盖章记录（只读尾部/注释区，不写文件）。</summary>
        Private Shared Function HasExistingRecord(path As String) As Boolean
            Try
                Dim c = CarrierRegistry.Find(path)
                If c Is Nothing Then Return False
                Return c.ReadBlob(path) IsNot Nothing
            Catch
                Return False
            End Try
        End Function

        ''' <summary>
        ''' 「删除章」：把列表里选中文件上已经盖的章摘掉，恢复成未盖章。
        ''' 因为盖章默认保留旧记录，一个文件可能叠了多条，这里摘到干净为止。
        ''' 只动我们自己写进去的那几段字节，文件其余内容不碰。
        ''' </summary>
        Private Async Sub EraseSealFromSelected()
            If _busy Then Return

            Dim idx As New List(Of Integer)()
            For Each row As DataGridViewRow In _grid.SelectedRows
                idx.Add(row.Index)
            Next
            idx.Sort()
            If idx.Count = 0 Then
                MessageBox.Show("请先在左边列表里选中要删除章的文件（可按住 Ctrl 多选）。", "SymbolPrint",
                                MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If

            Dim names = idx.Where(Function(i) i >= 0 AndAlso i < _files.Count).
                            Select(Function(i) IO.Path.GetFileName(_files(i))).ToArray()
            Dim ask = $"将从 {idx.Count} 个文件上删除已盖的章：" & Environment.NewLine &
                      String.Join("、", names.Take(3)) & If(names.Length > 3, " 等", "") & Environment.NewLine & Environment.NewLine &
                      "只摘除本程序写入的记录，文件其它内容不变；章删除后需要重新盖章才能恢复。是否继续？"
            If MessageBox.Show(ask, "删除章", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                               MessageBoxDefaultButton.Button2) <> DialogResult.Yes Then Return

            Dim okN = 0, noneN = 0, failN = 0
            For Each i In idx
                If i < 0 OrElse i >= _files.Count Then Continue For
                Dim f = _files(i)
                Try
                    SetRowState(i, "删除中…")
                    ' 摘章会改文件，放到后台线程，界面不卡
                    Dim removed = Await Task.Run(Function() SealService.RemoveAllSeals(f))
                    If removed > 0 Then
                        okN += 1
                        SetRowState(i, "未盖章")
                        AppendLog($"√ {IO.Path.GetFileName(f)}：已删除 {removed} 条章记录")
                    Else
                        noneN += 1
                        SetRowState(i, "未盖章")
                        AppendLog($"○ {IO.Path.GetFileName(f)}：没有找到已盖的章")
                    End If
                Catch ex As Exception
                    failN += 1
                    SetRowState(i, "删除失败")
                    AppendLog($"× {IO.Path.GetFileName(f)}：{ex.Message}")
                End Try
            Next

            AppendLog($"删除章完成：成功 {okN}，本来无章 {noneN}，失败 {failN}")
            UpdatePreviewSource()
        End Sub

        Private Sub RefreshGrid()
            _grid.Rows.Clear()
            For Each f In _files
                Dim fi As New FileInfo(f)
                Dim carrier = CarrierRegistry.Find(f)
                Dim carrierName = If(carrier Is Nothing, "不支持", carrier.DisplayName)
                Dim state = If(carrier Is Nothing, "跳过", "待盖章")
                _grid.Rows.Add(fi.Name, Comp.HumanSize(fi.Length), carrierName, state)
            Next
            If _grid.Rows.Count > 0 Then
                _grid.Rows(0).Selected = True
                _grid.CurrentCell = _grid.Rows(0).Cells(0)
            Else
                _grid.ClearSelection()
            End If
            UpdatePreviewSource()
        End Sub

        ''' <summary>列表里当前选中的文件（用于预览真实页面）。</summary>
        Private Function SelectedFile() As String
            Dim row = _grid.CurrentRow
            If row Is Nothing Then row = _grid.Rows.Cast(Of DataGridViewRow)().FirstOrDefault(Function(r) r.Selected)
            If row Is Nothing Then Return ""
            Dim i = row.Index
            If i < 0 OrElse i >= _files.Count Then Return ""
            Return _files(i)
        End Function

        ''' <summary>
        ''' 把选中文件变成预览底图：图片直接显示内容，PDF 读真实页面尺寸，
        ''' 其它格式说明"没有页面图像"。这样预览和最终写入是同一套几何。
        ''' </summary>
        Private Sub UpdatePreviewSource()
            Dim f = SelectedFile()
            If String.IsNullOrEmpty(f) Then
                _preview.SetPageShape(0, 0, "")
                Return
            End If

            Dim name = Path.GetFileName(f)
            Dim ext = Path.GetExtension(f).ToLowerInvariant()

            Try
                If ext = ".pdf" Then
                    Using doc = PdfSharp.Pdf.IO.PdfReader.Open(f, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import)
                        If doc.PageCount > 0 Then
                            Dim pg = doc.Pages(0)
                            _preview.SetPageShape(pg.Width.Point, pg.Height.Point,
                                                  $"{name} · 第 1 页 {pg.Width.Point:0}×{pg.Height.Point:0} pt")
                        Else
                            _preview.SetPageShape(0, 0, name)
                        End If
                    End Using
                    Return
                End If

                If VisualMarkApplier.CanApply(f) Then
                    Using fs As New FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                        Using src = Image.FromStream(fs)
                            Dim ow = src.Width, oh = src.Height
                            Dim k = Math.Min(1.0, 900.0 / Math.Max(1, Math.Max(ow, oh)))
                            Dim w = Math.Max(1, CInt(ow * k))
                            Dim h = Math.Max(1, CInt(oh * k))
                            Using small As New Bitmap(w, h)
                                Using g = Graphics.FromImage(small)
                                    g.InterpolationMode = InterpolationMode.HighQualityBicubic
                                    g.PixelOffsetMode = PixelOffsetMode.HighQuality
                                    g.DrawImage(src, New Rectangle(0, 0, w, h))
                                End Using
                                _preview.SetPageImage(small, $"{name} · {ow}×{oh} px")
                            End Using
                        End Using
                    End Using
                    Return
                End If

                _preview.SetPageShape(0, 0, $"{name} · 该格式没有页面图像，只写入数据")
            Catch ex As Exception
                _preview.SetPageShape(0, 0, $"{name} · 无法预览（{ex.GetType().Name}）")
            End Try
        End Sub

        Private Sub OnCellFormatting(sender As Object, e As DataGridViewCellFormattingEventArgs)
            If e.RowIndex < 0 OrElse e.ColumnIndex <> 3 Then Return
            Select Case TryCast(e.Value, String)
                Case "跳过" : e.CellStyle.ForeColor = Theme.TextFaint
                Case "完成" : e.CellStyle.ForeColor = Theme.Ok
                Case "失败" : e.CellStyle.ForeColor = Theme.Danger
                Case "取消" : e.CellStyle.ForeColor = Theme.Warn
            End Select
        End Sub

        ' ==================== 拖放 ====================

        Private Sub OnDragEnter(sender As Object, e As DragEventArgs)
            e.Effect = If(e.Data IsNot Nothing AndAlso e.Data.GetDataPresent(DataFormats.FileDrop),
                          DragDropEffects.Copy, DragDropEffects.None)
        End Sub

        Private Sub OnDragDrop(sender As Object, e As DragEventArgs)
            If e.Data Is Nothing OrElse Not e.Data.GetDataPresent(DataFormats.FileDrop) Then Return
            Dim items = TryCast(e.Data.GetData(DataFormats.FileDrop), String())
            If items Is Nothing Then Return

            Dim files As New List(Of String)
            Dim exts = New HashSet(Of String)(CarrierRegistry.SupportedExtensions(), StringComparer.OrdinalIgnoreCase)
            For Each it In items
                If Directory.Exists(it) Then
                    For Each f In Directory.EnumerateFiles(it).Take(2000)
                        If exts.Contains(Path.GetExtension(f).ToLowerInvariant()) Then files.Add(f)
                    Next
                ElseIf File.Exists(it) Then
                    files.Add(it)
                End If
            Next
            AddFiles(files)
        End Sub

        ' ==================== 章 ====================

        Private Sub PickSeal()
            Using d As New OpenFileDialog()
                d.Title = "选择章文件"
                d.Filter = "SymbolPrint 章|*.bin|所有文件|*.*"
                If d.ShowDialog(FindForm()) = DialogResult.OK Then
                    _sealPath.Text = d.FileName
                    _seal = Nothing
                    _sealInfo.ForeColor = Theme.TextSub
                    _sealInfo.Text = "已选择，点「载入」读取。"
                End If
            End Using
        End Sub

        Private Sub LoadSeal()
            Dim p = _sealPath.Text
            If String.IsNullOrWhiteSpace(p) OrElse Not File.Exists(p) Then
                _sealInfo.ForeColor = Theme.Warn
                _sealInfo.Text = "请先选择章文件。"
                Return
            End If

            Try
                Cursor = Cursors.WaitCursor
                Dim seal = StampFile.Load(p, _sealPass.Text)
                _seal = seal

                If seal.IsLocked Then
                    _sealInfo.ForeColor = Theme.Warn
                    _sealInfo.Text = "章文件正确，但私钥受口令保护。" & Environment.NewLine &
                                     "请在上方输入口令后重新点「载入」。"
                    _owner.SetSeal(p, Nothing)
                    Return
                End If

                _sealInfo.ForeColor = Theme.TextSub
                ' 「显示章描述」关掉时，描述那行整行不出现（有些描述很长，会把卡片撑高）
                Dim descLine = If(_owner.Settings.Ui.ShowStampDescription,
                                   Environment.NewLine & $"描述：{If(seal.Description = "", "（无）", seal.Description)}", "")
                _sealInfo.Text = $"标签：{If(seal.Label = "", "（无）", seal.Label)}" & Environment.NewLine &
                                 $"指纹：{seal.StampIdShort}" & descLine &
                                 If(seal.HasSvg, Environment.NewLine & "含内置图案，可直接用于贴图。", "")

                _owner.SetSeal(p, seal)
                AppendLog($"章已载入：{seal.Label}（{seal.StampIdShort}）")

                If seal.HasSvg AndAlso String.IsNullOrWhiteSpace(_markBox.Text) Then
                    _markBox.Text = "（使用章内图案）"
                    _visualOn.Checked = True
                End If

                UpdateVisualEnabled()
            Catch ex As Exception
                _seal = Nothing
                _owner.SetSeal(p, Nothing)
                _sealInfo.ForeColor = Theme.Danger
                _sealInfo.Text = "载入失败：" & ex.Message
            Finally
                Cursor = Cursors.Default
            End Try
        End Sub

        ' ==================== 图案 ====================

        Private Sub PickMark()
            Using d As New OpenFileDialog()
                d.Title = "选择图案"
                d.Filter = "SVG 或 PNG|*.svg;*.png|SVG|*.svg|PNG|*.png"
                If d.ShowDialog(FindForm()) = DialogResult.OK Then
                    _markBox.Text = d.FileName
                    _visualOn.Checked = True
                    UpdateVisualEnabled()
                End If
            End Using
        End Sub

        ''' <summary>有没有可用的图案来源：章内自带 SVG，或选中的 .svg/.png。</summary>
        Private Function HasMarkSource() As Boolean
            Dim src = _markBox.Text.Trim()
            If src = "" OrElse src = "（使用章内图案）" Then
                Return _seal IsNot Nothing AndAlso _seal.HasSvg
            End If
            If Not File.Exists(src) Then Return False
            Dim ext = Path.GetExtension(src).ToLowerInvariant()
            Return ext = ".svg" OrElse ext = ".png"
        End Function

        ''' <summary>把图案渲染成 512px 位图给可视化窗口；调用方负责 Dispose。</summary>
        Private Function RenderMarkImage() As Image
            Dim src = _markBox.Text.Trim()
            Try
                Dim svg As String = Nothing
                If src = "" OrElse src = "（使用章内图案）" Then
                    If _seal Is Nothing OrElse Not _seal.HasSvg Then Return Nothing
                    svg = _seal.SvgMarkup
                ElseIf Path.GetExtension(src).ToLowerInvariant() = ".svg" Then
                    svg = File.ReadAllText(src, System.Text.Encoding.UTF8)
                ElseIf Path.GetExtension(src).ToLowerInvariant() = ".png" Then
                    Using fs As New FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                        Using t = Image.FromStream(fs)
                            Return New Bitmap(t)
                        End Using
                    End Using
                Else
                    Return Nothing
                End If

                Dim png = VisualMarkApplier.RenderSvgToPng(svg, 512)
                Using ms As New MemoryStream(png)
                    Using t = Image.FromStream(ms)
                        Return New Bitmap(t)
                    End Using
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>载入可视化窗口用的页面图：图片给真实内容，PDF 给真实比例，其它给白纸。</summary>
        Private Sub LoadVisualPage(filePath As String, ByRef image As Image, ByRef aspect As Double, ByRef caption As String)
            Dim ext = Path.GetExtension(filePath).ToLowerInvariant()

            If ext = ".pdf" Then
                Try
                    Using doc = PdfSharp.Pdf.IO.PdfReader.Open(filePath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import)
                        If doc.PageCount > 0 Then
                            Dim pg = doc.Pages(0)
                            If pg.Width.Point > 1 AndAlso pg.Height.Point > 1 Then aspect = pg.Height.Point / pg.Width.Point
                            caption = $"{Path.GetFileName(filePath)} · 第 1 页（{pg.Width.Point:0}×{pg.Height.Point:0} pt，内容不渲染）"
                            Return
                        End If
                    End Using
                Catch
                End Try
                caption = Path.GetFileName(filePath)
                Return
            End If

            If VisualMarkApplier.CanApply(filePath) Then
                Try
                    Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                        Using src = Image.FromStream(fs)
                            Dim ow = src.Width, oh = src.Height
                            Dim k = Math.Min(1.0, 2400.0 / Math.Max(1, Math.Max(ow, oh)))
                            Dim w = Math.Max(1, CInt(ow * k))
                            Dim h = Math.Max(1, CInt(oh * k))
                            Dim bmp As New Bitmap(w, h)
                            Using g = Graphics.FromImage(bmp)
                                g.InterpolationMode = InterpolationMode.HighQualityBicubic
                                g.PixelOffsetMode = PixelOffsetMode.HighQuality
                                g.DrawImage(src, New Rectangle(0, 0, w, h))
                            End Using
                            image = bmp
                            aspect = h / CDbl(w)
                            caption = $"{Path.GetFileName(filePath)} · {ow}×{oh} px"
                        End Using
                    End Using
                    Return
                Catch
                End Try
            End If

            caption = $"{Path.GetFileName(filePath)} · 该格式没有页面图像"
        End Sub

        ''' <summary>打开全屏可视化盖章：章跟鼠标走，右键确认后回填坐标。</summary>
        Friend Sub OpenVisualSeal()
            If Not HasMarkSource() Then
                MessageBox.Show("当前章不含图案，无法可视化盖章。" & Environment.NewLine & Environment.NewLine &
                                "请点「选择…」指定一个 .svg / .png 图案，或改用自带图案的章。",
                                "SymbolPrint", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If

            Dim f = SelectedFile()
            If f = "" Then
                MessageBox.Show("请先添加要盖章的文件（左侧列表）。", "SymbolPrint",
                                MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If

            Dim pageImg As Image = Nothing
            Dim markImg As Image = Nothing
            Dim aspect As Double = 1.4142
            Dim cap As String = Path.GetFileName(f)

            Cursor = Cursors.WaitCursor
            Try
                LoadVisualPage(f, pageImg, aspect, cap)
                markImg = RenderMarkImage()
                If markImg Is Nothing Then
                    MessageBox.Show("图案渲染失败，无法可视化盖章。", "SymbolPrint",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning)
                    Return
                End If
            Finally
                Cursor = Cursors.Default
            End Try

            Try
                Using dlg As New VisualSealForm(pageImg, aspect, cap, markImg,
                                                CDbl(_widthRatio.Value) / 100.0,
                                                CDbl(_opacity.Value) / 100.0,
                                                CDbl(_rotate.Value),
                                                CDbl(_posX.Value) / 100.0,
                                                CDbl(_posY.Value) / 100.0)
                    If dlg.ShowDialog(FindForm()) = DialogResult.OK Then
                        _posX.Value = CDec(Math.Round(Math.Max(0, Math.Min(1, dlg.XRatio)) * 100))
                        _posY.Value = CDec(Math.Round(Math.Max(0, Math.Min(1, dlg.YRatio)) * 100))
                        _widthRatio.Value = CDec(Math.Round(Math.Max(0.02, Math.Min(1, dlg.WidthRatio)) * 100))
                        If Not _visualOn.Checked Then _visualOn.Checked = True
                        SyncInputsToPreview()
                        UpdateVisualEnabled()
                        AppendLog($"可视化盖章：水平 {dlg.XRatio * 100:0.0}%，垂直 {dlg.YRatio * 100:0.0}%，章宽 {dlg.WidthRatio * 100:0.#}%")
                    End If
                End Using
            Finally
                If markImg IsNot Nothing Then markImg.Dispose()
                If pageImg IsNot Nothing Then pageImg.Dispose()
            End Try
        End Sub

        Private Sub UpdateVisualEnabled()
            Dim isOn = _visualOn.Checked
            For Each c As Control In New Control() {_markBox, _posX, _posY, _widthRatio, _opacity, _rotate, _page}
                c.Enabled = isOn
            Next
            _preview.Enabled = isOn

            ' 没有图案就不给可视化盖（章里没 SVG、也没选图案文件）
            Dim hasMark = HasMarkSource()
            _visualBtn.Enabled = hasMark
            _tip.SetToolTip(_visualBtn, If(hasMark,
                                           "全屏放置章：移动鼠标定位，滚轮缩放，右键确认",
                                           "当前章不含图案，无法可视化盖章：请先选择 .svg/.png 图案，或改用带图案的章"))

            ' 章的图案来源：外部文件，或章内自带 SVG（直接光栅化给预览，保证"看到的就是会贴上去的"）
            Dim src = _markBox.Text.Trim()
            If src = "" OrElse src = "（使用章内图案）" Then
                If _seal IsNot Nothing AndAlso _seal.HasSvg Then
                    Try
                        Dim png = VisualMarkApplier.RenderSvgToPng(_seal.SvgMarkup, 480)
                        Using ms As New MemoryStream(png)
                            Using img = Image.FromStream(ms)
                                _preview.SetMarkImage(img, "（章内图案）")
                            End Using
                        End Using
                    Catch
                        _preview.MarkFile = ""
                    End Try
                Else
                    _preview.MarkFile = ""
                End If
            Else
                _preview.MarkFile = src
            End If

            _preview.OpacityValue = CDbl(_opacity.Value) / 100.0
            _preview.RotationDegrees = CDbl(_rotate.Value)
            _preview.UpdateFromInputs(CDbl(_posX.Value) / 100.0, CDbl(_posY.Value) / 100.0, CDbl(_widthRatio.Value) / 100.0)
        End Sub

        Private Sub SyncPreviewToInputs()
            _posX.Value = CDec(Math.Round(_preview.XRatio * 100))
            _posY.Value = CDec(Math.Round(_preview.YRatio * 100))
            _widthRatio.Value = CDec(Math.Round(_preview.WidthRatio * 100))
        End Sub

        ' ==================== 盖章 ====================

        Private Async Sub StartStamp()
            If _busy Then Return
            If _files.Count = 0 Then
                MessageBox.Show("请先添加要盖章的文件。", "SymbolPrint", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If
            If _seal Is Nothing Then
                MessageBox.Show("请先选择并载入章文件（右侧「印章文件」卡片）。", "SymbolPrint",
                                MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If
            If _seal.IsLocked Then
                MessageBox.Show("章的私钥受口令保护，请输入口令后点「载入」。", "SymbolPrint",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If

            Dim mark As VisualMark = Nothing
            If _visualOn.Checked Then
                mark = BuildVisualMark()
                If mark Is Nothing Then Return
            End If

            Dim cfg = _owner.Settings
            Dim sealOpt As New SealOptions()
            sealOpt.BrotliQuality = cfg.Compression.BrotliQuality
            ' 压缩算法 / 放弃压缩 / 章字节上限：高级设置里能改，这里必须真的传下去，
            ' 否则那几项就是"改了没效果"的假选项
            sealOpt.CompressionAlgorithm = CByte(cfg.Compression.Algorithm)
            sealOpt.SkipIfLarger = cfg.Compression.SkipIfLarger
            sealOpt.MinSizeToCompress = cfg.Compression.MinSizeToCompress
            sealOpt.SealBytesMax = cfg.Compression.SealBytesMax
            sealOpt.Pbkdf2Iterations = cfg.Crypto.Pbkdf2Iterations
            ' 保留旧记录、直接追加：已盖章的文件当普通文件处理，不再自动换新章
            sealOpt.ReplaceExisting = False
            sealOpt.BackupBeforeVisualMark = _backup.Checked
            sealOpt.BackupSuffix = cfg.General.BackupSuffix
            sealOpt.VisualMark = mark
            sealOpt.Carrier.Log = Sub(m) AppendLog("  " & m)

            Dim targets = _files.ToArray()

            ' 覆盖前确认：这些文件里已经盖过章，再盖会在文件里追加一条新记录
            ' （盖章默认保留旧记录）。属于"会改变既有结果"的操作，先问一句。
            If cfg.Ui.ConfirmBeforeOverwrite Then
                Dim already = targets.Where(Function(t) HasExistingRecord(t)).ToArray()
                If already.Length > 0 Then
                    Dim names = already.Select(Function(t) IO.Path.GetFileName(t)).Take(3).ToArray()
                    Dim ask = $"有 {already.Length} 个文件已经带盖章记录：" & Environment.NewLine &
                              String.Join("、", names) & If(already.Length > 3, " 等", "") & Environment.NewLine & Environment.NewLine &
                              "继续盖章会在文件里再追加一条记录（旧记录保留）。是否继续？"
                    If MessageBox.Show(ask, "确认重复盖章", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                                       MessageBoxDefaultButton.Button2) <> DialogResult.Yes Then
                        AppendLog("已取消：用户放弃对已盖章文件的重复盖章。")
                        Return
                    End If
                End If
            End If

            _busy = True
            _startBtn.Enabled = False
            _progress.BeginWork("盖章中…")
            AppendLog($"开始处理 {targets.Length} 个文件（{If(mark IsNot Nothing, "带图案", "仅写数据")}）。")

            Dim sink = _progress.Sink
            Dim stamp = _seal
            Dim okCount = 0, failCount = 0, skipCount = 0
            Dim sw = Diagnostics.Stopwatch.StartNew()

            Try
                For i = 0 To targets.Length - 1
                    If sink.IsCancelled Then Exit For
                    Dim target = targets(i)

                    If CarrierRegistry.Find(target) Is Nothing Then
                        skipCount += 1
                        SetRowState(i, "跳过")
                        Continue For
                    End If

                    Dim scope As New ProgressScope(sink, i / CDbl(targets.Length), (i + 1) / CDbl(targets.Length),
                                                   $"{i + 1}/{targets.Length}  {IO.Path.GetFileName(target)}")
                    Try
                        Dim r = Await Task.Run(Function() SealService.Stamp(target, stamp, sealOpt, scope))
                        SetRowState(i, "完成")
                        okCount += 1
                        AppendLog($"√ {IO.Path.GetFileName(target)}：{r.CarrierName}，{r.BlobLength} 字节" &
                                  If(r.VisualBurned, "，已贴图案", ""))
                    Catch ex As OperationCanceledException
                        SetRowState(i, "取消")
                        Exit For
                    Catch ex As Exception
                        SetRowState(i, "失败")
                        failCount += 1
                        AppendLog($"× {IO.Path.GetFileName(target)}：{ex.Message}")
                    End Try
                Next
            Finally
                sw.Stop()
                _busy = False
                _startBtn.Enabled = True
                Dim cancelled = sink.IsCancelled
                _progress.EndWork(If(cancelled, "已取消", "完成"))
                AppendLog(If(cancelled, "已取消。",
                             $"完成：成功 {okCount}，失败 {failCount}，跳过 {skipCount}，用时 {sw.Elapsed.TotalSeconds:F1}s"))
            End Try
        End Sub

        Private Function BuildVisualMark() As VisualMark
            Dim src = _markBox.Text.Trim()
            Dim m As New VisualMark()
            m.XRatio = CDbl(_posX.Value) / 100.0
            m.YRatio = CDbl(_posY.Value) / 100.0
            m.WidthRatio = Math.Max(0.01, CDbl(_widthRatio.Value) / 100.0)
            m.Opacity = CDbl(_opacity.Value) / 100.0
            m.RotationDegrees = CDbl(_rotate.Value)
            m.PdfPage = CInt(_page.Value)

            If String.IsNullOrWhiteSpace(src) OrElse src = "（使用章内图案）" Then
                If _seal IsNot Nothing AndAlso _seal.HasSvg Then
                    m.SvgMarkup = _seal.SvgMarkup
                    Return m
                End If
                MessageBox.Show("没有可用的图案：请选择 .svg/.png，或使用含图案的章。", "SymbolPrint",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return Nothing
            End If

            If Not File.Exists(src) Then
                MessageBox.Show("图案文件不存在：" & src, "SymbolPrint", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return Nothing
            End If

            Dim ext = Path.GetExtension(src).ToLowerInvariant()
            If ext = ".svg" Then
                m.SvgMarkup = File.ReadAllText(src, System.Text.Encoding.UTF8)
            ElseIf ext = ".png" Then
                m.PngBytes = File.ReadAllBytes(src)
            Else
                MessageBox.Show("目前只支持 .svg 和 .png 作为图案。", "SymbolPrint",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return Nothing
            End If
            Return m
        End Function

        Private Sub SetRowState(rowIndex As Integer, state As String)
            If _grid.IsDisposed OrElse rowIndex < 0 OrElse rowIndex >= _grid.Rows.Count Then Return
            If _grid.InvokeRequired Then
                _grid.BeginInvoke(New Action(Of Integer, String)(AddressOf SetRowState), rowIndex, state)
                Return
            End If
            _grid.Rows(rowIndex).Cells(3).Value = state
        End Sub

        ''' <summary>外部预填：命令行传入文件与章，打开就能一键开始。</summary>
        Friend Sub PrefillFiles(paths As IEnumerable(Of String), sealPath As String, password As String, markPath As String)
            If paths IsNot Nothing Then AddFiles(paths)
            If Not String.IsNullOrEmpty(sealPath) Then
                _sealPath.Text = sealPath
                _sealPass.Text = If(password, "")
                LoadSeal()
            End If
            If Not String.IsNullOrEmpty(markPath) Then
                _markBox.Text = markPath
                _visualOn.Checked = True
                UpdateVisualEnabled()
            End If
        End Sub

        ''' <summary>诊断用：把页面里几块「可能的布局大头」暴露出来，供 --sweep 做逐块归因。</summary>
        Friend Function DiagTargets() As List(Of (String, Control))
            Dim l As New List(Of (String, Control))()
            l.Add(("sealCard", _sealCard))
            l.Add(("visCard", _visCard))
            l.Add(("rightHost", _rightHost))
            l.Add(("sealTable", _sealTable))
            l.Add(("visTable", _visTable))
            l.Add(("body", _body))
            l.Add(("outer", _outer))
            Return l
        End Function

        ''' <summary>递归挂起布局（深度 3 层足够覆盖 卡片 → 表格 → 控件），记录顺序便于逆序恢复。</summary>
        Private Sub SuspendDeep(c As Control, Optional depth As Integer = 0)
            If c Is Nothing OrElse depth > 3 Then Return
            c.SuspendLayout()
            _suspended.Add(c)
            For Each ch As Control In c.Controls
                SuspendDeep(ch, depth + 1)
            Next
        End Sub

        ''' <summary>逆序恢复，最后对根容器做一次真正布局。</summary>
        Private Sub ResumeDeep()
            For i = _suspended.Count - 1 To 0 Step -1
                Try
                    _suspended(i).ResumeLayout(False)
                Catch
                End Try
            Next
            _suspended.Clear()
            If _body IsNot Nothing AndAlso Not _body.IsDisposed Then _body.PerformLayout()
        End Sub

        ''' <summary>竖向分隔（上半工作区 / 下半日志）：日志区高度跟着窗口高度走。</summary>
        Private Sub ApplyOuterSplit()
            Dim o = _outer
            If o Is Nothing OrElse o.IsDisposed Then Return
            If o.Height < 420 Then Return
            Dim want = Math.Max(o.Panel1MinSize, o.Height - Math.Max(o.Panel2MinSize, Theme.LineH * 5 + 96))
            want = Math.Min(want, o.Height - o.Panel2MinSize - o.SplitterWidth)
            If _outerSplitSet AndAlso Math.Abs(o.SplitterDistance - want) < 2 Then Return
            _outerSplitSet = True
            Try
                o.SplitterDistance = want
            Catch
            End Try
        End Sub

        ''' <summary>横向分隔（文件列表 / 右栏）：右栏保持约 794px，两侧都留最小宽度。</summary>
        Private Sub ApplyBodySplit()
            Dim b = _body
            If b Is Nothing OrElse b.IsDisposed Then Return
            Dim total = b.Width - b.SplitterWidth
            If total < 200 Then Return
            Dim minLeft = Math.Min(340, CInt(total * 0.3))
            Dim minRight = Math.Min(560, CInt(total * 0.55))
            Try
                If b.Panel1MinSize <> minLeft Then b.Panel1MinSize = minLeft
                If b.Panel2MinSize <> minRight Then b.Panel2MinSize = minRight
            Catch
                Return
            End Try
            Dim want = Math.Max(minLeft, b.Width - 794)
            want = Math.Min(want, total - minRight)
            If want < minLeft Then want = minLeft
            Try
                If b.SplitterDistance <> want Then b.SplitterDistance = want
            Catch
            End Try
        End Sub

        ''' <summary>拖动结束后的统一重排（拖动期间这些动作被冻结，避免每帧整树重排）。</summary>
        Public Sub Relayout() Implements IAppPage.Relayout
            SyncRightPadding()
            ApplyBodySplit()
            ApplyOuterSplit()
        End Sub

        Public Sub OnShown() Implements IAppPage.OnShown
        End Sub
    End Class

End Namespace
