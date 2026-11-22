Imports System
Imports System.IO
Imports System.Text

Namespace Util

    ''' <summary>小端字节读取器。所有多字节整数一律使用小端序。</summary>
    Public NotInheritable Class ByteReader

        Private ReadOnly _buf As Byte()
        Private _pos As Integer
        Private ReadOnly _end As Integer

        Public Sub New(buf As Byte())
            Me.New(buf, 0, If(buf Is Nothing, 0, buf.Length))
        End Sub

        Public Sub New(buf As Byte(), offset As Integer, count As Integer)
            If buf Is Nothing Then Throw New ArgumentNullException(NameOf(buf))
            If offset < 0 OrElse count < 0 OrElse offset + count > buf.Length Then
                Throw New ArgumentOutOfRangeException(NameOf(offset))
            End If
            _buf = buf
            _pos = offset
            _end = offset + count
        End Sub

        Public ReadOnly Property Position As Integer
            Get
                Return _pos
            End Get
        End Property

        Public ReadOnly Property Remaining As Integer
            Get
                Return _end - _pos
            End Get
        End Property

        Public ReadOnly Property Raw() As Byte()
            Get
                Return _buf
            End Get
        End Property

        Private Sub Need(n As Integer)
            If _pos + n > _end Then Throw New EndOfStreamException($"数据不足：位于 {_pos} 处还需要 {n} 字节，但只剩 {_end - _pos} 字节。")
        End Sub

        Public Function ReadBytes(n As Integer) As Byte()
            If n < 0 Then Throw New ArgumentOutOfRangeException(NameOf(n))
            Need(n)
            Dim r(n - 1) As Byte
            If n > 0 Then System.Buffer.BlockCopy(_buf, _pos, r, 0, n)
            _pos += n
            Return r
        End Function

        Public Function ReadU8() As Byte
            Need(1)
            Dim v = _buf(_pos)
            _pos += 1
            Return v
        End Function

        Public Function ReadU16() As UShort
            Need(2)
            Dim v = CUShort(CInt(_buf(_pos)) Or (CInt(_buf(_pos + 1)) << 8))
            _pos += 2
            Return v
        End Function

        Public Function ReadU32() As UInteger
            Need(4)
            Dim v As UInteger = CUInt(_buf(_pos)) Or (CUInt(_buf(_pos + 1)) << 8) Or (CUInt(_buf(_pos + 2)) << 16) Or (CUInt(_buf(_pos + 3)) << 24)
            _pos += 4
            Return v
        End Function

        Public Function ReadU64() As ULong
            Need(8)
            Dim lo = ReadU32()
            Dim hi = ReadU32()
            Return CULng(lo) Or (CULng(hi) << 32)
        End Function

        Public Function ReadI32() As Integer
            Need(4)
            Dim v As Integer = CInt(_buf(_pos)) Or (CInt(_buf(_pos + 1)) << 8) Or (CInt(_buf(_pos + 2)) << 16) Or (CInt(_buf(_pos + 3)) << 24)
            _pos += 4
            Return v
        End Function

        Public Sub Skip(n As Integer)
            Need(n)
            _pos += n
        End Sub

        Public Sub ExpectMagic(magic As Byte(), what As String)
            If Remaining < magic.Length Then Throw New InvalidDataException($"{what}：魔数长度不足。")
            For i As Integer = 0 To magic.Length - 1
                If _buf(_pos + i) <> magic(i) Then Throw New InvalidDataException($"{what}：魔数不匹配，这不是 SymbolPrint 识别的数据结构。")
            Next
            _pos += magic.Length
        End Sub

        Public Function Slice(from As Integer, count As Integer) As Byte()
            If from < 0 OrElse count < 0 OrElse from + count > _buf.Length Then Throw New ArgumentOutOfRangeException(NameOf(from))
            Dim r(count - 1) As Byte
            If count > 0 Then System.Buffer.BlockCopy(_buf, from, r, 0, count)
            Return r
        End Function

    End Class

    ''' <summary>小端字节写入器。</summary>
    Public NotInheritable Class ByteWriter
        Implements IDisposable

        Private ReadOnly _ms As MemoryStream

        Public Sub New()
            _ms = New MemoryStream()
        End Sub

        Public Sub New(capacity As Integer)
            _ms = New MemoryStream(capacity)
        End Sub

        Public ReadOnly Property Length As Integer
            Get
                Return CInt(_ms.Length)
            End Get
        End Property

        Public Sub WriteBytes(b As Byte())
            If b IsNot Nothing AndAlso b.Length > 0 Then _ms.Write(b, 0, b.Length)
        End Sub

        Public Sub WriteBytes(b As Byte(), offset As Integer, count As Integer)
            If count > 0 Then _ms.Write(b, offset, count)
        End Sub

        Public Sub WriteU8(v As Byte)
            _ms.WriteByte(v)
        End Sub

        Public Sub WriteU16(v As UShort)
            _ms.WriteByte(CByte(v And &HFFUS))
            _ms.WriteByte(CByte((v >> 8) And &HFFUS))
        End Sub

        Public Sub WriteU32(v As UInteger)
            _ms.WriteByte(CByte(v And &HFFUI))
            _ms.WriteByte(CByte((v >> 8) And &HFFUI))
            _ms.WriteByte(CByte((v >> 16) And &HFFUI))
            _ms.WriteByte(CByte((v >> 24) And &HFFUI))
        End Sub

        Public Sub WriteU64(v As ULong)
            WriteU32(CUInt(v And &HFFFFFFFFUL))
            WriteU32(CUInt((v >> 32) And &HFFFFFFFFUL))
        End Sub

        Public Sub WriteI32(v As Integer)
            WriteU32(CUInt(v And &HFFFFFFFFL))
        End Sub

        Public Function ToArray() As Byte()
            Return _ms.ToArray()
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            _ms.Dispose()
        End Sub

    End Class

    Public Module Bin

        Public Function Concat(ParamArray parts As Byte()()) As Byte()
            Dim total As Integer = 0
            For Each p In parts
                If p IsNot Nothing Then total += p.Length
            Next
            Dim r(total - 1) As Byte
            Dim off As Integer = 0
            For Each p In parts
                If p IsNot Nothing AndAlso p.Length > 0 Then
                    System.Buffer.BlockCopy(p, 0, r, off, p.Length)
                    off += p.Length
                End If
            Next
            Return r
        End Function

        Public Function ConstBytes(s As String) As Byte()
            Return Encoding.ASCII.GetBytes(s)
        End Function

        Public Function RandomBytes(n As Integer) As Byte()
            Dim b(n - 1) As Byte
            Security.Cryptography.RandomNumberGenerator.Fill(b)
            Return b
        End Function

        Public Function Hex(b As Byte()) As String
            If b Is Nothing Then Return ""
            Return Convert.ToHexString(b)
        End Function

        Public Function FromHex(s As String) As Byte()
            If String.IsNullOrEmpty(s) Then Return Array.Empty(Of Byte)()
            Return Convert.FromHexString(s.Replace(" "c, "").Replace("-"c, ""))
        End Function

        Public Function Same(a As Byte(), b As Byte()) As Boolean
            If a Is Nothing OrElse b Is Nothing Then Return a Is b
            If a.Length <> b.Length Then Return False
            Return Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b)
        End Function

        Public Function Utf8(b As Byte()) As String
            If b Is Nothing Then Return ""
            Return Encoding.UTF8.GetString(b)
        End Function

        Public Function Utf8Bytes(s As String) As Byte()
            If s Is Nothing Then Return Array.Empty(Of Byte)()
            Return Encoding.UTF8.GetBytes(s)
        End Function

    End Module

End Namespace
