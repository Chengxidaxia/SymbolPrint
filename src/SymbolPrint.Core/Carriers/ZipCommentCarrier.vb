Imports System
Imports System.IO
Imports SymbolPrint.Core.Model
Imports SymbolPrint.Core.Util

Namespace Carriers

    ''' <summary>
    ''' ZIP 注释区载体（zip / docx / pptx / xlsx / jar / apk / epub …）。
    '''
    ''' 为什么不用"往归档里加文件"：那样用户在压缩软件里能直接看到我们的原始数据，
    ''' 而且加密归档还得先解密再重打。ZIP 的 End of Central Directory 记录带一个注释字段
    ''' （最长 65535 字节），它不属于任何归档条目、不出现在文件列表里、本身也不加密。
    '''
    ''' 做法：新注释 = 原注释（原样保留） + 我们的信封；只改写 EOCD 的"注释长度"两个字节并追加注释。
    ''' 归档内所有条目、中央目录、以及原注释的既有字节都不动。移除时精确截掉我们那段。
    ''' 容量上限 65535 字节（压缩后的信封要塞得进去）；超了由上层决定是否重打包。
    ''' </summary>
    Public NotInheritable Class ZipCommentCarrier
        Implements ISettingsAware
        Implements IStampCarrier
        Implements IOriginalHashAware

        Public Const MaxComment As Integer = 65535
        ' 可被设置下调（ZIP 规范上限仍是 65535，只能更小不能更大）
        Private _commentMax As Integer = MaxComment

        ''' <summary>ZIP 注释区上限来自「高级设置 → 载体 → ZIP 注释区上限」。</summary>
        Public Sub ApplySettings(s As SymbolPrint.Core.Model.CarrierSettings) Implements ISettingsAware.ApplySettings
            If s Is Nothing Then Return
            If s.ZipCommentMaxBytes >= 1024 AndAlso s.ZipCommentMaxBytes <= MaxComment Then
                _commentMax = s.ZipCommentMaxBytes
            End If
        End Sub
        Private Const EocdSize As Integer = 22

        Private Shared ReadOnly Exts As String() = {".zip", ".zipx", ".docx", ".docm", ".dotx", ".dotm",
                                                    ".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm",
                                                    ".xlsx", ".xlsm", ".xltx", ".xltm", ".xlsb",
                                                    ".jar", ".apk", ".epub", ".vsdx", ".xps", ".odt", ".ods", ".odp"}

        Public ReadOnly Property Id As String Implements IStampCarrier.Id
            Get
                Return "zip-comment"
            End Get
        End Property

        Public ReadOnly Property DisplayName As String Implements IStampCarrier.DisplayName
            Get
                Return "ZIP 注释区"
            End Get
        End Property

        Public Function CanHandle(filePath As String) As Boolean Implements IStampCarrier.CanHandle
            Return Array.IndexOf(Exts, CarrierExt.ExtOf(filePath)) >= 0
        End Function

        Private Shared Function ReadFully(fs As FileStream, buf As Byte(), offset As Integer, count As Integer) As Integer
            Dim got As Integer = 0
            While got < count
                Dim n = fs.Read(buf, offset + got, count - got)
                If n <= 0 Then Exit While
                got += n
            End While
            Return got
        End Function

        ''' <summary>
        ''' 定位 EOCD。要求 EOCD 的注释长度正好收尾到文件末尾 —— 否则说明文件后面还挂着别的东西
        ''' （比如别的程序追加的数据），此时本载体不参与，避免把别人的数据裁掉。
        ''' </summary>
        Public Shared Function FindEocd(fs As FileStream) As Long
            Dim len = fs.Length
            If len < EocdSize Then Return -1
            Dim searchLen As Integer = CInt(Math.Min(CLng(MaxComment) + EocdSize, len))
            Dim buf(searchLen - 1) As Byte
            fs.Position = len - searchLen
            If ReadFully(fs, buf, 0, searchLen) <> searchLen Then Return -1

            For i As Integer = searchLen - EocdSize To 0 Step -1
                If buf(i) = &H50 AndAlso buf(i + 1) = &H4B AndAlso buf(i + 2) = &H05 AndAlso buf(i + 3) = &H06 Then
                    Dim clen As Integer = CInt(buf(i + 20)) Or (CInt(buf(i + 21)) << 8)
                    Dim eocdAbs As Long = (len - searchLen) + i
                    If eocdAbs + EocdSize + clen = len Then Return eocdAbs
                End If
            Next
            Return -1
        End Function

        Private Shared Function ReadComment(fs As FileStream, eocd As Long) As Byte()
            fs.Position = eocd + 20
            Dim hdr(1) As Byte
            If ReadFully(fs, hdr, 0, 2) <> 2 Then Return Nothing
            Dim clen As Integer = CInt(hdr(0)) Or (CInt(hdr(1)) << 8)
            If clen = 0 Then Return Array.Empty(Of Byte)()
            Dim c(clen - 1) As Byte
            fs.Position = eocd + EocdSize
            If ReadFully(fs, c, 0, clen) <> clen Then Return Nothing
            Return c
        End Function

        ''' <summary>若注释末尾正好是我们的信封，返回去掉信封后的原注释；否则原样返回。</summary>
        Private Shared Function StripOurBlob(comment As Byte()) As Byte()
            If comment Is Nothing OrElse comment.Length < SealEnvelope.FooterSize Then Return comment
            Dim f = SealEnvelope.TryParseFooter(comment, comment.Length - SealEnvelope.FooterSize)
            If f Is Nothing Then Return comment
            If f.TotalLength <= 0 OrElse f.TotalLength > comment.Length Then Return comment
            Dim keepLen As Integer = comment.Length - f.TotalLength
            Dim keep(keepLen - 1) As Byte
            If keepLen > 0 Then Buffer.BlockCopy(comment, 0, keep, 0, keepLen)
            Return keep
        End Function

        ''' <summary>剩余可用注释容量；返回 -1 表示"找不到 EOCD，这不是一个可写的 ZIP"。</summary>
        Public Function Capacity(filePath As String) As Long Implements IStampCarrier.Capacity
            Try
                Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    Dim eocd = FindEocd(fs)
                    If eocd < 0 Then Return -1
                    Dim c = ReadComment(fs, eocd)
                    Dim baseLen As Integer = If(StripOurBlob(c) Is Nothing, 0, StripOurBlob(c).Length)
                    Return Math.Max(0, _commentMax - baseLen)
                End Using
            Catch
                Return -1
            End Try
        End Function

        Public Function ReadBlob(filePath As String) As Byte() Implements IStampCarrier.ReadBlob
            Try
                Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    Dim eocd = FindEocd(fs)
                    If eocd < 0 Then Return Nothing
                    Dim c = ReadComment(fs, eocd)
                    If c Is Nothing OrElse c.Length < SealEnvelope.FooterSize Then Return Nothing
                    Dim f = SealEnvelope.TryParseFooter(c, c.Length - SealEnvelope.FooterSize)
                    If f Is Nothing Then Return Nothing
                    If f.TotalLength <= 0 OrElse f.TotalLength > c.Length Then Return Nothing
                    Dim blob(f.TotalLength - 1) As Byte
                    Buffer.BlockCopy(c, c.Length - f.TotalLength, blob, 0, f.TotalLength)
                    Return blob
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        Public Sub WriteBlob(filePath As String, blob As Byte(), options As CarrierOptions) Implements IStampCarrier.WriteBlob
            If blob Is Nothing OrElse blob.Length <= SealEnvelope.FooterSize Then Throw New ArgumentException("信封内容为空。", NameOf(blob))

            Using fs As New FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Dim eocd = FindEocd(fs)
                If eocd < 0 Then Throw New InvalidDataException("找不到 ZIP 中央目录结束记录（EOCD），文件可能已损坏或不是标准 ZIP。")

                Dim oldComment = ReadComment(fs, eocd)
                Dim base_ = StripOurBlob(oldComment)
                Dim newLen As Integer = base_.Length + blob.Length
                If newLen > MaxComment Then
                    Throw New InvalidDataException($"ZIP 注释区容量不足：需要 {newLen} 字节，上限 {MaxComment} 字节（原注释已占 {base_.Length} 字节）。")
                End If

                ' 只裁掉旧注释，[0, eocd+20) 的所有字节（含全部条目与中央目录）一个都不动
                fs.SetLength(eocd + 20)
                fs.Position = eocd + 20
                fs.WriteByte(CByte(newLen And &HFF))
                fs.WriteByte(CByte((newLen >> 8) And &HFF))
                If base_.Length > 0 Then fs.Write(base_, 0, base_.Length)
                fs.Write(blob, 0, blob.Length)
                fs.Flush()

                options?.Log?.Invoke($"已写入 ZIP 注释区 {blob.Length} 字节（原注释 {base_.Length} 字节原样保留，归档条目未改动）。")
            End Using
        End Sub

        Public Function RemoveBlob(filePath As String) As Boolean Implements IStampCarrier.RemoveBlob
            Using fs As New FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                Dim eocd = FindEocd(fs)
                If eocd < 0 Then Return False
                Dim c = ReadComment(fs, eocd)
                If c Is Nothing OrElse c.Length = 0 Then Return False
                Dim base_ = StripOurBlob(c)
                If base_.Length = c.Length Then Return False ' 里面没有我们的东西
                fs.SetLength(eocd + 20)
                fs.Position = eocd + 20
                fs.WriteByte(CByte(base_.Length And &HFF))
                fs.WriteByte(CByte((base_.Length >> 8) And &HFF))
                If base_.Length > 0 Then fs.Write(base_, 0, base_.Length)
                fs.Flush()
                Return True
            End Using
        End Function

        ''' <summary>
        ''' 还原"原文件"：我们只是把信封接在原注释后面、并改写了 EOCD 的注释长度两个字节，
        ''' 所以还原 = 取前 (文件长 - blobLen) 字节，同时把那两个字节改回原注释长度。
        ''' </summary>
        Public Function HashOriginal(filePath As String, blobLen As Integer,
                                     Optional sink As Util.IProgressSink = Nothing,
                                     Optional stage As String = "") As Byte() Implements IOriginalHashAware.HashOriginal
            Dim eocd As Long
            Dim curCommentLen As Integer
            Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                eocd = FindEocd(fs)
                If eocd < 0 Then Throw New InvalidDataException("找不到 ZIP 的 EOCD，无法还原原文件内容。")
                Dim c = ReadComment(fs, eocd)
                curCommentLen = If(c Is Nothing, 0, c.Length)
            End Using
            Dim origLen As Integer = curCommentLen - blobLen
            If origLen < 0 Then origLen = 0
            Dim limit As Long = eocd + 22 + origLen
            Dim patches = New Util.Hashing.Patch() {Util.Hashing.U16Patch(eocd + 20, origLen)}
            Return Util.Hashing.HashPrefixWithPatches(filePath, limit, patches, sink, stage)
        End Function

    End Class

End Namespace
