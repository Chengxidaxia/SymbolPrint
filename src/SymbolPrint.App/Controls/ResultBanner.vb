Imports System
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Windows.Forms

Namespace App.Controls

    Friend Enum BannerKind
        None
        Ok
        Warn
        Fail
    End Enum

    ''' <summary>
    ''' 结论横幅：一条彩色圆角带 + 左侧强调条 + 标题/副标题。
    ''' 验章结果、盖章汇总都用它，配色统一由 Theme 决定。
    ''' </summary>
    Friend NotInheritable Class ResultBanner
        Inherits Control

        Private _kind As BannerKind = BannerKind.None
        Private _title As String = "尚未验证"
        Private _subtitle As String = "选择文件后点「开始验证」。"

        Friend Sub New()
            DoubleBuffered = True
            Height = 78
            BackColor = Theme.Bg
            SetStyle(ControlStyles.ResizeRedraw, True)
        End Sub

        Friend Property Kind As BannerKind
            Get
                Return _kind
            End Get
            Set(value As BannerKind)
                _kind = value
                Invalidate()
            End Set
        End Property

        Friend Property Title As String
            Get
                Return _title
            End Get
            Set(value As String)
                _title = If(value, "")
                Invalidate()
            End Set
        End Property

        Friend Property Subtitle As String
            Get
                Return _subtitle
            End Get
            Set(value As String)
                _subtitle = If(value, "")
                Invalidate()
            End Set
        End Property

        Friend Sub ShowResult(kind As BannerKind, title As String, subtitle As String)
            _kind = kind
            _title = title
            _subtitle = subtitle
            Invalidate()
        End Sub

        Private Sub Palette(ByRef bg As Color, ByRef accent As Color, ByRef mark As String)
            Select Case _kind
                Case BannerKind.Ok
                    bg = Theme.OkLight : accent = Theme.Ok : mark = "✓"
                Case BannerKind.Warn
                    bg = Theme.WarnLight : accent = Theme.Warn : mark = "!"
                Case BannerKind.Fail
                    bg = Theme.DangerLight : accent = Theme.Danger : mark = "×"
                Case Else
                    bg = Theme.InfoLight : accent = Theme.TextFaint : mark = "·"
            End Select
        End Sub

        Protected Overrides Sub OnPaint(e As PaintEventArgs)
            Dim g = e.Graphics
            g.SmoothingMode = SmoothingMode.AntiAlias
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.ClearTypeGridFit

            Dim bg As Color, accent As Color, mark As String
            Palette(bg, accent, mark)

            Dim r As New Rectangle(0, 0, Width - 1, Height - 1)
            Using path = Theme.RoundRect(r, 8)
                Using b As New SolidBrush(bg)
                    g.FillPath(b, path)
                End Using
            End Using

            ' 左侧强调条（圆角只在左边，简单起见图一个圆角矩形再叠一个方角）
            Using b As New SolidBrush(accent)
                g.FillRectangle(b, 0, 12, 4, Height - 24)
            End Using

            ' 左侧状态圈
            Dim circle As New Rectangle(20, Height \ 2 - 17, 34, 34)
            Using b As New SolidBrush(accent)
                g.FillEllipse(b, circle)
            End Using
            Using f As New Font("Microsoft YaHei UI", 14.0F, FontStyle.Bold)
                Dim sz = g.MeasureString(mark, f)
                Using b As New SolidBrush(Color.White)
                    g.DrawString(mark, f, b, circle.X + (circle.Width - sz.Width) / 2, circle.Y + (circle.Height - sz.Height) / 2)
                End Using
            End Using

            Using f As New Font("Microsoft YaHei UI", 12.5F, FontStyle.Bold)
                Using b As New SolidBrush(accent)
                    g.DrawString(_title, f, b, 68, 12)
                End Using
            End Using

            Using f As New Font("Microsoft YaHei UI", 9.0F)
                Using b As New SolidBrush(Theme.TextMain)
                    Dim rect As New RectangleF(68, 40, Width - 84, Height - 44)
                    g.DrawString(_subtitle, f, b, rect)
                End Using
            End Using
        End Sub
    End Class

End Namespace
