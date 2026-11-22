Imports System
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Drawing.Imaging
Imports System.IO
Imports System.Windows.Forms
Imports SymbolPrint.Core.Visual

Namespace App.Controls

    ''' <summary>
    ''' 盖章位置预览。不是"示意方框"，而是**用目标文件的真实页面当底图**：
    ''' 图片直接显示图像内容，PDF 用真实页面尺寸比例，然后把章按
    ''' (XRatio, YRatio, WidthRatio, Opacity, RotationDegrees) 叠上去。
    ''' 这里的坐标系、越界贴边、绕中心旋转，全部与 Core 的 VisualMarkApplier 一致，
    ''' 所以预览看到什么，写进文件里就是什么。
    ''' </summary>
    Friend NotInheritable Class MarkPreview
        Inherits Control

        Private _pageImage As Image          ' 真实底图（已缩放的小图，控件自己持有）
        Private _pageAspect As Double = 1.4142
        Private _caption As String = ""
        Private _hasPage As Boolean = False
        Private _pageNote As String = ""

        Private _markFile As String = ""
        Private _markImage As Image
        Private _markError As String = ""
        Private _xRatio As Double = 0.72
        Private _yRatio As Double = 0.78
        Private _widthRatio As Double = 0.22
        Private _opacity As Double = 0.9
        ''' <summary>出现动画：换底图/换章时从 0 淡到 1，让「换了一张」这件事被看见。</summary>
        Private _appear As Double = 1.0

        Private Sub StartAppear()
            _appear = 0.0
            Anim.Run(240, Sub(t2)
                                 _appear = Anim.EaseOut(t2)
                                 Invalidate()
                             End Sub)
        End Sub
        Private _rotation As Double = 0

        Private _dragMode As Integer ' 0=无 1=移动 2=缩放
        Private _dragStart As Point
        Private _startX As Double
        Private _startY As Double
        Private _startW As Double

        Friend Event PositionChanged As EventHandler

        Friend Sub New()
            DoubleBuffered = True
            BackColor = Theme.Bg
            SetStyle(ControlStyles.ResizeRedraw, True)
            SetStyle(ControlStyles.OptimizedDoubleBuffer, True)
        End Sub

        ' ==================== 页面（底图） ====================

        ''' <summary>设置真实页面底图；传 Nothing 则回到空白纸张示意。控件内部保存副本。</summary>
        Friend Sub SetPageImage(image As Image, caption As String)
            StartAppear()
            If _pageImage IsNot Nothing Then
                _pageImage.Dispose()
                _pageImage = Nothing
            End If
            If image IsNot Nothing Then
                _pageImage = New Bitmap(image)
                _pageAspect = _pageImage.Height / CDbl(Math.Max(1, _pageImage.Width))
                _hasPage = True
            Else
                _pageAspect = 1.4142
                _hasPage = False
            End If
            _caption = If(caption, "")
            _pageNote = ""
            Invalidate()
        End Sub

        ''' <summary>无可用位图时，仍然可以把真实页面尺寸比例告诉预览（比如 PDF）。</summary>
        Friend Sub SetPageShape(widthPt As Double, heightPt As Double, caption As String)
            If _pageImage IsNot Nothing Then
                _pageImage.Dispose()
                _pageImage = Nothing
            End If
            _hasPage = False
            If widthPt > 1 AndAlso heightPt > 1 Then
                _pageAspect = heightPt / widthPt
            Else
                _pageAspect = 1.4142
            End If
            _caption = If(caption, "")
            _pageNote = "内容不渲染"
            Invalidate()
        End Sub

        Friend Property MarkFile As String
            Get
                Return _markFile
            End Get
            Set(value As String)
                If String.Equals(_markFile, value, StringComparison.OrdinalIgnoreCase) Then Return
                _markFile = If(value, "")
                LoadMark()
                Invalidate()
            End Set
        End Property

        ''' <summary>直接指定章位图（章内 SVG 已光栅化的结果），控件保存副本。</summary>
        Friend Sub SetMarkImage(image As Image, caption As String)
            StartAppear()
            If _markImage IsNot Nothing Then
                _markImage.Dispose()
                _markImage = Nothing
            End If
            _markError = ""
            _markFile = If(caption, "")
            If image IsNot Nothing Then _markImage = New Bitmap(image)
            Invalidate()
        End Sub

        Friend Property OpacityValue As Double
            Get
                Return _opacity
            End Get
            Set(value As Double)
                _opacity = Math.Max(0.0, Math.Min(1.0, value))
                Invalidate()
            End Set
        End Property

        Friend Property RotationDegrees As Double
            Get
                Return _rotation
            End Get
            Set(value As Double)
                _rotation = value
                Invalidate()
            End Set
        End Property

        Friend ReadOnly Property XRatio As Double
            Get
                Return _xRatio
            End Get
        End Property

        Friend ReadOnly Property YRatio As Double
            Get
                Return _yRatio
            End Get
        End Property

        Friend ReadOnly Property WidthRatio As Double
            Get
                Return _widthRatio
            End Get
        End Property

        Friend Sub UpdateFromInputs(x As Double, y As Double, w As Double)
            _xRatio = Math.Max(0.0, Math.Min(1.0, x))
            _yRatio = Math.Max(0.0, Math.Min(1.0, y))
            _widthRatio = Math.Max(0.02, Math.Min(1.0, w))
            Invalidate()
        End Sub

        Private Sub LoadMark()
            If _markImage IsNot Nothing Then
                _markImage.Dispose()
                _markImage = Nothing
            End If
            _markError = ""

            If String.IsNullOrWhiteSpace(_markFile) OrElse _markFile = "（使用章内图案）" Then Return
            If Not File.Exists(_markFile) Then
                _markError = "图案文件不存在"
                Return
            End If

            Try
                Dim ext = Path.GetExtension(_markFile).ToLowerInvariant()
                If ext = ".png" OrElse ext = ".jpg" OrElse ext = ".bmp" Then
                    Using fs As New FileStream(_markFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                        Using tmp = Image.FromStream(fs)
                            _markImage = New Bitmap(tmp)
                        End Using
                    End Using
                ElseIf ext = ".svg" Then
                    Dim svg = File.ReadAllText(_markFile, System.Text.Encoding.UTF8)
                    Dim png = VisualMarkApplier.RenderSvgToPng(svg, 360)
                    Using ms As New MemoryStream(png)
                        Using tmp = Image.FromStream(ms)
                            _markImage = New Bitmap(tmp)
                        End Using
                    End Using
                Else
                    _markError = "不支持的图案格式"
                End If
            Catch ex As Exception
                _markError = "图案读取失败"
            End Try
        End Sub

        ''' <summary>内缩后的页面矩形（按真实宽高比）。</summary>
        Private Function PageRect() As Rectangle
            Dim availW = Math.Max(40, Width - 28)
            Dim availH = Math.Max(40, Height - 34)
            Dim w = availW
            Dim h = CInt(w * _pageAspect)
            If h > availH Then
                h = availH
                w = CInt(h / _pageAspect)
            End If
            If w < 1 Then w = 1
            If h < 1 Then h = 1
            Return New Rectangle(CInt((Width - w) / 2), CInt((Height - 34 - h) / 2) + 6, w, h)
        End Function

        ''' <summary>与 Core 完全一致的章矩形：宽 = 页宽 × WidthRatio，越界贴边。</summary>
        Private Function MarkRect(page As Rectangle) As RectangleF
            Dim mw = CSng(page.Width * _widthRatio)
            Dim mh = mw
            If _markImage IsNot Nothing AndAlso _markImage.Width > 0 Then
                mh = mw * _markImage.Height / _markImage.Width
            End If
            If mh > page.Height Then
                mh = page.Height * 0.98F
                mw = mh
            End If
            Dim mx = CSng(page.X + page.Width * _xRatio)
            Dim my = CSng(page.Y + page.Height * _yRatio)
            If mx + mw > page.Right Then mx = Math.Max(page.X, page.Right - mw)
            If my + mh > page.Bottom Then my = Math.Max(page.Y, page.Bottom - mh)
            Return New RectangleF(mx, my, mw, mh)
        End Function

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            Using New Prof.T("MarkPreview.OnPaint")
                PaintCore(e)
            End Using
        End Sub

        Private Sub PaintCore(e As PaintEventArgs)
            Dim g = e.Graphics
            g.SmoothingMode = SmoothingMode.AntiAlias
            g.InterpolationMode = InterpolationMode.HighQualityBicubic
            g.PixelOffsetMode = PixelOffsetMode.HighQuality

            Dim page = PageRect()

            ' 页面阴影
            Using shadow As New SolidBrush(Color.FromArgb(18, 0, 0, 0))
                g.FillRectangle(shadow, page.X + 2, page.Y + 3, page.Width, page.Height)
            End Using

            If _pageImage IsNot Nothing Then
                g.DrawImage(_pageImage, page)
            Else
                Using paper As New SolidBrush(Color.White)
                    g.FillRectangle(paper, page)
                End Using
                ' 没有真实内容时，用行线暗示"一页纸"，并写明为何看不到内容
                Using pen As New Pen(Color.FromArgb(236, 239, 244), 2)
                    For i = 0 To 7
                        Dim y = page.Y + CInt(page.Height * (0.14 + i * 0.075))
                        g.DrawLine(pen, page.X + 12, y, page.X + page.Width - 12, y)
                    Next
                End Using
            End If

            Using pen As New Pen(Theme.BorderStrong, 1)
                g.DrawRectangle(pen, page)
            End Using

            If Not Enabled Then
                ' 未启用时不要糊住底图：只把"章会贴在哪"画成虚线圈
                Dim mr0 = MarkRect(page)
                Using pen As New Pen(Color.FromArgb(130, Theme.TextSub), 1.2F)
                    pen.DashStyle = DashStyle.Dash
                    g.DrawRectangle(pen, mr0.X, mr0.Y, mr0.Width, mr0.Height)
                End Using
                Using f As New Font("Microsoft YaHei UI", 8.0F)
                    Dim s = "未启用图案"
                    Dim sz = g.MeasureString(s, f)
                    Dim tx = mr0.X + (mr0.Width - sz.Width) / 2
                    Dim ty = mr0.Y + (mr0.Height - sz.Height) / 2
                    Using bg As New SolidBrush(Color.FromArgb(210, Color.White))
                        g.FillRectangle(bg, tx - 5, ty - 2, sz.Width + 10, sz.Height + 4)
                    End Using
                    Using b As New SolidBrush(Theme.TextSub)
                        g.DrawString(s, f, b, tx, ty)
                    End Using
                End Using
            Else
                Dim mr = MarkRect(page)
                Dim state = g.Save()
                If Math.Abs(_rotation) > 0.01 Then
                    g.TranslateTransform(mr.X + mr.Width / 2.0F, mr.Y + mr.Height / 2.0F)
                    g.RotateTransform(CSng(_rotation))
                    g.TranslateTransform(-(mr.X + mr.Width / 2.0F), -(mr.Y + mr.Height / 2.0F))
                End If

                If _markImage IsNot Nothing Then
                    Dim cm As New Imaging.ColorMatrix()
                    cm.Matrix33 = CSng(Math.Max(0.0, Math.Min(1.0, _opacity * _appear)))
                    Using ia As New Imaging.ImageAttributes()
                        ia.SetColorMatrix(cm, Imaging.ColorMatrixFlag.Default, Imaging.ColorAdjustType.Bitmap)
                        g.DrawImage(_markImage, Rectangle.Round(mr), 0, 0, _markImage.Width, _markImage.Height,
                                    GraphicsUnit.Pixel, ia)
                    End Using
                Else
                    Using b As New SolidBrush(Color.FromArgb(CInt(_opacity * _appear * 70), Theme.Accent))
                        g.FillEllipse(b, mr)
                    End Using
                    Using f As New Font("Microsoft YaHei UI", 8.0F)
                        Using b As New SolidBrush(Theme.Accent)
                            Dim s = If(_markError = "", "章", _markError)
                            Dim sz = g.MeasureString(s, f)
                            If sz.Width < mr.Width Then
                                g.DrawString(s, f, b, mr.X + (mr.Width - sz.Width) / 2, mr.Y + (mr.Height - sz.Height) / 2)
                            End If
                        End Using
                    End Using
                End If
                g.Restore(state)

                ' 选中框（跟随旋转）+ 右下角缩放手柄
                Dim st2 = g.Save()
                If Math.Abs(_rotation) > 0.01 Then
                    g.TranslateTransform(mr.X + mr.Width / 2.0F, mr.Y + mr.Height / 2.0F)
                    g.RotateTransform(CSng(_rotation))
                    g.TranslateTransform(-(mr.X + mr.Width / 2.0F), -(mr.Y + mr.Height / 2.0F))
                End If
                Using pen As New Pen(Color.FromArgb(150, Theme.Accent), 1.2F)
                    pen.DashStyle = DashStyle.Dash
                    g.DrawRectangle(pen, mr.X, mr.Y, mr.Width, mr.Height)
                End Using
                g.Restore(st2)
                Using b As New SolidBrush(Theme.Accent)
                    g.FillRectangle(b, mr.Right - 9, mr.Bottom - 9, 9, 9)
                End Using
            End If

            ' 角标：底图来源（居中一行，过长就截断，不跟别的文字抢位置）
            Using f As New Font("Microsoft YaHei UI", 7.5F)
                Using b As New SolidBrush(Theme.TextFaint)
                    Dim s = If(_caption = "", If(_hasPage, "", "未选择文件（按 A4 比例示意）"), _caption)
                    If _pageNote <> "" Then s = s & " · " & _pageNote
                    If s <> "" Then
                        While g.MeasureString(s, f).Width > Width - 12 AndAlso s.Length > 6
                            s = s.Substring(0, s.Length - 2)
                        End While
                        If s.Length > 4 AndAlso Not s.EndsWith("…", StringComparison.Ordinal) AndAlso s.Length < _caption.Length Then s = s & "…"
                        Dim sz = g.MeasureString(s, f)
                        g.DrawString(s, f, b, Math.Max(6, (Width - sz.Width) / 2), Height - 15)
                    End If
                End Using
            End Using
        End Sub

        Protected Overrides Sub OnMouseDown(e As MouseEventArgs)
            MyBase.OnMouseDown(e)
            If Not Enabled OrElse e.Button <> MouseButtons.Left Then Return
            Dim page = PageRect()
            Dim mr = MarkRect(page)
            Dim handle As New RectangleF(mr.Right - 10, mr.Bottom - 10, 14, 14)

            If handle.Contains(e.Location) Then
                _dragMode = 2
                Cursor = Cursors.SizeNWSE
            ElseIf mr.Contains(e.Location) Then
                _dragMode = 1
                Cursor = Cursors.SizeAll
            Else
                Return
            End If
            _dragStart = e.Location
            _startX = _xRatio
            _startY = _yRatio
            _startW = _widthRatio
        End Sub

        Protected Overrides Sub OnMouseMove(e As MouseEventArgs)
            MyBase.OnMouseMove(e)

            If _dragMode = 0 Then
                If Enabled Then
                    Dim page = PageRect()
                    Dim mr = MarkRect(page)
                    Dim handle As New RectangleF(mr.Right - 10, mr.Bottom - 10, 14, 14)
                    If handle.Contains(e.Location) Then
                        Cursor = Cursors.SizeNWSE
                    ElseIf mr.Contains(e.Location) Then
                        Cursor = Cursors.SizeAll
                    Else
                        Cursor = Cursors.Default
                    End If
                End If
                Return
            End If

            Dim page2 = PageRect()
            If page2.Width <= 0 OrElse page2.Height <= 0 Then Return

            Dim dx = (e.X - _dragStart.X) / CSng(page2.Width)
            Dim dy = (e.Y - _dragStart.Y) / CSng(page2.Height)

            If _dragMode = 1 Then
                _xRatio = Math.Max(0.0, Math.Min(1.0, _startX + dx))
                _yRatio = Math.Max(0.0, Math.Min(1.0, _startY + dy))
            ElseIf _dragMode = 2 Then
                _widthRatio = Math.Max(0.02, Math.Min(1.0, _startW + dx))
            End If

            Invalidate()
            RaiseEvent PositionChanged(Me, EventArgs.Empty)
        End Sub

        Protected Overrides Sub OnMouseUp(e As MouseEventArgs)
            MyBase.OnMouseUp(e)
            _dragMode = 0
            Cursor = Cursors.Default
        End Sub

        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing Then
                If _markImage IsNot Nothing Then
                    _markImage.Dispose()
                    _markImage = Nothing
                End If
                If _pageImage IsNot Nothing Then
                    _pageImage.Dispose()
                    _pageImage = Nothing
                End If
            End If
            MyBase.Dispose(disposing)
        End Sub
    End Class

End Namespace
