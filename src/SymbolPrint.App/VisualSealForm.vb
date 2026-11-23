Imports System
Imports System.Diagnostics
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Drawing.Imaging
Imports System.Windows.Forms

Namespace App

    ''' <summary>
    ''' 可视化盖章窗口：把目标文件真实页面铺满屏幕，章图案跟着鼠标走，
    ''' 滚轮改大小，右键弹出确认面板；确认后把位置/大小回填到盖章页。
    ''' 这里只负责"决定贴哪儿"，不写文件。
    '''
    ''' 性能要点（鼠标移动是否跟手全靠这里）：
    '''   1) 深色底 / 标题 / 阴影 / 页面图 / 边框这些"不动的东西"烘焙到 _bg 位图，
    '''      只在窗口尺寸变化时重建一次；每帧只做一次 1:1 位图拷贝。
    '''      原先每帧都把 2400px 的页面图用 HighQualityBicubic 重采样一遍，单帧 30ms+。
    '''   2) 章图案按当前显示尺寸 + 不透明度预渲染成 _markCache，滚轮改尺寸时才重建；
    '''      每帧不再走 ColorMatrix/ImageAttributes。
    '''   3) 字体、画刷、画笔全部字段化，不在 OnPaint 里 New。
    ''' </summary>
    Friend NotInheritable Class VisualSealForm
        Inherits Form

        Private ReadOnly _pageImage As Image
        Private ReadOnly _pageAspect As Double
        Private ReadOnly _markImage As Image
        Private ReadOnly _caption As String

        Private _widthRatio As Double
        Private _opacity As Double
        Private _rotation As Double

        ''' <summary>章中心相对页面的比例（0–1）。</summary>
        Private _cx As Double = 0.83
        Private _cy As Double = 0.89

        Private _panel As Panel

        ' ---- 背景缓存：窗口尺寸变化时重建 ----
        Private _bg As Bitmap
        Private _bgDirty As Boolean = True
        Private _bgW As Integer
        Private _bgH As Integer

        ' ---- 章图案缓存：显示尺寸/不透明度/旋转变化时重建 ----
        Private _markCache As Bitmap
        Private _mcW As Integer
        Private _mcH As Integer
        Private _mcOpacity As Double = -1.0
        Private _mcRotation As Double = Double.NaN

        ' ---- GDI 资源：生命周期跟窗体走，绝不在 OnPaint 里 New ----
        Private ReadOnly _titleFont As New Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold)
        Private ReadOnly _hintFont As New Font("Microsoft YaHei UI", 9.0F)
        Private ReadOnly _valueFont As New Font("Consolas", 10.5F)
        Private ReadOnly _titleBrush As New SolidBrush(Color.FromArgb(232, 236, 244))
        Private ReadOnly _hintBrush As New SolidBrush(Color.FromArgb(150, 160, 178))
        Private ReadOnly _valueBrush As New SolidBrush(Color.FromArgb(170, 190, 210))
        Private ReadOnly _pageBorderPen As New Pen(Color.FromArgb(110, 120, 138), 1)
        Private ReadOnly _markBorderPen As New Pen(Color.FromArgb(190, 110, 180, 255), 1.2F) With {.DashStyle = DashStyle.Dash}
        Private ReadOnly _paperPen As New Pen(Color.FromArgb(235, 238, 243), 2)

        ' 确认后的结果（章左上角占页宽/页高的比例）
        Private _xOut As Double
        Private _yOut As Double
        Private _wOut As Double
        Private _oOut As Double
        Private _rOut As Double

        Friend ReadOnly Property XRatio As Double
            Get
                Return _xOut
            End Get
        End Property

        Friend ReadOnly Property YRatio As Double
            Get
                Return _yOut
            End Get
        End Property

        Friend ReadOnly Property WidthRatio As Double
            Get
                Return _wOut
            End Get
        End Property

        Friend ReadOnly Property OpacityValue As Double
            Get
                Return _oOut
            End Get
        End Property

        Friend ReadOnly Property RotationDegrees As Double
            Get
                Return _rOut
            End Get
        End Property

        ''' <param name="pageImage">文件页面位图；Nothing 表示画白纸（PDF 按真实页面比例）</param>
        ''' <param name="pageAspect">页高 / 页宽</param>
        ''' <param name="caption">顶部显示的文件说明</param>
        ''' <param name="markImage">章图案位图</param>
        ''' <param name="startX">初始章左上角占页宽比例</param>
        ''' <param name="startY">初始章左上角占页高比例</param>
        Friend Sub New(pageImage As Image, pageAspect As Double, caption As String,
                       markImage As Image, widthRatio As Double, opacity As Double, rotation As Double,
                       Optional startX As Double = 0.72, Optional startY As Double = 0.78)
            _pageImage = pageImage
            _pageAspect = If(pageAspect > 0.05, pageAspect, 1.4142)
            _caption = If(caption, "")
            _markImage = markImage
            _widthRatio = Math.Max(0.04, Math.Min(1.0, widthRatio))
            _opacity = Math.Max(0.0, Math.Min(1.0, opacity))
            _rotation = rotation
            _cx = Math.Max(0.0, Math.Min(1.0, startX + _widthRatio / 2))
            _cy = Math.Max(0.0, Math.Min(1.0, startY + _widthRatio / 2))

            FormBorderStyle = FormBorderStyle.None
            StartPosition = FormStartPosition.Manual
            WindowState = FormWindowState.Maximized
            BackColor = Color.FromArgb(38, 42, 51)
            DoubleBuffered = True
            KeyPreview = True
            Cursor = Cursors.Cross
            SetStyle(ControlStyles.ResizeRedraw Or ControlStyles.OptimizedDoubleBuffer Or
                     ControlStyles.AllPaintingInWmPaint Or ControlStyles.UserPaint, True)
            Text = "可视化盖章"
        End Sub

        Private Function PageRect() As Rectangle
            Dim availW = Math.Max(100, ClientSize.Width - 80)
            Dim availH = Math.Max(100, ClientSize.Height - 150)
            Dim w = availW
            Dim h = CInt(w * _pageAspect)
            If h > availH Then
                h = availH
                w = CInt(h / _pageAspect)
            End If
            Return New Rectangle((ClientSize.Width - w) \ 2, 70 + (availH - h) \ 2, w, h)
        End Function

        Private Function MarkSize(page As Rectangle) As SizeF
            Dim w = CSng(page.Width * _widthRatio)
            Dim h = w
            If _markImage IsNot Nothing AndAlso _markImage.Width > 0 Then
                h = w * _markImage.Height / _markImage.Width
            End If
            Return New SizeF(w, h)
        End Function

        ''' <summary>当前章矩形（已夹在页面内），供绘制和确认时取坐标。</summary>
        Private Function CurrentMarkRect(page As Rectangle) As RectangleF
            Dim ms = MarkSize(page)
            Dim mx = page.X + page.Width * _cx - ms.Width / 2.0
            Dim my = page.Y + page.Height * _cy - ms.Height / 2.0
            If mx < page.X Then mx = page.X
            If my < page.Y Then my = page.Y
            If mx + ms.Width > page.Right Then mx = page.Right - ms.Width
            If my + ms.Height > page.Bottom Then my = page.Bottom - ms.Height
            Return New RectangleF(CSng(mx), CSng(my), ms.Width, ms.Height)
        End Function

        ' ==================== 背景烘焙 ====================

        ''' <summary>把不随鼠标变化的内容一次性画进位图，之后每帧只做整块拷贝。</summary>
        Private Sub BuildBackground()
            Dim w = ClientSize.Width, h = ClientSize.Height
            If w <= 0 OrElse h <= 0 Then Return

            If _bg IsNot Nothing AndAlso (_bgW <> w OrElse _bgH <> h) Then
                _bg.Dispose()
                _bg = Nothing
            End If
            If _bg Is Nothing Then
                _bg = New Bitmap(w, h, PixelFormat.Format32bppPArgb)
                _bgW = w
                _bgH = h
            End If

            Using g = Graphics.FromImage(_bg)
                g.SmoothingMode = SmoothingMode.AntiAlias
                g.InterpolationMode = InterpolationMode.HighQualityBicubic
                g.PixelOffsetMode = PixelOffsetMode.HighQuality
                g.Clear(BackColor)

                Dim page = PageRect()

                ' 顶部提示
                g.DrawString("可视化盖章", _titleFont, _titleBrush, 28, 18)
                g.DrawString("移动鼠标放置章 · 滚轮缩放 · 右键确认 · Esc 取消", _hintFont, _hintBrush, 28, 46)
                If _caption <> "" Then
                    Dim sz = g.MeasureString(_caption, _hintFont)
                    g.DrawString(_caption, _hintFont, _hintBrush,
                                 Math.Max(28, ClientSize.Width - sz.Width - 28), 20)
                End If

                ' 页面阴影
                For i = 1 To 6
                    Using sb As New SolidBrush(Color.FromArgb(Math.Max(6, 30 \ i), 0, 0, 0))
                        g.FillRectangle(sb, page.X + i, page.Y + i + 4, page.Width, page.Height)
                    End Using
                Next

                If _pageImage IsNot Nothing Then
                    g.DrawImage(_pageImage, page)
                Else
                    Using paper As New SolidBrush(Color.White)
                        g.FillRectangle(paper, page)
                    End Using
                    For i = 0 To 9
                        Dim y = page.Y + CInt(page.Height * (0.12 + i * 0.075))
                        g.DrawLine(_paperPen, page.X + 16, y, page.X + page.Width - 16, y)
                    Next
                End If

                g.DrawRectangle(_pageBorderPen, page)
            End Using

            _bgDirty = False
        End Sub

        ' ==================== 章图案缓存 ====================

        ''' <summary>按当前显示尺寸把章缩好并把不透明度烘进位图，滚轮/透明度变化才重建。</summary>
        Private Sub EnsureMarkCache(w As Integer, h As Integer)
            If _markImage Is Nothing Then Return
            If w < 1 Then w = 1
            If h < 1 Then h = 1
            If _markCache IsNot Nothing AndAlso _mcW = w AndAlso _mcH = h AndAlso
               Math.Abs(_mcOpacity - _opacity) < 0.0005 AndAlso Math.Abs(_mcRotation - _rotation) < 0.0005 Then
                Return
            End If

            _markCache?.Dispose()
            _markCache = New Bitmap(w, h, PixelFormat.Format32bppPArgb)
            Using g = Graphics.FromImage(_markCache)
                g.SmoothingMode = SmoothingMode.AntiAlias
                g.InterpolationMode = InterpolationMode.HighQualityBicubic
                g.PixelOffsetMode = PixelOffsetMode.HighQuality
                g.CompositingQuality = CompositingQuality.HighQuality

                Dim state = g.Save()
                ' 旋转围绕位图中心；OnPaint 里再按目标矩形贴回去，等价于围绕章中心旋转
                If Math.Abs(_rotation) > 0.01 Then
                    g.TranslateTransform(w / 2.0F, h / 2.0F)
                    g.RotateTransform(CSng(_rotation))
                    g.TranslateTransform(-w / 2.0F, -h / 2.0F)
                End If

                Dim cm As New ColorMatrix()
                cm.Matrix33 = CSng(_opacity)
                Using ia As New ImageAttributes()
                    ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap)
                    g.DrawImage(_markImage, New Rectangle(0, 0, w, h), 0, 0,
                                _markImage.Width, _markImage.Height, GraphicsUnit.Pixel, ia)
                End Using
                g.Restore(state)
            End Using

            _mcW = w
            _mcH = h
            _mcOpacity = _opacity
            _mcRotation = _rotation
        End Sub

        ' ==================== 绘制 ====================

        ''' <summary>背景位图整块覆盖，省掉默认的全屏擦除。</summary>
        Protected Overrides Sub OnPaintBackground(e As PaintEventArgs)
        End Sub

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            Dim g = e.Graphics
            g.PageUnit = GraphicsUnit.Pixel

            If _bg Is Nothing OrElse _bgDirty OrElse _bgW <> ClientSize.Width OrElse _bgH <> ClientSize.Height Then
                BuildBackground()
            End If

            ' 1:1 位图拷贝：这是每帧唯一的大块操作
            If _bg IsNot Nothing Then
                g.InterpolationMode = InterpolationMode.NearestNeighbor
                g.PixelOffsetMode = PixelOffsetMode.Half
                g.DrawImageUnscaled(_bg, 0, 0)
            End If

            g.SmoothingMode = SmoothingMode.AntiAlias

            Dim page = PageRect()
            Dim mr = CurrentMarkRect(page)

            ' 章：缓存位图直接贴，尺寸已对齐目标矩形
            Dim mw = Math.Max(1, CInt(Math.Round(mr.Width)))
            Dim mh = Math.Max(1, CInt(Math.Round(mr.Height)))
            EnsureMarkCache(mw, mh)
            If _markCache IsNot Nothing Then
                g.InterpolationMode = InterpolationMode.Bilinear
                g.PixelOffsetMode = PixelOffsetMode.Half
                g.DrawImage(_markCache, New Rectangle(CInt(Math.Round(mr.X)), CInt(Math.Round(mr.Y)), mw, mh))
            End If

            ' 定位虚线框
            g.DrawRectangle(_markBorderPen, mr.X, mr.Y, mr.Width, mr.Height)

            ' 左下角实时数值
            Dim xr = (mr.X - page.X) / page.Width
            Dim yr = (mr.Y - page.Y) / page.Height
            Dim s = $"位置 {xr * 100:0.0}% , {yr * 100:0.0}%      章宽 {_widthRatio * 100:0.#}%      不透明 {_opacity * 100:0}%      旋转 {_rotation:0}°"
            g.DrawString(s, _valueFont, _valueBrush, 28, ClientSize.Height - 40)
        End Sub

        Protected Overrides Sub OnMouseMove(e As MouseEventArgs)
            MyBase.OnMouseMove(e)
            If _panel IsNot Nothing Then Return
            Dim page = PageRect()
            If page.Width <= 0 Then Return
            Dim nx = Math.Max(0.0, Math.Min(1.0, (e.X - page.X) / CDbl(page.Width)))
            Dim ny = Math.Max(0.0, Math.Min(1.0, (e.Y - page.Y) / CDbl(page.Height)))
            ' 位置没实质变化就不重绘（高回报率鼠标会疯狂发 MouseMove）
            If Math.Abs(nx - _cx) < 0.0002 AndAlso Math.Abs(ny - _cy) < 0.0002 Then Return
            _cx = nx
            _cy = ny
            ' 只脏章所在区域：新旧矩形并集，避免整屏重画
            InvalidateMarkArea(page)
        End Sub

        ''' <summary>把上一帧和这一帧的章矩形都标脏，其余区域不动。</summary>
        Private Sub InvalidateMarkArea(page As Rectangle)
            Dim cur = CurrentMarkRect(page)
            Dim pad = 6
            Dim r = Rectangle.FromLTRB(CInt(Math.Floor(cur.Left)) - pad, CInt(Math.Floor(cur.Top)) - pad,
                                       CInt(Math.Ceiling(cur.Right)) + pad, CInt(Math.Ceiling(cur.Bottom)) + pad)
            If _lastMarkRect.IsEmpty Then
                _lastMarkRect = r
                Invalidate()
                Return
            End If
            Dim union = Rectangle.Union(_lastMarkRect, r)
            _lastMarkRect = r
            ' 左下角数值也会变，一并带上
            union = Rectangle.Union(union, New Rectangle(20, ClientSize.Height - 48, ClientSize.Width - 40, 40))
            Invalidate(union)
        End Sub

        Private _lastMarkRect As Rectangle

        Protected Overrides Sub OnMouseWheel(e As MouseEventArgs)
            MyBase.OnMouseWheel(e)
            If _panel IsNot Nothing Then Return
            Dim before = _widthRatio
            _widthRatio = Math.Max(0.04, Math.Min(1.0, _widthRatio + If(e.Delta > 0, 0.02, -0.02)))
            If Math.Abs(before - _widthRatio) < 0.0001 Then Return
            _lastMarkRect = Rectangle.Empty
            Invalidate()
        End Sub

        Protected Overrides Sub OnMouseDown(e As MouseEventArgs)
            MyBase.OnMouseDown(e)
            If e.Button = MouseButtons.Right AndAlso _panel Is Nothing Then ShowConfirmPanel()
        End Sub

        Protected Overrides Sub OnResize(e As EventArgs)
            MyBase.OnResize(e)
            _bgDirty = True
            _lastMarkRect = Rectangle.Empty
        End Sub

        ' ==================== 确认面板 ====================

        Private Sub ShowConfirmPanel()
            Dim page = PageRect()
            Dim mr = CurrentMarkRect(page)
            Dim xr = (mr.X - page.X) / page.Width
            Dim yr = (mr.Y - page.Y) / page.Height
            _xOut = xr
            _yOut = yr
            _wOut = _widthRatio
            _oOut = _opacity
            _rOut = _rotation

            _panel = New Panel() With {
                .Size = New Size(430, 252), .BackColor = Color.White,
                .Left = (ClientSize.Width - 430) \ 2,
                .Top = (ClientSize.Height - 252) \ 2
            }
            AddHandler _panel.Paint, Sub(s, ev)
                                         ev.Graphics.SmoothingMode = SmoothingMode.AntiAlias
                                         Using p As New Pen(Theme.Border, 1)
                                             ev.Graphics.DrawRectangle(p, 0, 0, _panel.Width - 1, _panel.Height - 1)
                                         End Using
                                     End Sub

            Dim title As New Label() With {
                .Text = "确认盖章", .Font = New Font("Microsoft YaHei UI", 12.0F, FontStyle.Bold),
                .ForeColor = Theme.TextMain, .AutoSize = True, .Left = 26, .Top = 20
            }

            Dim detail As New Label() With {
                .AutoSize = False, .Left = 26, .Top = 58, .Width = 378, .Height = 108,
                .Font = New Font("Microsoft YaHei UI", 9.5F), .ForeColor = Theme.TextSub,
                .Text = $"文件：{_caption}" & Environment.NewLine &
                        $"位置：水平 {xr * 100:0.0}%    垂直 {yr * 100:0.0}%" & Environment.NewLine &
                        $"章宽：{_widthRatio * 100:0.#}% 页面宽度    不透明度 {_opacity * 100:0}%    旋转 {_rotation:0}°"
            }

            Dim ok As Button = Theme.PrimaryButton("确认盖章", 130)
            ok.SetBounds(262, 186, 130, 36)
            Dim cancel As Button = Theme.GhostButton("取消", 96)
            cancel.SetBounds(154, 186, 96, 36)

            AddHandler ok.Click, Sub(s, ev)
                                     DialogResult = DialogResult.OK
                                     Close()
                                 End Sub
            AddHandler cancel.Click, Sub(s, ev) ClosePanel()

            _panel.Controls.Add(title)
            _panel.Controls.Add(detail)
            _panel.Controls.Add(ok)
            _panel.Controls.Add(cancel)
            Controls.Add(_panel)
            _panel.BringToFront()
            ok.Focus()
        End Sub

        Private Sub ClosePanel()
            If _panel Is Nothing Then Return
            Controls.Remove(_panel)
            _panel.Dispose()
            _panel = Nothing
            Focus()
            Invalidate()
        End Sub

        Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
            MyBase.OnKeyDown(e)
            If e.KeyCode = Keys.Escape Then
                If _panel IsNot Nothing Then
                    ClosePanel()
                Else
                    DialogResult = DialogResult.Cancel
                    Close()
                End If
            ElseIf e.KeyCode = Keys.Enter AndAlso _panel IsNot Nothing Then
                DialogResult = DialogResult.OK
                Close()
            End If
        End Sub

        Protected Overrides Sub OnFormClosed(e As FormClosedEventArgs)
            _bg?.Dispose()
            _markCache?.Dispose()
            _titleFont.Dispose()
            _hintFont.Dispose()
            _valueFont.Dispose()
            _titleBrush.Dispose()
            _hintBrush.Dispose()
            _valueBrush.Dispose()
            _pageBorderPen.Dispose()
            _markBorderPen.Dispose()
            _paperPen.Dispose()
            MyBase.OnFormClosed(e)
        End Sub

        ''' <summary>
        ''' 复刻优化前的单帧成本：整页 HighQualityBicubic 重采样 + 6 层阴影 + 每帧 New 字体/画刷
        ''' + 章走 ColorMatrix。只用于 --visualbench 出对照数据。
        ''' </summary>
        Friend Function BenchLegacyFrames(n As Integer) As Double
            If n < 1 Then n = 1
            ' 按真实屏幕分辨率测：最大化后客户区就是这个尺寸，位图拷贝成本随之变化
            If ClientSize.Width < 400 Then
                Dim sc = Screen.PrimaryScreen.Bounds
                ClientSize = New Size(sc.Width, sc.Height)
            End If

            Using bmp As New Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb)
                Using g = Graphics.FromImage(bmp)
                    g.Clear(BackColor)
                    Dim sw As Stopwatch = Nothing
                    For i = 1 To n
                        If i = 2 Then sw = Stopwatch.StartNew()   ' 第一帧只做预热
                        Dim page = PageRect()
                        g.SmoothingMode = SmoothingMode.AntiAlias
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality

                        Using f As New Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold)
                            Using b As New SolidBrush(Color.FromArgb(232, 236, 244))
                                g.DrawString("可视化盖章", f, b, 28, 18)
                            End Using
                        End Using
                        Using f As New Font("Microsoft YaHei UI", 9.0F)
                            Using b As New SolidBrush(Color.FromArgb(150, 160, 178))
                                g.DrawString("移动鼠标放置章 · 滚轮缩放 · 右键确认 · Esc 取消", f, b, 28, 46)
                            End Using
                        End Using

                        For k = 1 To 6
                            Using sb As New SolidBrush(Color.FromArgb(Math.Max(6, 30 \ k), 0, 0, 0))
                                g.FillRectangle(sb, page.X + k, page.Y + k + 4, page.Width, page.Height)
                            End Using
                        Next
                        If _pageImage IsNot Nothing Then g.DrawImage(_pageImage, page)

                        Dim mr = CurrentMarkRect(page)
                        If _markImage IsNot Nothing Then
                            Dim cm As New ColorMatrix()
                            cm.Matrix33 = CSng(_opacity)
                            Using ia As New ImageAttributes()
                                ia.SetColorMatrix(cm, ColorMatrixFlag.Default, ColorAdjustType.Bitmap)
                                g.DrawImage(_markImage, Rectangle.Round(mr), 0, 0, _markImage.Width, _markImage.Height,
                                            GraphicsUnit.Pixel, ia)
                            End Using
                        End If

                        Using f As New Font("Consolas", 10.5F)
                            Using b As New SolidBrush(Color.FromArgb(170, 190, 210))
                                g.DrawString("位置 0.0% , 0.0%      章宽 22%", f, b, 28, ClientSize.Height - 40)
                            End Using
                        End Using
                    Next
                    If sw Is Nothing Then Return 0
                    sw.Stop()
                    Return sw.Elapsed.TotalMilliseconds / Math.Max(1, n - 1)
                End Using
            End Using
        End Function

        ' ==================== 性能自检 ====================

        ''' <summary>
        ''' 连续重绘 n 帧并返回平均毫秒数（--visualbench 用）。
        ''' 直接调 OnPaint 画到内存位图：不弹窗、不抢焦点，测的正是每帧真实的绘制成本。
        ''' </summary>
        Friend Function BenchFrames(n As Integer) As Double
            If n < 1 Then n = 1
            ' 按真实屏幕分辨率测：最大化后客户区就是这个尺寸，位图拷贝成本随之变化
            If ClientSize.Width < 400 Then
                Dim sc = Screen.PrimaryScreen.Bounds
                ClientSize = New Size(sc.Width, sc.Height)
            End If

            If _bg Is Nothing Then BuildBackground()
            Dim page = PageRect()
            Dim mr0 = CurrentMarkRect(page)
            EnsureMarkCache(Math.Max(1, CInt(mr0.Width)), Math.Max(1, CInt(mr0.Height)))

            Using bmp As New Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppPArgb)
                Using g = Graphics.FromImage(bmp)
                    Dim pe As New PaintEventArgs(g, New Rectangle(0, 0, bmp.Width, bmp.Height))
                    OnPaint(pe)      ' 预热，不计入

                    Dim sw = Stopwatch.StartNew()
                    For i = 1 To n
                        ' 模拟鼠标移动：位置每帧微调，逼出真实重绘路径
                        _cx = 0.3 + 0.4 * (i Mod 10) / 10.0
                        _cy = 0.3 + 0.4 * (i Mod 7) / 7.0
                        _lastMarkRect = Rectangle.Empty
                        OnPaint(pe)
                    Next
                    sw.Stop()
                    Return sw.Elapsed.TotalMilliseconds / n
                End Using
            End Using
        End Function
    End Class

End Namespace
