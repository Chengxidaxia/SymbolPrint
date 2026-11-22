Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports SymbolPrint.Core.Model
Imports SymbolPrint.Core.Util

Namespace Carriers

    ''' <summary>
    ''' 纯文本载体（.txt / .md 等）：把信封编码成**不可见的零宽字符**追加在文件末尾。
    ''' 为什么不用空格/制表符：空格与 Tab 有宽度、参与排版，Markdown 里行尾两个空格等于硬换行，
    ''' 各种格式化钩子（trimTrailingWhitespace / Prettier）会把它们删掉，章会悄悄消失。
    ''' 零宽字符（U+200B/200C/200D/2060）不占宽度、不参与排版、不会被 trim。
    ''' 编码：每个字符承载 2 bit（四个符号），即 1 字节 = 4 个字符；UTF-8 下每个字符 3 字节，
    ''' 所以膨胀率 12 倍 —— 一个 400 字节的信封约 4.8 KB，对文本文件可以接受。
    ''' 布局：[魔数 16 字符][载荷 4N 字符][总字符数 16 字符]（总字符数在最后，方便从尾部定位）。
    ''' </summary>
    Public NotInheritable Class ZeroWidthTextCarrier
        Implements IStampCarrier
        Implements IOriginalHashAware

        ' 四个零宽符号 —— 都不占宽度，都不会被 trim
        Private Shared ReadOnly Syms As Char() = {ChrW(&H200B), ChrW(&H200C), ChrW(&H200D), ChrW(&H2060)}
        Private Shared ReadOnly Magic As Byte() = {&H53, &H50, &H5A, &H1} ' "SPZ\1"

        Private Const TailChars As Integer = 16
        Private Const TailBytes As Integer = TailChars * 3
        Private Const MagicChars As Integer = 16

        Private Shared ReadOnly Exts As String() = {".txt", ".md", ".markdown", ".text", ".log", ".rst"}

        Public ReadOnly Property Id As String Implements IStampCarrier.Id
            Get
                Return "zerowidth-text"
            End Get
        End Property

        Public ReadOnly Property DisplayName As String Implements IStampCarrier.DisplayName
            Get
                Return "文本零宽字符"
            End Get
        End Property

        Public Function CanHandle(filePath As String) As Boolean Implements IStampCarrier.CanHandle
            Return Array.IndexOf(Exts, CarrierExt.ExtOf(filePath)) >= 0
        End Function

        Public Function Capacity(filePath As String) As Long Implements IStampCarrier.Capacity
            Return Long.MaxValue
        End Function

        ' ---- 编码 ----

        Private Shared Function EncodeBytes(data As Byte()) As String
            Dim sb As New StringBuilder(data.Length * 4)
            For Each b In data
                sb.Append(Syms((b >> 6) And 3))
                sb.Append(Syms((b >> 4) And 3))
                sb.Append(Syms((b >> 2) And 3))
                sb.Append(Syms(b And 3))
            Next
            Return sb.ToString()
        End Function

        Private Shared Function EncodeU32(v As UInteger) As String
            Dim b(3) As Byte
            b(0) = CByte(v And &HFFUI)
            b(1) = CByte((v >> 8) And &HFFUI)
            b(2) = CByte((v >> 16) And &HFFUI)
            b(3) = CByte((v >> 24) And &HFFUI)
            Return EncodeBytes(b)
        End Function

        Private Shared Function SymIndex(c As Char) As Integer
            For i As Integer = 0 To 3
                If Syms(i) = c Then Return i
            Next
            Return -1
        End Function

        ''' <summary>把一串零宽字符解回字节；遇到非本字母表的字符返回 Nothing。</summary>
        Private Shared Function DecodeChars(chars As String) As Byte()
            If chars.Length Mod 4 <> 0 Then Return Nothing
            Dim n = chars.Length \ 4
            Dim out(n - 1) As Byte
            For i As Integer = 0 To n - 1
                Dim v As Integer = 0
                For j As Integer = 0 To 3
                    Dim s = SymIndex(chars(i * 4 + j))
                    If s < 0 Then Return Nothing
                    v = (v << 2) Or s
                Next
                out(i) = CByte(v)
            Next
            Return out
        End Function

        ''' <summary>从文件尾部读出总字符数（块长度）。</summary>
        Private Shared Function ReadBlockChars(fs As FileStream) As Integer
            Dim len = fs.Length
            If len < TailBytes Then Return -1
            fs.Position = len - TailBytes
            Dim buf(TailBytes - 1) As Byte
            Dim got As Integer = 0
            While got < TailBytes
                Dim n = fs.Read(buf, got, TailBytes - got)
                If n <= 0 Then Exit While
                got += n
            End While
            If got <> TailBytes Then Return -1
            Dim s = Encoding.UTF8.GetString(buf)
            If s.Length <> TailChars Then Return -1
            Dim b = DecodeChars(s)
            If b Is Nothing OrElse b.Length <> 4 Then Return -1
            Dim v = CUInt(b(0)) Or (CUInt(b(1)) << 8) Or (CUInt(b(2)) << 16) Or (CUInt(b(3)) << 24)
            If v < MagicChars + TailChars * 2 OrElse v > Integer.MaxValue \ 3 Then Return -1
            Return CInt(v)
        End Function

        ''' <summary>定位我们的块：返回 (起始字节, 字节长度)；没有则 (-1, 0)。</summary>
        Public Shared Function LocateBlock(filePath As String) As (Start As Long, Length As Long)
            Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                Dim chars = ReadBlockChars(fs)
                If chars < 0 Then Return (-1, 0)
                Dim byteLen As Long = chars * 3L
                If byteLen > fs.Length Then Return (-1, 0)
                Dim start As Long = fs.Length - byteLen

                fs.Position = start
                Dim head(MagicChars * 3 - 1) As Byte
                Dim got As Integer = 0
                While got < head.Length
                    Dim n = fs.Read(head, got, head.Length - got)
                    If n <= 0 Then Exit While
                    got += n
                End While
                If got <> head.Length Then Return (-1, 0)
                Dim m = DecodeChars(Encoding.UTF8.GetString(head))
                If m Is Nothing OrElse Not Bin.Same(m, Magic) Then Return (-1, 0)
                Return (start, byteLen)
            End Using
        End Function

        Public Function ReadBlob(filePath As String) As Byte() Implements IStampCarrier.ReadBlob
            Dim loc = LocateBlock(filePath)
            If loc.Start < 0 Then Return Nothing
            Dim block = CarrierExt.ReadRange(filePath, loc.Start, CInt(loc.Length))
            If block Is Nothing Then Return Nothing
            ' 去掉尾部 16 字符的总长度字段，再去掉头部 16 字符魔数
            Dim midLen = block.Length - TailBytes - MagicChars * 3
            If midLen <= 0 Then Return Nothing
            Dim mid(midLen - 1) As Byte
            Buffer.BlockCopy(block, MagicChars * 3, mid, 0, midLen)
            Dim decoded = DecodeChars(Encoding.UTF8.GetString(mid))
            If decoded Is Nothing Then Return Nothing
            Return decoded
        End Function

        Public Sub WriteBlob(filePath As String, blob As Byte(), options As CarrierOptions) Implements IStampCarrier.WriteBlob
            If blob Is Nothing OrElse blob.Length <= SealEnvelope.FooterSize Then Throw New ArgumentException("信封内容为空。", NameOf(blob))

            If Not IsUtf8Text(filePath) Then
                Throw New InvalidDataException(
                    "这个文本文件不是 UTF-8 编码。零宽字符只有在 UTF-8 的文本里才真正不可见，" &
                    "如果在 GBK/ANSI 文件里追加，会显示成乱码。请先把它转成 UTF-8 再盖章。")
            End If

            RemoveBlob(filePath)

            Dim payloadChars = blob.Length * 4
            Dim totalChars = MagicChars + payloadChars + TailChars
            Dim sb As New StringBuilder(totalChars)
            sb.Append(EncodeBytes(Magic))
            sb.Append(EncodeBytes(blob))
            sb.Append(EncodeU32(CUInt(totalChars)))

            Dim bytes = Encoding.UTF8.GetBytes(sb.ToString())
            Using fs As New FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.None)
                fs.Write(bytes, 0, bytes.Length)
                fs.Flush()
            End Using

            options?.Log?.Invoke($"已在文本末尾追加 {bytes.Length} 字节零宽字符（肉眼不可见，不参与排版）。")
        End Sub

        Public Function RemoveBlob(filePath As String) As Boolean Implements IStampCarrier.RemoveBlob
            Dim loc = LocateBlock(filePath)
            If loc.Start < 0 Then Return False
            Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None)
                fs.SetLength(loc.Start)
                fs.Flush()
            End Using
            Return True
        End Function

        Public Function HashOriginal(filePath As String, blobLen As Integer,
                                     Optional sink As IProgressSink = Nothing,
                                     Optional stage As String = "") As Byte() Implements IOriginalHashAware.HashOriginal
            Dim loc = LocateBlock(filePath)
            If loc.Start < 0 Then
                ' 没定位到就退回"整份文件"（空记录的情况下等价）
                Return Hashing.HashFileSkipping(filePath, 0, 0, Nothing, sink, stage)
            End If
            Return Hashing.HashFileSkipping(filePath, loc.Start, loc.Length, Nothing, sink, stage)
        End Function

        ''' <summary>粗判：整份文件能按 UTF-8 严格解码（无 BOM 也允许）。</summary>
        Private Shared Function IsUtf8Text(filePath As String) As Boolean
            Try
                Dim bytes = File.ReadAllBytes(filePath)
                Dim strict As New UTF8Encoding(False, True)
                strict.GetString(bytes)
                Return True
            Catch
                Return False
            End Try
        End Function

    End Class

    ''' <summary>
    ''' RTF 载体：把信封写成 RTF 的"可忽略目标组" {\*\symprint &lt;hex&gt;}，插在最外层 } 之前。
    ''' RTF 规范里 \* 开头的目标组是给"不认识的阅读器直接忽略"的，所以 Word/写字板/WPS 打开时
    ''' 正文一个字都不会变。hex 编码保证全是 ASCII，绝不会破坏 RTF 的括号/转义结构。
    ''' 我们只做"插入"：原有字节一个都不改，摘除时也只删掉这一组。
    ''' </summary>
    Public NotInheritable Class RtfGroupCarrier
        Implements IStampCarrier
        Implements IOriginalHashAware

        Private Shared ReadOnly Marker As Byte() = Encoding.ASCII.GetBytes("{\*\symprint ")
        Private Const MaxRtfSize As Long = 256L * 1024 * 1024

        Public ReadOnly Property Id As String Implements IStampCarrier.Id
            Get
                Return "rtf-group"
            End Get
        End Property

        Public ReadOnly Property DisplayName As String Implements IStampCarrier.DisplayName
            Get
                Return "RTF 可忽略组"
            End Get
        End Property

        Public Function CanHandle(filePath As String) As Boolean Implements IStampCarrier.CanHandle
            Return String.Equals(CarrierExt.ExtOf(filePath), ".rtf", StringComparison.OrdinalIgnoreCase)
        End Function

        Public Function Capacity(filePath As String) As Long Implements IStampCarrier.Capacity
            Return Long.MaxValue
        End Function

        ''' <summary>找到我们那一组的 (起始, 长度)；没有则 (-1, 0)。</summary>
        Public Shared Function LocateGroup(bytes As Byte()) As (Start As Integer, Length As Integer)
            Dim start As Integer = -1
            For i As Integer = bytes.Length - Marker.Length To 0 Step -1
                Dim hit = True
                For j As Integer = 0 To Marker.Length - 1
                    If bytes(i + j) <> Marker(j) Then
                        hit = False
                        Exit For
                    End If
                Next
                If hit Then
                    start = i
                    Exit For
                End If
            Next
            If start < 0 Then Return (-1, 0)

            Dim close As Integer = -1
            For i As Integer = start + Marker.Length To bytes.Length - 1
                If bytes(i) = CByte(AscW("}"c)) Then
                    close = i
                    Exit For
                End If
            Next
            If close < 0 Then Return (-1, 0)
            Return (start, close - start + 1)
        End Function

        Private Shared Function ReadAll(filePath As String) As Byte()
            Dim len = New FileInfo(filePath).Length
            If len > MaxRtfSize Then Throw New InvalidDataException($"RTF 文件过大（{Comp.HumanSize(len)}），超过 {Comp.HumanSize(MaxRtfSize)} 上限。")
            Return File.ReadAllBytes(filePath)
        End Function

        Public Function ReadBlob(filePath As String) As Byte() Implements IStampCarrier.ReadBlob
            Dim all = ReadAll(filePath)
            Dim g = LocateGroup(all)
            If g.Start < 0 Then Return Nothing
            Dim hexLen = g.Length - Marker.Length - 1
            If hexLen <= 0 OrElse hexLen Mod 2 <> 0 Then Return Nothing
            Dim hex = Encoding.ASCII.GetString(all, g.Start + Marker.Length, hexLen)
            Try
                Return Bin.FromHex(hex)
            Catch
                Return Nothing
            End Try
        End Function

        Public Sub WriteBlob(filePath As String, blob As Byte(), options As CarrierOptions) Implements IStampCarrier.WriteBlob
            If blob Is Nothing OrElse blob.Length <= SealEnvelope.FooterSize Then Throw New ArgumentException("信封内容为空。", NameOf(blob))

            Dim all = ReadAll(filePath)
            Dim g = LocateGroup(all)
            If g.Start >= 0 Then all = RemoveRange(all, g.Start, g.Length)

            ' 插在最外层 } 之前
            Dim closeIdx As Integer = -1
            For i As Integer = all.Length - 1 To 0 Step -1
                If all(i) = CByte(AscW("}"c)) Then
                    closeIdx = i
                    Exit For
                End If
            Next
            If closeIdx < 0 Then Throw New InvalidDataException("不是有效的 RTF：找不到最外层组的结束 }。")

            Dim group As Byte() = Encoding.ASCII.GetBytes("{\*\symprint " & Bin.Hex(blob) & "}")
            Dim result(all.Length + group.Length - 1) As Byte
            Buffer.BlockCopy(all, 0, result, 0, closeIdx)
            Buffer.BlockCopy(group, 0, result, closeIdx, group.Length)
            Buffer.BlockCopy(all, closeIdx, result, closeIdx + group.Length, all.Length - closeIdx)

            File.WriteAllBytes(filePath, result)
            options?.Log?.Invoke($"已插入 RTF 可忽略组 {group.Length} 字节（正文未改动，阅读器会忽略它）。")
        End Sub

        Public Function RemoveBlob(filePath As String) As Boolean Implements IStampCarrier.RemoveBlob
            Dim all = ReadAll(filePath)
            Dim g = LocateGroup(all)
            If g.Start < 0 Then Return False
            File.WriteAllBytes(filePath, RemoveRange(all, g.Start, g.Length))
            Return True
        End Function

        Private Shared Function RemoveRange(src As Byte(), start As Integer, count As Integer) As Byte()
            Dim out(src.Length - count - 1) As Byte
            If start > 0 Then Buffer.BlockCopy(src, 0, out, 0, start)
            Dim tailLen = src.Length - start - count
            If tailLen > 0 Then Buffer.BlockCopy(src, start + count, out, start, tailLen)
            Return out
        End Function

        Public Function HashOriginal(filePath As String, blobLen As Integer,
                                     Optional sink As IProgressSink = Nothing,
                                     Optional stage As String = "") As Byte() Implements IOriginalHashAware.HashOriginal
            Dim g As (Start As Integer, Length As Integer) = (-1, 0)
            Try
                g = LocateGroup(ReadAll(filePath))
            Catch
            End Try
            If g.Start < 0 Then Return Hashing.HashFileSkipping(filePath, 0, 0, Nothing, sink, stage)
            Return Hashing.HashFileSkipping(filePath, g.Start, g.Length, Nothing, sink, stage)
        End Function

    End Class

End Namespace
