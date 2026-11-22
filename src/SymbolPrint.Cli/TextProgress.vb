Imports System
Imports SymbolPrint.Core.Util

Namespace SymbolPrint.Cli

    ''' <summary>
    ''' 控制台进度条渲染器。
    ''' 两条规则：①操作很快（&lt;250ms）时一个字都不打，避免刷屏闪烁；
    ''' ②输出被重定向（管道/文件）时不画动态条，只在阶段切换时打一行，方便看日志。
    ''' </summary>
    Public NotInheritable Class TextProgress
        Implements IProgressSink

        Private ReadOnly _interactive As Boolean
        Private ReadOnly _innerWidth As Integer
        Private _lastRendered As Integer = -1
        Private _lastStage As String = ""
        Private _spin As Integer
        Private _dirty As Boolean
        Private _prevLen As Integer

        ''' <summary>取消状态来源（控制台由 Ctrl+C 触发）；Nothing 表示永不可取消。</summary>
        Private ReadOnly _cancelRequested As Func(Of Boolean)

        Public Sub New(Optional cancelCheck As Func(Of Boolean) = Nothing)
            _cancelRequested = If(cancelCheck, Function() False)
            _interactive = Not Console.IsOutputRedirected OrElse Environment.GetEnvironmentVariable("SP_FORCE_TTY") = "1"
            Dim w As Integer = 100
            Try
                If _interactive Then w = Math.Max(40, Console.WindowWidth)
            Catch
            End Try
            ' 预留：阶段名(约28) + 空格 + 进度条 + 百分比 + 明细
            _innerWidth = Math.Max(12, Math.Min(36, w - 60))
        End Sub

        ''' <summary>控制台的取消由 Ctrl+C（CancelKeyPress）负责，这里不自带取消源。</summary>
        Public ReadOnly Property IsCancelled As Boolean Implements IProgressSink.IsCancelled
            Get
                Return _cancelRequested()
            End Get
        End Property

        Public Sub Report(stage As String, fraction As Double, detail As String) Implements IProgressSink.Report
            Dim name As String = If(String.IsNullOrEmpty(stage), _lastStage, stage)
            If String.IsNullOrEmpty(name) Then name = "处理中"

            If Not _interactive Then
                ' 日志模式：只在阶段变化时记一行
                If Not String.Equals(name, _lastStage, StringComparison.Ordinal) Then
                    Console.WriteLine($"… {name}" & If(String.IsNullOrEmpty(detail), "", $"  {detail}"))
                    _lastStage = name
                End If
                If fraction >= 1.0 Then _lastStage = ""
                Return
            End If

            Dim pct As Integer
            Dim body As String
            If fraction < 0.0 Then
                Dim frames As String = "|/-\"
                body = "[" & frames(_spin Mod frames.Length) & "]  " & If(detail, "")
                _spin += 1
            Else
                pct = CInt(Math.Round(Math.Min(1.0, fraction) * 100.0))
                Dim filled As Integer = CInt(Math.Round(Math.Min(1.0, fraction) * _innerWidth))
                If filled > _innerWidth Then filled = _innerWidth
                Dim empty As Integer = _innerWidth - filled
                Dim bar As String
                If filled = 0 Then
                    bar = New String(" "c, _innerWidth)
                ElseIf empty = 0 Then
                    bar = New String("="c, _innerWidth)
                Else
                    bar = New String("="c, filled - 1) & ">" & New String(" "c, empty)
                End If
                body = "[" & bar & $"] {pct,3}%  " & If(detail, "")
            End If

            Dim line As String = Fit(name, 30).PadRight(30) & " " & body
            ' 只在内容变了才重绘（Windows 控制台每帧都要重排，重绘太频繁会拖慢自身）
            Dim sig As Integer = line.GetHashCode()
            If sig = _lastRendered Then Return
            _lastRendered = sig

            Dim maxW As Integer = line.Length
            Try
                maxW = Math.Max(20, Console.WindowWidth - 1)
            Catch
            End Try
            If line.Length > maxW Then line = line.Substring(0, maxW)

            Console.Write(ChrW(13) & line)
            ' 用空格擦掉上一次残留的尾巴
            Dim pad As Integer = _prevLen - line.Length
            If pad > 0 Then Console.Write(New String(" "c, pad))
            _prevLen = line.Length
            _dirty = True

            If fraction >= 1.0 Then
                Console.WriteLine()
                _lastRendered = -1
                _prevLen = 0
                _dirty = False
                _lastStage = ""
            End If
        End Sub

        Private Shared Function Fit(s As String, max As Integer) As String
            If s Is Nothing Then Return ""
            If s.Length <= max Then Return s
            If max <= 1 Then Return s.Substring(0, max)
            Return s.Substring(0, max - 1) & "…"
        End Function

        ''' <summary>收尾：如果还有未换行的动态条，补一个换行。</summary>
        Public Sub FinishLine()
            If _dirty Then
                Console.WriteLine()
                _dirty = False
                _prevLen = 0
            End If
            _lastRendered = -1
        End Sub
    End Class

End Namespace
