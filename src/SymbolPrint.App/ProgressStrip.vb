Imports System
Imports System.Drawing
Imports System.Windows.Forms
Imports SymbolPrint.Core.Model
Imports SymbolPrint.Core.Util

Namespace App

    ''' <summary>
    ''' 进度条 + 状态文字 + 取消按钮的组合条。
    ''' 后台线程通过 Sink 上报（节流后跨线程 BeginInvoke 到 UI），用户点取消 → Sink.IsCancelled 变 True，
    ''' Core 里的耗时循环会在下一个检查点抛 OperationCanceledException。
    ''' 快捷操作（&lt;250ms）由 DelayedProgress 吃掉，界面上不会闪一下进度条。
    ''' </summary>
    Friend NotInheritable Class ProgressStrip
        Inherits UserControl

        Private ReadOnly _stage As Label
        Private ReadOnly _detail As Label
        Private ReadOnly _bar As ProgressBar
        Private ReadOnly _cancelBtn As Button
        Private ReadOnly _renderer As ActionProgress
        Private ReadOnly _throttled As ThrottledProgress
        Private ReadOnly _delayed As DelayedProgress
        Private _cancelable As CancelableProgress
        Private _busy As Boolean
        Private _lastFraction As Double = -2
        ' 进度相关的可调项全部来自「高级设置 → 进度」，这里每一项都必须真的被用上，
        ' 否则界面上就是个假开关
        Private ReadOnly _opts As ProgressOptions

        Friend Sub New(Optional delayMs As Integer = 250, Optional throttleMs As Integer = 60,
                       Optional opts As ProgressOptions = Nothing)
            _opts = If(opts, New ProgressOptions())
            ' 高度由字体算出来：DPI 放大时文字变高，这里跟着长，不会把字切掉。
            ' 顶部再留 4px：被父容器压住一两像素时，文字仍然完整。
            Height = Theme.LineH + 38
            BackColor = Color.Transparent
            DoubleBuffered = True

            _stage = New Label() With {
                .AutoSize = False, .Left = 0, .Top = 4, .Height = Theme.LineH + 6, .Width = 460,
                .Font = Theme.FontBold, .ForeColor = Theme.TextMain, .Text = "就绪",
                .TextAlign = ContentAlignment.MiddleLeft
            }
            _detail = New Label() With {
                .AutoSize = False, .Top = 4, .Height = Theme.LineH + 6, .Width = 300,
                .Font = Theme.FontBase, .ForeColor = Theme.TextSub, .Text = "",
                .TextAlign = ContentAlignment.MiddleRight
            }
            ' 用 0–1000 表示 0–100%，比默认 0–100 更平滑；截图里也不会显得一格一格跳
            _bar = New ProgressBar() With {.Left = 0, .Top = Theme.LineH + 14, .Height = 10, .Style = ProgressBarStyle.Blocks,
                                           .Minimum = 0, .Maximum = 1000}
            _cancelBtn = Theme.GhostButton("取消", 76)
            _cancelBtn.Height = Theme.LineH + 8
            _cancelBtn.Top = 3
            _cancelBtn.Visible = False
            ' 关掉「显示进度条」时只留阶段文字，不再画条
            _bar.Visible = _opts.Enabled

            Controls.Add(_stage)
            Controls.Add(_detail)
            Controls.Add(_bar)
            Controls.Add(_cancelBtn)

            _renderer = New ActionProgress(AddressOf OnReport)
            _throttled = New ThrottledProgress(_renderer, Math.Max(10, throttleMs), 0.004)
            _delayed = New DelayedProgress(_throttled, Math.Max(0, delayMs))
            _cancelable = New CancelableProgress(_delayed)

            AddHandler _cancelBtn.Click, Sub(s, e)
                                             _cancelBtn.Enabled = False
                                             _cancelBtn.Text = "正在取消…"
                                             _cancelable.Cancel()
                                         End Sub

            AddHandler Resize, Sub(s, e) LayoutParts()
            LayoutParts()
        End Sub

        ''' <summary>当前操作的进度接收器（同时承载取消令牌）。</summary>
        Friend ReadOnly Property Sink As IProgressSink
            Get
                Return _cancelable
            End Get
        End Property

        ''' <summary>开始一个操作：换新的取消令牌、清空文字、显示取消按钮。</summary>
        Friend Sub BeginWork(Optional initialStage As String = "准备中…")
            Try
                _cancelable.Dispose()
            Catch
            End Try
            _cancelable = New CancelableProgress(_delayed)

            _busy = True
            _lastFraction = -2
            _stage.Text = initialStage
            _detail.Text = ""
            _bar.Style = ProgressBarStyle.Blocks
            _bar.Value = 0
            _bar.Visible = _opts.Enabled
            _detail.Visible = _opts.ShowBytesDetail
            _cancelBtn.Text = "取消"
            _cancelBtn.Enabled = True
            _cancelBtn.Visible = True
            LayoutParts()
        End Sub

        ''' <summary>操作结束（成功/失败/取消都必须调用）。</summary>
        Friend Sub EndWork(Optional finalText As String = Nothing)
            _busy = False
            _cancelBtn.Visible = False
            _bar.Style = ProgressBarStyle.Blocks
            If finalText IsNot Nothing Then _stage.Text = finalText
            _detail.Text = ""
            _bar.Value = 0
        End Sub

        Private Sub LayoutParts()
            Dim w = Math.Max(120, Width)
            _cancelBtn.Left = w - _cancelBtn.Width
            _bar.Width = Math.Max(60, w - _cancelBtn.Width - 10)
            _stage.Width = Math.Max(60, CInt((w - _cancelBtn.Width) * 0.6))
            _detail.Left = _stage.Width
            _detail.Width = Math.Max(60, w - _cancelBtn.Width - 10 - _stage.Width)
        End Sub

        Private Sub OnReport(stage As String, fraction As Double, detail As String)
            If IsDisposed Then Return
            Try
                If InvokeRequired Then
                    ' 外层已节流；UI 线程忙时宁可丢帧，不影响正确性
                    BeginInvoke(New Action(Of String, Double, String)(AddressOf ApplyReport), stage, fraction, detail)
                Else
                    ApplyReport(stage, fraction, detail)
                End If
            Catch ex As ObjectDisposedException
            Catch ex As InvalidOperationException
                ' 窗体正在销毁，忽略
            End Try
        End Sub

        Private Sub ApplyReport(stage As String, fraction As Double, detail As String)
            If IsDisposed OrElse Not _busy Then Return
            If Not String.IsNullOrEmpty(stage) Then _stage.Text = stage
            ' 「显示字节明细」关掉时，右侧那列整块不显示（它承载的就是体积/速率这类数字）
            _detail.Visible = _opts.ShowBytesDetail
            If detail IsNot Nothing Then _detail.Text = If(_opts.ShowBytesDetail, detail, "")

            If fraction < 0 AndAlso _opts.ShowIndeterminate Then
                If _bar.Style <> ProgressBarStyle.Marquee Then
                    _bar.Style = ProgressBarStyle.Marquee
                    _bar.MarqueeAnimationSpeed = 30
                End If
            ElseIf fraction < 0 Then
                ' 不允许不确定进度：保持当前块状进度，不来回跳
                If _bar.Style <> ProgressBarStyle.Blocks Then _bar.Style = ProgressBarStyle.Blocks
            Else
                If _bar.Style <> ProgressBarStyle.Blocks Then _bar.Style = ProgressBarStyle.Blocks
                Dim v = Math.Round(Math.Max(0.0, Math.Min(1.0, fraction)) * 1000)
                If v <> _lastFraction Then
                    _lastFraction = v
                    _bar.Value = CInt(Math.Max(_bar.Minimum, Math.Min(_bar.Maximum, v)))
                End If
            End If
        End Sub
    End Class

End Namespace
