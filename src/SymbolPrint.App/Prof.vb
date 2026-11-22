Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Linq
Imports System.Text

Namespace App

    ''' <summary>
    ''' 极简计时器：只在 --sweep 这类诊断模式下启用，正常运行时 Enabled=False，开销为一次布尔判断。
    ''' 用途：回答「拖动缩放时到底哪个处理器在吃时间」，而不是靠猜。
    ''' </summary>
    Friend Module Prof

        Friend Enabled As Boolean

        Private ReadOnly _us As New Dictionary(Of String, Long)()
        Private ReadOnly _n As New Dictionary(Of String, Integer)()

        Friend Sub Add(name As String, us As Long)
            If Not Enabled Then Return
            Dim cur As Long = 0
            _us.TryGetValue(name, cur)
            _us(name) = cur + us
            Dim c As Integer = 0
            _n.TryGetValue(name, c)
            _n(name) = c + 1
        End Sub

        Friend Sub Reset()
            _us.Clear()
            _n.Clear()
        End Sub

        Friend Function Dump() As String
            Dim sb As New StringBuilder()
            For Each kv In _us.OrderByDescending(Function(x) x.Value)
                Dim cnt = _n(kv.Key)
                Dim avg = If(cnt = 0, 0, kv.Value \ cnt)
                sb.AppendLine($"    {kv.Key,-30} 合计 {kv.Value / 1000.0,8:F1} ms  次数 {cnt,5}  平均 {avg / 1000.0,6:F2} ms")
            Next
            Return sb.ToString()
        End Function

        ''' <summary>用法：Using Prof.T("名字") ... End Using</summary>
        Friend NotInheritable Class T
            Implements IDisposable

            Private ReadOnly _name As String
            Private ReadOnly _sw As Stopwatch

            Friend Sub New(name As String)
                _name = name
                If Enabled Then
                    _sw = Stopwatch.StartNew()
                End If
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
                If _sw IsNot Nothing Then
                    _sw.Stop()
                    Prof.Add(_name, _sw.ElapsedTicks * 1000000L \ Stopwatch.Frequency)
                End If
            End Sub
        End Class

    End Module

End Namespace
