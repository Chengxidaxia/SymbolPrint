Imports System
Imports System.IO
Imports System.IO.Compression

Namespace Util

    ''' <summary>
    ''' 压缩封装。质量可调（Brotli 0–11），按块处理以便上报进度、响应取消。
    ''' 说明：VB 不支持 Span，拿不到 Brotli 的低层 window/mode 参数，所以只暴露 Quality —— 不放"调了也不生效"的假参数。
    ''' </summary>
    Public Module Comp

        Public Const MethodNone As Byte = 0
        Public Const MethodBrotli As Byte = 1

        Public Const MinBrotliQuality As Integer = 0
        Public Const MaxBrotliQuality As Integer = 11
        Public Const DefaultBrotliQuality As Integer = 9

        Private Const ChunkSize As Integer = 1 << 20

        Public Function ClampQuality(q As Integer) As Integer
            If q < MinBrotliQuality Then Return MinBrotliQuality
            If q > MaxBrotliQuality Then Return MaxBrotliQuality
            Return q
        End Function

        Public Function BrotliCompress(data As Byte(), Optional quality As Integer = DefaultBrotliQuality,
                                       Optional sink As IProgressSink = Nothing,
                                       Optional stage As String = "") As Byte()
            If data Is Nothing OrElse data.Length = 0 Then Return Array.Empty(Of Byte)()
            Dim opts As New BrotliCompressionOptions()
            opts.Quality = ClampQuality(quality)
            Dim name As String = If(String.IsNullOrEmpty(stage), "Brotli 压缩", stage)
            Using ms As New MemoryStream()
                Using bs As New BrotliStream(ms, opts, True)
                    Dim off As Integer = 0
                    While off < data.Length
                        sink.ThrowIfCancelled()
                        Dim n As Integer = Math.Min(ChunkSize, data.Length - off)
                        bs.Write(data, off, n)
                        off += n
                        Push(sink, name, off / CDbl(data.Length), ProgressExt.BytesDetail(off, data.Length))
                    End While
                End Using
                Return ms.ToArray()
            End Using
        End Function

        Public Function BrotliDecompress(data As Byte(), Optional sink As IProgressSink = Nothing,
                                         Optional stage As String = "") As Byte()
            If data Is Nothing OrElse data.Length = 0 Then Return Array.Empty(Of Byte)()
            Dim name As String = If(String.IsNullOrEmpty(stage), "Brotli 解压", stage)
            Using input As New MemoryStream(data)
                Using bs As New BrotliStream(input, CompressionMode.Decompress)
                    Using outMs As New MemoryStream()
                        Dim buf(ChunkSize - 1) As Byte
                        Dim doneBytes As Long = 0
                        Dim n As Integer
                        Do
                            sink.ThrowIfCancelled()
                            n = bs.Read(buf, 0, buf.Length)
                            If n <= 0 Then Exit Do
                            outMs.Write(buf, 0, n)
                            doneBytes += n
                            ' 解压后的总长事先未知，只能报"不确定进度 + 已处理量"
                            Push(sink, name, -1.0, ProgressExt.BytesDetail(doneBytes, 0))
                        Loop
                        Return outMs.ToArray()
                    End Using
                End Using
            End Using
        End Function

        ''' <summary>按标志压缩：返回 (方法字节, 数据)。压缩后反而更大时原样保存。</summary>
        Public Function Pack(data As Byte(), Optional quality As Integer = DefaultBrotliQuality,
                             Optional sink As IProgressSink = Nothing,
                             Optional stage As String = "",
                             Optional algorithm As Byte = MethodBrotli,
                             Optional skipIfLarger As Boolean = True) As (Method As Byte, Data As Byte())
            ' 算法可由设置直接选「不压缩」：省 CPU，代价是记录变大
            If algorithm = MethodNone Then Return (MethodNone, data)

            Dim c = BrotliCompress(data, quality, sink, stage)
            If c.Length = 0 Then Return (MethodNone, data)
            ' 压缩后反而更大：默认退回原文；关掉这项就按压缩结果存
            If skipIfLarger AndAlso c.Length >= data.Length Then Return (MethodNone, data)
            Return (MethodBrotli, c)
        End Function

        Public Function Unpack(method As Byte, data As Byte(), Optional sink As IProgressSink = Nothing,
                               Optional stage As String = "") As Byte()
            Select Case method
                Case MethodNone
                    Return data
                Case MethodBrotli
                    Return BrotliDecompress(data, sink, stage)
                Case Else
                    Throw New InvalidDataException($"未知的压缩方法编号：{method}")
            End Select
        End Function

        Public Function HumanSize(n As Long) As String
            If n < 1024 Then Return $"{n} B"
            If n < 1024 * 1024 Then Return $"{n / 1024.0:0.0} KB"
            Return $"{n / 1024.0 / 1024.0:0.00} MB"
        End Function

    End Module

End Namespace
