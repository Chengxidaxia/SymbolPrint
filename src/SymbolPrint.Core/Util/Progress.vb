Imports System
Imports System.Diagnostics
Imports System.Runtime.CompilerServices
Imports System.Threading

Namespace Util

    ''' <summary>
    ''' 进度上报 + 取消接口。Core 层不引用任何 UI 类型，只往这里推"在干什么 / 干了多少 / 该不该停"，
    ''' 由调用方决定显示成控制台文本条还是 WinForms 进度条 + 取消按钮。
    ''' 约定：fraction ∈ [0,1]；fraction &lt; 0 表示"不确定进度"（只知道还在算）。
    ''' stage 为空字符串表示沿用上一个阶段名；detail 是给人看的补充（如“12.3 MB / 45.6 MB”），可为 Nothing。
    ''' </summary>
    Public Interface IProgressSink
        Sub Report(stage As String, fraction As Double, detail As String)
        ''' <summary>用户点了取消后为 True；耗时循环里用 sink.ThrowIfCancelled() 主动退出。</summary>
        ReadOnly Property IsCancelled As Boolean
    End Interface

    ''' <summary>空实现（默认值，不产生任何开销，永不可取消）。</summary>
    Public NotInheritable Class NullProgress
        Implements IProgressSink

        Public Shared ReadOnly Instance As New NullProgress()

        Private Sub New()
        End Sub

        Public Sub Report(stage As String, fraction As Double, detail As String) Implements IProgressSink.Report
        End Sub

        Public ReadOnly Property IsCancelled As Boolean Implements IProgressSink.IsCancelled
            Get
                Return False
            End Get
        End Property
    End Class

    ''' <summary>把委托当作进度接收器（UI 常用：直接传 Lambda 更新控件）。</summary>
    Public NotInheritable Class ActionProgress
        Implements IProgressSink

        Private ReadOnly _action As Action(Of String, Double, String)
        Private ReadOnly _cancelled As Func(Of Boolean)

        Public Sub New(action As Action(Of String, Double, String), Optional cancelled As Func(Of Boolean) = Nothing)
            If action Is Nothing Then Throw New ArgumentNullException(NameOf(action))
            _action = action
            _cancelled = cancelled
        End Sub

        Public Sub Report(stage As String, fraction As Double, detail As String) Implements IProgressSink.Report
            _action(stage, fraction, detail)
        End Sub

        Public ReadOnly Property IsCancelled As Boolean Implements IProgressSink.IsCancelled
            Get
                Return _cancelled IsNot Nothing AndAlso _cancelled()
            End Get
        End Property
    End Class

    ''' <summary>
    ''' 可取消的进度包装：包住真实渲染器，自带 CancellationTokenSource。
    ''' UI 上"取消"按钮 → Cancel()；后台线程每个循环检查 IsCancelled / ThrowIfCancelled()。
    ''' </summary>
    Public NotInheritable Class CancelableProgress
        Implements IProgressSink
        Implements IDisposable

        Private ReadOnly _inner As IProgressSink
        Private ReadOnly _cts As New CancellationTokenSource()

        Public Sub New(inner As IProgressSink)
            _inner = If(inner, NullProgress.Instance)
        End Sub

        Public ReadOnly Property Token As CancellationToken
            Get
                Return _cts.Token
            End Get
        End Property

        Public ReadOnly Property IsCancelled As Boolean Implements IProgressSink.IsCancelled
            Get
                Return _cts.IsCancellationRequested
            End Get
        End Property

        Public Sub Cancel()
            If Not _cts.IsCancellationRequested Then
                Try
                    _cts.Cancel()
                Catch ex As ObjectDisposedException
                End Try
            End If
        End Sub

        Public Sub Report(stage As String, fraction As Double, detail As String) Implements IProgressSink.Report
            _inner.Report(stage, fraction, detail)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            _cts.Dispose()
        End Sub
    End Class

    ''' <summary>
    ''' 子任务进度段：把一个子操作的 [0,1] 映射到父操作的 [from,to] 区间，
    ''' 多个阶段串起来时总进度连续，而不是每阶段都从 0 跳到 100。
    ''' 取消状态从父级透传。
    ''' </summary>
    Public NotInheritable Class ProgressScope
        Implements IProgressSink
        Implements IDisposable

        Private ReadOnly _parent As IProgressSink
        Private ReadOnly _fromValue As Double
        Private ReadOnly _toValue As Double
        Private ReadOnly _name As String

        Public Sub New(parent As IProgressSink, fromValue As Double, toValue As Double, name As String)
            _parent = If(parent, NullProgress.Instance)
            _fromValue = Math.Max(0.0, Math.Min(1.0, fromValue))
            _toValue = Math.Max(_fromValue, Math.Min(1.0, toValue))
            _name = If(name, "")
        End Sub

        ''' <summary>在当前段内部再切一段（按本段比例）。</summary>
        Public Function Slice(fromRatio As Double, toRatio As Double, name As String) As ProgressScope
            Dim span = _toValue - _fromValue
            Return New ProgressScope(_parent, _fromValue + span * fromRatio, _fromValue + span * toRatio,
                                     JoinName(_name, name))
        End Function

        Private Shared Function JoinName(a As String, b As String) As String
            If String.IsNullOrEmpty(a) Then Return If(b, "")
            If String.IsNullOrEmpty(b) Then Return a
            Return a & " · " & b
        End Function

        Public ReadOnly Property IsCancelled As Boolean Implements IProgressSink.IsCancelled
            Get
                Return _parent.IsCancelled
            End Get
        End Property

        Public Sub Report(stage As String, fraction As Double, detail As String) Implements IProgressSink.Report
            Dim mapped As Double
            If fraction < 0.0 Then
                mapped = -1.0
            Else
                Dim f = If(fraction > 1.0, 1.0, fraction)
                mapped = _fromValue + (_toValue - _fromValue) * f
            End If
            _parent.Report(JoinName(_name, stage), mapped, detail)
        End Sub

        ''' <summary>结束本段（只用于 UI 收尾，不额外上报，避免阶段边界闪一下 100%）。</summary>
        Public Sub Dispose() Implements IDisposable.Dispose
        End Sub
    End Class

    ''' <summary>
    ''' 节流：进度回调本身有成本（尤其跨线程封送到 UI），按"最小时间间隔 + 最小变化量"过滤；
    ''' 完成信号（fraction &gt;= 1）和阶段切换永远透传。
    ''' </summary>
    Public NotInheritable Class ThrottledProgress
        Implements IProgressSink

        Private ReadOnly _inner As IProgressSink
        Private ReadOnly _minIntervalMs As Long
        Private ReadOnly _minDelta As Double
        Private ReadOnly _clock As Stopwatch = Stopwatch.StartNew()
        Private _lastMs As Long = Long.MinValue \ 4
        Private _lastFraction As Double = -2.0
        Private _lastStage As String = ""

        Public Sub New(inner As IProgressSink, Optional minIntervalMs As Integer = 60, Optional minDelta As Double = 0.005)
            _inner = If(inner, NullProgress.Instance)
            _minIntervalMs = Math.Max(0, minIntervalMs)
            _minDelta = Math.Max(0.0, minDelta)
        End Sub

        Public ReadOnly Property IsCancelled As Boolean Implements IProgressSink.IsCancelled
            Get
                Return _inner.IsCancelled
            End Get
        End Property

        Public Sub Report(stage As String, fraction As Double, detail As String) Implements IProgressSink.Report
            Dim now = _clock.ElapsedMilliseconds
            Dim stageChanged = Not String.Equals(stage, _lastStage, StringComparison.Ordinal)
            Dim finished = fraction >= 1.0
            If Not finished AndAlso Not stageChanged Then
                If now - _lastMs < _minIntervalMs Then Return
                If fraction >= 0 AndAlso _lastFraction >= 0 AndAlso Math.Abs(fraction - _lastFraction) < _minDelta Then Return
            End If
            _lastMs = now
            _lastFraction = fraction
            _lastStage = stage
            _inner.Report(stage, fraction, detail)
        End Sub
    End Class

    ''' <summary>
    ''' 启动延迟：操作很快（&lt; delayMs）时**完全不显示**进度条，避免一闪而过。
    ''' 第一次上报若还没到时间点，就只记下来，等后续上报到一起放出去。
    ''' </summary>
    Public NotInheritable Class DelayedProgress
        Implements IProgressSink

        Private ReadOnly _inner As IProgressSink
        Private ReadOnly _delayMs As Long
        Private ReadOnly _clock As Stopwatch = Stopwatch.StartNew()
        Private _opened As Boolean
        Private _hasPending As Boolean
        Private _pStage As String = ""
        Private _pFraction As Double
        Private _pDetail As String

        Public Sub New(inner As IProgressSink, Optional delayMs As Integer = 220)
            _inner = If(inner, NullProgress.Instance)
            _delayMs = Math.Max(0, delayMs)
        End Sub

        Public ReadOnly Property IsOpen As Boolean
            Get
                Return _opened
            End Get
        End Property

        Public ReadOnly Property IsCancelled As Boolean Implements IProgressSink.IsCancelled
            Get
                Return _inner.IsCancelled
            End Get
        End Property

        Public Sub Report(stage As String, fraction As Double, detail As String) Implements IProgressSink.Report
            If Not _opened Then
                If _clock.ElapsedMilliseconds < _delayMs Then
                    _hasPending = True
                    _pStage = stage
                    _pFraction = fraction
                    _pDetail = detail
                    Return
                End If
                _opened = True
            End If
            _inner.Report(stage, fraction, detail)
        End Sub
    End Class

    ''' <summary>把进度转成 .NET 标准 IProgress(Of Double)（方便跨线程交给 UI）。</summary>
    Public NotInheritable Class DoubleProgressAdapter
        Implements IProgressSink

        Private ReadOnly _inner As IProgress(Of Double)

        Public Sub New(inner As IProgress(Of Double))
            If inner Is Nothing Then Throw New ArgumentNullException(NameOf(inner))
            _inner = inner
        End Sub

        Public ReadOnly Property IsCancelled As Boolean Implements IProgressSink.IsCancelled
            Get
                Return False
            End Get
        End Property

        Public Sub Report(stage As String, fraction As Double, detail As String) Implements IProgressSink.Report
            _inner.Report(fraction)
        End Sub
    End Class

    ''' <summary>进度上报的语法糖。</summary>
    Public Module ProgressExt

        ''' <summary>带上补充说明上报进度；sink 为 Nothing 时安全忽略（不必到处写 If）。</summary>
        Public Sub Push(sink As IProgressSink, stage As String, fraction As Double, Optional detail As String = Nothing)
            If sink Is Nothing Then Return
            sink.Report(stage, fraction, detail)
        End Sub

        <Extension>
        Public Sub Report(sink As IProgressSink, stage As String, fraction As Double)
            If sink Is Nothing Then Return
            sink.Report(stage, fraction, Nothing)
        End Sub

        ''' <summary>不确定进度（耗时未知的纯计算，例如生成 ECC 密钥）。</summary>
        <Extension>
        Public Sub Indeterminate(sink As IProgressSink, stage As String)
            If sink Is Nothing Then Return
            sink.Report(stage, -1.0, Nothing)
        End Sub

        <Extension>
        Public Sub Completed(sink As IProgressSink, stage As String)
            If sink Is Nothing Then Return
            sink.Report(stage, 1.0, Nothing)
        End Sub

        ''' <summary>耗时循环里调用：用户取消就抛 OperationCanceledException 立刻退出。</summary>
        <Extension>
        Public Sub ThrowIfCancelled(sink As IProgressSink)
            If sink IsNot Nothing AndAlso sink.IsCancelled Then Throw New OperationCanceledException("操作已被用户取消。")
        End Sub

        ''' <summary>统一的字节进度描述：“12.3 MB / 45.6 MB”。</summary>
        Public Function BytesDetail(done As Long, total As Long) As String
            If total <= 0 Then Return Comp.HumanSize(done)
            Return Comp.HumanSize(done) & " / " & Comp.HumanSize(total)
        End Function

        Public Function Clamp01(v As Double) As Double
            If v < 0.0 Then Return 0.0
            If v > 1.0 Then Return 1.0
            Return v
        End Function
    End Module

End Namespace
