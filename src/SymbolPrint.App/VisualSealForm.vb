Imports System
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Drawing.Imaging
Imports System.Windows.Forms

Namespace App

    ''' <summary>
    ''' 可视化盖章窗口：把目标文件真实页面铺满屏幕，章图案跟着鼠标走，
    ''' 滚轮改大小，右键弹出确认面板；确认后把位置/大小回填到盖章页。
    ''' 这里只负责"决定贴哪儿"，不写文件。
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
            SetStyle(ControlStyles.ResizeRedraw Or ControlStyles.OptimizedDoubleBuffer, True)
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

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            Dim g = e.Graphics
            g.SmoothingMode = SmoothingMode.AntiAlias
            g.InterpolationMode = InterpolationMode.HighQualityBicubic
            g.PixelOffsetMode = PixelOffsetMode.HighQuality

            Dim page = PageRect()

            ' 顶部提示
            Using f As New Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold)
                Using b As New SolidBrush(Color.FromArgb(232, 236, 244))
                    g.DrawString("可视化盖章", f, b, 28, 18)
                End Using
            End Using
            Using f As New Font("Microsoft YaHei UI", 9.0F)
                Using b As New SolidBrush(Color.FromArgb(150, 160, 178))
                    g.DrawString("移动鼠标放置章 · 滚轮缩放 · 右键确认 · Esc 取消", f, b, 28, 46)
                    If _caption <> "" Then
                        Dim s = _caption
                        Dim sz = g.MeasureString(s, f)
                        g.DrawString(s, f, b, Math.Max(28, ClientSize.Width - sz.Width - 28), 20)
                    End If
                End Using
            End Using

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
                Using pen As New Pen(Color.FromArgb(235, 238, 243), 2)
                    For i = 0 To 9
                        Dim y = page.Y + CInt(page.Height * (0.12 + i * 0.075))
                        g.DrawLine(pen, page.X + 16, y, page.X + page.Width - 16, y)
                    Next
                End Using
            End If

            Using pen As New Pen(Color.FromArgb(110, 120, 138), 1)
                g.DrawRectangle(pen, page)
            End Using

            Dim mr = CurrentMarkRect(page)
            Dim state = g.Save()
            If Math.Abs(_rotation) > 0.01 Then
                g.TranslateTransform(mr.X + mr.Width / 2.0F, mr.Y + mr.Height / 2.0F)
                g.RotateTransform(CSng(_rotation))
                g.TranslateTransform(-(mr.X + mr.Width / 2.0F), -(mr.Y + mr.Height / 2.0F))
            End If
            If _markImage IsNot Nothing Then
                Dim cm As New Imaging.ColorMatrix()
                cm.Matrix33 = CSng(_opacity)
                Using ia As New Imaging.ImageAttributes()
                    ia.SetColorMatrix(cm, Imaging.ColorMatrixFlag.Default, Imaging.ColorAdjustType.Bitmap)
                    g.DrawImage(_markImage, Rectangle.Round(mr), 0, 0, _markImage.Width, _markImage.Height,
                                GraphicsUnit.Pixel, ia)
                End Using
            End If
            g.Restore(state)

            Using pen As New Pen(Color.FromArgb(190, 110, 180, 255), 1.2F)
                pen.DashStyle = DashStyle.Dash
                g.DrawRectangle(pen, mr.X, mr.Y, mr.Width, mr.Height)
            End Using

            ' 左下角实时数值
            Dim xr = (mr.X - page.X) / page.Width
            Dim yr = (mr.Y - page.Y) / page.Height
            Using f As New Font("Consolas", 10.5F)
                Using b As New SolidBrush(Color.FromArgb(170, 190, 210))
                    Dim s = $"位置 {xr * 100:0.0}% , {yr * 100:0.0}%      章宽 {_widthRatio * 100:0.#}%      不透明 {_opacity * 100:0}%      旋转 {_rotation:0}°"
                    g.DrawString(s, f, b, 28, ClientSize.Height - 40)
                End Using
            End Using
        End Sub

        Protected Overrides Sub OnMouseMove(e As MouseEventArgs)
            MyBase.OnMouseMove(e)
            If _panel IsNot Nothing Then Return
            Dim page = PageRect()
            If page.Width <= 0 Then Return
            _cx = Math.Max(0.0, Math.Min(1.0, (e.X - page.X) / CDbl(page.Width)))
            _cy = Math.Max(0.0, Math.Min(1.0, (e.Y - page.Y) / CDbl(page.Height)))
            Invalidate()
        End Sub

        Protected Overrides Sub OnMouseWheel(e As MouseEventArgs)
            MyBase.OnMouseWheel(e)
            If _panel IsNot Nothing Then Return
            _widthRatio = Math.Max(0.04, Math.Min(1.0, _widthRatio + If(e.Delta > 0, 0.02, -0.02)))
            Invalidate()
        End Sub

        Protected Overrides Sub OnMouseDown(e As MouseEventArgs)
            MyBase.OnMouseDown(e)
            If e.Button = MouseButtons.Right AndAlso _panel Is Nothing Then ShowConfirmPanel()
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
    End Class

End Namespace
