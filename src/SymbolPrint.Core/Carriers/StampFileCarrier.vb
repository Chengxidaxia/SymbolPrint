Imports System
Imports System.IO
Imports SymbolPrint.Core.Model
Imports SymbolPrint.Core.Util

Namespace Carriers

    ''' <summary>
    ''' 章文件自身的载体（自指）。章 .bin 在 [128, 128+65536) 留了一块空白区，
    ''' 身份 = SHA3-512(文件头 || 65536 个 0 || 正文)，即这块空白区**不参与身份计算**，
    ''' 所以章可以给自己盖章，而盖章前后这个章的身份（公钥指纹）完全不变。
    ''' 记录永远是"贴着保留区末尾"放的：占 [128+65536-n, 128+65536)。
    ''' 前面的空间留给别人/将来使用，我们只动自己那一段。
    ''' </summary>
    Public NotInheritable Class StampFileCarrier
        Implements IStampCarrier
        Implements IOriginalHashAware

        Public Const ReservedEnd As Long = StampFile.BodyOffset

        Public ReadOnly Property Id As String Implements IStampCarrier.Id
            Get
                Return "stamp-reserved"
            End Get
        End Property

        Public ReadOnly Property DisplayName As String Implements IStampCarrier.DisplayName
            Get
                Return "章文件保留区（自指）"
            End Get
        End Property

        Public Function CanHandle(filePath As String) As Boolean Implements IStampCarrier.CanHandle
            If Not String.Equals(CarrierExt.ExtOf(filePath), ".bin", StringComparison.OrdinalIgnoreCase) Then Return False
            Return StampFile.HasMagic(filePath)
        End Function

        Public Function Capacity(filePath As String) As Long Implements IStampCarrier.Capacity
            ' 保留区整块可用（记录贴末尾放，前面的空间将来留给他用）
            Return StampFile.ReservedSize
        End Function

        Public Function ReadBlob(filePath As String) As Byte() Implements IStampCarrier.ReadBlob
            Dim len As Long = New FileInfo(filePath).Length
            If len < StampFile.BodyOffset + 1 Then Return Nothing
            Dim tail = CarrierExt.ReadRange(filePath, ReservedEnd - StampFile.ReservedSize, StampFile.ReservedSize)
            If tail Is Nothing Then Return Nothing
            Dim f = SealEnvelope.TryParseFooter(tail, tail.Length - SealEnvelope.FooterSize)
            If f Is Nothing Then Return Nothing
            If f.TotalLength <= SealEnvelope.FooterSize OrElse f.TotalLength > tail.Length Then Return Nothing
            Dim blob(f.TotalLength - 1) As Byte
            Buffer.BlockCopy(tail, tail.Length - f.TotalLength, blob, 0, f.TotalLength)
            Return blob
        End Function

        Public Sub WriteBlob(filePath As String, blob As Byte(), options As CarrierOptions) Implements IStampCarrier.WriteBlob
            If blob Is Nothing OrElse blob.Length <= SealEnvelope.FooterSize Then Throw New ArgumentException("信封内容为空。", NameOf(blob))
            If blob.Length > StampFile.ReservedSize Then
                Throw New InvalidDataException($"章文件保留区容量不足：需要 {blob.Length} 字节，上限 {StampFile.ReservedSize} 字节。")
            End If

            ' 先记下旧记录的位置与长度，写入后把旧记录留下的残渣清零（那是我们自己的旧数据）
            Dim oldLen As Integer = 0
            Dim old = ReadBlob(filePath)
            If old IsNot Nothing Then oldLen = old.Length

            Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None)
                Dim startPos As Long = ReservedEnd - blob.Length
                fs.Position = startPos
                fs.Write(blob, 0, blob.Length)

                Dim oldStart As Long = ReservedEnd - oldLen
                If oldLen > blob.Length Then
                    ' 旧记录更长，把 [oldStart, startPos) 这段旧残留清零
                    Dim n As Integer = CInt(startPos - oldStart)
                    Dim zeros(n - 1) As Byte
                    fs.Position = oldStart
                    fs.Write(zeros, 0, zeros.Length)
                End If
                fs.Flush()
            End Using

            options?.Log?.Invoke($"已写入章文件保留区 {blob.Length} 字节（贴末尾放置；文件头、正文、身份均未改变）。")
        End Sub

        Public Function RemoveBlob(filePath As String) As Boolean Implements IStampCarrier.RemoveBlob
            Dim blob = ReadBlob(filePath)
            If blob Is Nothing Then Return False
            Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None)
                Dim zeros(blob.Length - 1) As Byte
                fs.Position = ReservedEnd - blob.Length
                fs.Write(zeros, 0, zeros.Length)
                fs.Flush()
            End Using
            Return True
        End Function

        ''' <summary>原文件哈希 = 把保留区整块当 0 后的 SHA3-512（与章身份定义一致）。</summary>
        Public Function HashOriginal(filePath As String, blobLen As Integer,
                                     Optional sink As IProgressSink = Nothing,
                                     Optional stage As String = "") As Byte() Implements IOriginalHashAware.HashOriginal
            Dim patches = New Hashing.Patch() {Hashing.ZeroPatch(StampFile.HeaderSize, StampFile.ReservedSize)}
            Return Hashing.HashPrefixWithPatches(filePath, New FileInfo(filePath).Length, patches, sink, stage)
        End Function

    End Class

End Namespace
