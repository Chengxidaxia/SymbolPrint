Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Drawing
Imports System.Windows.Forms

Namespace App

    ''' <summary>
    ''' 极简动画内核：一个 60fps 定时器跑所有补间，没有动画时自动停。
    ''' 只用「颜色 / 不透明度」这类不会触发布局的属性，所以永远不会引起重排闪烁。
    ''' 刻意不做页面滑动、缩放变形这类花哨效果——界面是工具，动效只用来交代状态变化。
    ''' </summary>
    Friend Module Anim

        Private Const FrameMs As Integer = 16

        Private Class Tween
            Public Span As Stopwatch
            Public Duration As Integer
            Public Tick As Action(Of Double)
            Public Done As Action
            Public Cancelled As Boolean
        End Class

        ''' <summary>诊断用：定时器实际跑过多少帧、当前还有几段补间在排队。</summary>
        Friend TickCount As Long
        Friend LastError As String
        Friend ReadOnly Property Pending As Integer
            Get
                Return _list.Count
            End Get
        End Property

        Private _timer As Timer
        Private ReadOnly _list As New List(Of Tween)()
        Private ReadOnly _byControl As New Dictionary(Of Control, Tween)()

        ''' <summary>0→1 先快后慢，和系统窗口动画的手感一致。</summary>
        Friend Function EaseOut(t As Double) As Double
            Dim inv = 1.0 - Math.Max(0.0, Math.Min(1.0, t))
            Return 1.0 - inv * inv * inv
        End Function

        Friend Function Blend(a As Color, b As Color, t As Double) As Color
            If Double.IsNaN(t) Then t = 0
            t = Math.Max(0.0, Math.Min(1.0, t))
            Return Color.FromArgb(Channel(a.R, b.R, t), Channel(a.G, b.G, t), Channel(a.B, b.B, t))
        End Function

        ''' <summary>
        ''' 单通道插值。绝不能直接 CInt(Double)：VB 默认开着整数溢出检查，
        ''' 一旦 t 为 NaN 或结果越界就抛 OverflowException，被上层吞掉后整段动画静默失效。
        ''' 所以先夹到 0~255 再取整。
        ''' </summary>
        Private Function Channel(fromV As Integer, toV As Integer, t As Double) As Integer
            Dim v = fromV + (toV - fromV) * t
            If Double.IsNaN(v) Then Return fromV
            If v <= 0 Then Return 0
            If v >= 255 Then Return 255
            Return CInt(Math.Round(v))
        End Function

        ''' <summary>跑一段补间；tick 收到的参数是 0→1 的原始进度（需要缓动就自己套 EaseOut）。</summary>
        Friend Sub Run(durationMs As Integer, tick As Action(Of Double), Optional done As Action = Nothing)
            If durationMs <= 0 Then
                tick(1.0)
                done?.Invoke()
                Return
            End If
            EnsureTimer()
            Dim tw As New Tween() With {
                .Span = Stopwatch.StartNew(), .Duration = durationMs, .Tick = tick, .Done = done
            }
            _list.Add(tw)
        End Sub

        ''' <summary>同一个控件上的补间互斥：后一个开始，前一个立即作废（避免两段动画抢同一个属性）。</summary>
        Friend Sub RunFor(owner As Control, durationMs As Integer, tick As Action(Of Double))
            If owner Is Nothing Then
                Run(durationMs, tick)
                Return
            End If
            Dim old As Tween = Nothing
            If _byControl.TryGetValue(owner, old) Then
                old.Cancelled = True
                _byControl.Remove(owner)
            End If
            EnsureTimer()
            Dim tw As New Tween() With {
                .Span = Stopwatch.StartNew(), .Duration = Math.Max(1, durationMs), .Tick = tick,
                .Done = Sub() _byControl.Remove(owner)
            }
            _byControl(owner) = tw
            _list.Add(tw)
        End Sub

        Private Sub EnsureTimer()
            If _timer Is Nothing Then
                _timer = New Timer() With {.Interval = FrameMs}
                AddHandler _timer.Tick, Sub(s, e) Pump()
            End If
            If Not _timer.Enabled Then _timer.Start()
        End Sub

        Private Sub Pump()
            TickCount += 1
            If _list.Count = 0 Then
                _timer.Stop()
                Return
            End If
            For i = _list.Count - 1 To 0 Step -1
                Dim tw = _list(i)
                Dim p = Math.Min(1.0, tw.Span.ElapsedMilliseconds / tw.Duration)
                Try
                    tw.Tick(p)
                Catch ex As Exception
                    ' 控件已被销毁等情况：丢弃这段动画，不能让动画把界面搞崩
                    LastError = ex.GetType().Name & ": " & ex.Message & " @ " &
                                If(ex.StackTrace, "").Split(New String() {vbCrLf, vbLf}, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                    tw.Cancelled = True
                End Try
                If p >= 1.0 OrElse tw.Cancelled Then
                    _list.RemoveAt(i)
                    If Not tw.Cancelled Then
                        Try
                            tw.Done?.Invoke()
                        Catch
                        End Try
                    End If
                End If
            Next
            If _list.Count = 0 Then _timer.Stop()
        End Sub

        ''' <summary>文字淡入：从背景色渐变到目标色。字色变深的过程看起来就是「浮现」。</summary>
        Friend Sub FadeText(lbl As Label, target As Color, Optional durationMs As Integer = 260)
            If lbl Is Nothing Then Return
            Dim from = If(lbl.Parent IsNot Nothing, lbl.Parent.BackColor, Color.White)
            If from.A = 0 Then from = Color.White
            lbl.ForeColor = from
            RunFor(lbl, durationMs, Sub(t)
                                        lbl.ForeColor = Blend(from, target, EaseOut(t))
                                    End Sub)
        End Sub

        ''' <summary>按钮悬停：颜色渐变而不是硬切，鼠标扫过时不会一格一格跳。</summary>
        Friend Sub WireHover(b As Button, normal As Color, hover As Color, Optional durationMs As Integer = 130)
            If b Is Nothing Then Return
            b.BackColor = normal
            AddHandler b.MouseEnter, Sub(s, e) ColorTo(b, hover, durationMs)
            AddHandler b.MouseLeave, Sub(s, e) ColorTo(b, normal, durationMs)
        End Sub

        Friend Sub ColorTo(c As Control, target As Color, Optional durationMs As Integer = 150)
            Dim from = c.BackColor
            RunFor(c, durationMs, Sub(t) c.BackColor = Blend(from, target, EaseOut(t)))
        End Sub

    End Module

End Namespace
