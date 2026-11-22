Imports System
Imports System.IO
Imports SymbolPrint.Core.Crypto
Imports SymbolPrint.Core.Util

Namespace Model

    Public Module StampTags
        Public Const Label As Byte = &H1
        Public Const SealBytes As Byte = &H2
        Public Const Description As Byte = &H3
        Public Const Svg As Byte = &H4
        Public Const CreatedAt As Byte = &H5
        Public Const PublicKey As Byte = &H6
        Public Const PrivateKey As Byte = &H7
        Public Const Curve As Byte = &H8
        Public Const KeyProtection As Byte = &H9
        Public Const KdfSalt As Byte = &HA
        Public Const KdfIterations As Byte = &HB
        Public Const EncNonce As Byte = &HC
        Public Const EncTag As Byte = &HD
        Public Const EncPrivateKey As Byte = &HE
        Public Const Generator As Byte = &HF
    End Module

    ''' <summary>
    ''' 章文件（.bin）。结构（小端）：
    '''   [0,128)          固定文件头
    '''   [128,128+65536)  自指空白区（正常全 0；自章数据对齐写在末尾）
    '''   [65664, ...)     TLV 正文
    ''' 身份（Identity）= SHA3-512(文件头 || 65536 个 0 || 正文)，即自指空白区不参与身份计算，
    ''' 所以章可以给自己盖章而不改变自身身份，空白区前部也可留给将来使用。
    ''' </summary>
    Public NotInheritable Class StampFile

        Public Const FormatVersion As UShort = 1
        Public Const HeaderSize As Integer = 128
        Public Const ReservedSize As Integer = 65536
        Public Const BodyOffset As Long = HeaderSize + ReservedSize
        Public Const DefaultKdfIterations As Integer = 40000

        Public Shared ReadOnly Magic As Byte() = {&H53, &H50, &H53, &H54, &H41, &H4D, &H50, &H1A}

        ' ---- 内容 ----
        Public Property Label As String = ""
        Public Property Description As String = ""
        Public Property SvgMarkup As String = ""
        Public Property SealRawBytes As Byte() = Array.Empty(Of Byte)()
        Public Property CreatedAt As DateTimeOffset = DateTimeOffset.Now
        Public Property Keys As EcKeys
        Public Property PassphraseProtected As Boolean = False
        Public Property KdfSalt As Byte()
        Public Property KdfIterations As Integer = DefaultKdfIterations
        Public Property EncNonce As Byte()
        Public Property EncTag As Byte()
        Public Property EncPrivate As Byte()
        Public Property FileStampId As Byte()

        ' ---- 运行时 ----
        Public Property SourcePath As String = ""
        Private _privatePlain As Byte() = Nothing

        Public Sub New()
            Me.Keys = Ecies.GenerateKeys(EcCurveId.NistP256)
        End Sub

        Public ReadOnly Property HasSvg As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(SvgMarkup)
            End Get
        End Property

        Public ReadOnly Property HasSealBytes As Boolean
            Get
                Return SealRawBytes IsNot Nothing AndAlso SealRawBytes.Length > 0
            End Get
        End Property

        Public ReadOnly Property IsLocked As Boolean
            Get
                Return PassphraseProtected AndAlso _privatePlain Is Nothing
            End Get
        End Property

        Public ReadOnly Property StampId As Byte()
            Get
                Return Sha3Kdf.Hash512(Keys.PublicSpki)
            End Get
        End Property

        Public ReadOnly Property StampIdShort As String
            Get
                Return Sha3Kdf.ShortFingerprint(StampId, 8)
            End Get
        End Property

        Public Function GetPrivateKey() As Byte()
            If PassphraseProtected Then
                If _privatePlain Is Nothing Then Throw New InvalidOperationException("该章的私钥受口令保护，需要先提供口令解锁。")
                Return _privatePlain
            End If
            Return Keys.PrivatePkcs8
        End Function

        ''' <summary>
        ''' 设置保护口令；空字符串表示取消口令保护。
        ''' iterations 为 PBKDF2 迭代次数，&lt;= 0 时用默认值（设置页可调，影响暴力破解成本）。
        ''' </summary>
        Public Sub SetPassphrase(password As String, Optional iterations As Integer = 0)
            If String.IsNullOrEmpty(password) Then
                PassphraseProtected = False
                EncPrivate = Nothing
                EncNonce = Nothing
                EncTag = Nothing
                KdfSalt = Nothing
                _privatePlain = Keys.PrivatePkcs8
                Return
            End If
            KdfIterations = If(iterations > 0, iterations, DefaultKdfIterations)
            Dim salt As Byte() = Nothing
            Dim nonce As Byte() = Nothing
            Dim tag As Byte() = Nothing
            Dim cipher = AesGcmUtil.SealWithPassword(Keys.PrivatePkcs8, password, KdfIterations, salt, nonce, tag)
            KdfSalt = salt
            EncNonce = nonce
            EncTag = tag
            EncPrivate = cipher
            PassphraseProtected = True
            _privatePlain = Keys.PrivatePkcs8
        End Sub

        Public Sub Unlock(password As String)
            If Not PassphraseProtected Then Return
            Try
                _privatePlain = AesGcmUtil.OpenWithPassword(EncPrivate, password, KdfIterations, KdfSalt, EncNonce, EncTag)
            Catch ex As System.Security.Cryptography.CryptographicException
                Throw New InvalidOperationException("口令不正确（私钥解密失败）。", ex)
            End Try
        End Sub

        ' ================= 序列化 =================

        Private Shared Sub Tlv(w As ByteWriter, tag As Byte, data As Byte())
            If data Is Nothing Then data = Array.Empty(Of Byte)()
            w.WriteU8(tag)
            w.WriteU32(CUInt(data.Length))
            w.WriteBytes(data)
        End Sub

        Public Function BuildBody() As Byte()
            Dim w As New ByteWriter()
            Tlv(w, StampTags.Label, Bin.Utf8Bytes(Label))
            Tlv(w, StampTags.Description, Bin.Utf8Bytes(Description))
            If HasSvg Then Tlv(w, StampTags.Svg, Bin.Utf8Bytes(SvgMarkup))
            If HasSealBytes Then Tlv(w, StampTags.SealBytes, SealRawBytes)
            Tlv(w, StampTags.CreatedAt, U64Of(CULng(CreatedAt.ToUnixTimeMilliseconds())))
            Tlv(w, StampTags.PublicKey, Keys.PublicSpki)
            Tlv(w, StampTags.Curve, Bin.Utf8Bytes(Ecies.CurveNameOf(Keys.CurveId)))
            Tlv(w, StampTags.KeyProtection, New Byte() {If(PassphraseProtected, CByte(1), CByte(0))})
            If PassphraseProtected Then
                Tlv(w, StampTags.KdfSalt, KdfSalt)
                Tlv(w, StampTags.KdfIterations, U32Of(CUInt(KdfIterations)))
                Tlv(w, StampTags.EncNonce, EncNonce)
                Tlv(w, StampTags.EncTag, EncTag)
                Tlv(w, StampTags.EncPrivateKey, EncPrivate)
            Else
                Tlv(w, StampTags.PrivateKey, Keys.PrivatePkcs8)
            End If
            Tlv(w, StampTags.Generator, Bin.Utf8Bytes("SymbolPrint/1.0"))
            Return w.ToArray()
        End Function

        Private Shared Function U64Of(v As ULong) As Byte()
            Dim w As New ByteWriter()
            w.WriteU64(v)
            Return w.ToArray()
        End Function

        Private Shared Function U32Of(v As UInteger) As Byte()
            Dim w As New ByteWriter()
            w.WriteU32(v)
            Return w.ToArray()
        End Function

        Public Sub Save(targetPath As String)
            Dim body = BuildBody()
            Dim flags As UShort = 0US
            If HasSvg Then flags = CUShort(flags Or 1US)
            If HasSealBytes Then flags = CUShort(flags Or 2US)
            If PassphraseProtected Then flags = CUShort(flags Or 4US)

            Dim sid = StampId
            Dim bw As New ByteWriter()
            bw.WriteBytes(Magic)                                       ' 8
            bw.WriteU16(FormatVersion)                                 ' 2 -> 10
            bw.WriteU16(flags)                                         ' 2 -> 12
            bw.WriteU32(CUInt(HeaderSize))                             ' 4 -> 16
            bw.WriteU64(CULng(HeaderSize))                             ' 8 -> 24
            bw.WriteU64(CULng(ReservedSize))                           ' 8 -> 32
            bw.WriteU64(CULng(BodyOffset))                             ' 8 -> 40
            bw.WriteU64(CULng(body.Length))                            ' 8 -> 48
            bw.WriteU64(CULng(CreatedAt.ToUnixTimeMilliseconds()))      ' 8 -> 56
            bw.WriteU64(0UL)                                           ' 8 -> 64
            bw.WriteBytes(New Byte(31) {})                             ' 32 -> 96
            bw.WriteU32(CRC32.Compute(body))                           ' 4 -> 100
            bw.WriteU32(0UI)                                           ' 4 -> 104
            bw.WriteBytes(New Byte(23) {})                             ' 24 -> 128
            Dim hdr = bw.ToArray()
            If hdr.Length <> HeaderSize Then Throw New InvalidOperationException($"章文件头长度异常：{hdr.Length}（应为 {HeaderSize}）。")

            Buffer.BlockCopy(sid, 0, hdr, 64, 32)
            Dim crcBytes = U32Of(CRC32.Compute(hdr))
            Buffer.BlockCopy(crcBytes, 0, hdr, 100, 4)

            Dim reserved(ReservedSize - 1) As Byte
            If IO.File.Exists(targetPath) Then
                Try
                    If New IO.FileInfo(targetPath).Length >= BodyOffset Then
                        Dim old = IO.File.ReadAllBytes(targetPath)
                        Buffer.BlockCopy(old, HeaderSize, reserved, 0, ReservedSize)
                    End If
                Catch
                End Try
            End If

            Dim dir = IO.Path.GetDirectoryName(IO.Path.GetFullPath(targetPath))
            If Not String.IsNullOrEmpty(dir) AndAlso Not IO.Directory.Exists(dir) Then IO.Directory.CreateDirectory(dir)

            Using fs As New FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None)
                fs.Write(hdr, 0, hdr.Length)
                fs.Write(reserved, 0, reserved.Length)
                fs.Write(body, 0, body.Length)
            End Using

            SourcePath = targetPath
            FileStampId = sid
            _privatePlain = If(PassphraseProtected, Nothing, Keys.PrivatePkcs8)
        End Sub

        Public Shared Function Load(sourcePath As String, Optional password As String = Nothing) As StampFile
            Dim bytes = IO.File.ReadAllBytes(sourcePath)
            Dim doc = Parse(bytes)
            doc.SourcePath = sourcePath
            If doc.PassphraseProtected AndAlso password IsNot Nothing Then doc.Unlock(password)
            Return doc
        End Function

        Public Shared Function Parse(bytes As Byte()) As StampFile
            If bytes Is Nothing OrElse bytes.Length < HeaderSize Then Throw New InvalidDataException("文件太小，不是有效的章文件。")
            Dim r As New ByteReader(bytes)
            Dim m = r.ReadBytes(Magic.Length)
            If Not Bin.Same(m, Magic) Then Throw New InvalidDataException("魔数不匹配：这不是 SymbolPrint 章文件（.bin）。")
            Dim ver = r.ReadU16()
            If ver > FormatVersion Then Throw New InvalidDataException($"章文件版本 {ver} 高于本程序支持的 {FormatVersion}，请升级 SymbolPrint。")
            r.ReadU16()
            r.ReadU32()
            Dim reservedOffset = r.ReadU64()
            Dim reservedSize = r.ReadU64()
            Dim bodyOffset = r.ReadU64()
            Dim bodySize = r.ReadU64()
            Dim createdMs = r.ReadU64()
            r.ReadU64()
            Dim sid = r.ReadBytes(32)
            Dim bodyCrc = r.ReadU32()

            If reservedOffset <> HeaderSize OrElse reservedSize <> ReservedSize Then
                Throw New InvalidDataException("章文件的空白区参数与当前版本不一致，无法解析。")
            End If
            If bodyOffset + bodySize > bytes.LongLength Then Throw New InvalidDataException("章文件正文长度越界，文件可能已被截断。")

            Dim body(CInt(bodySize) - 1) As Byte
            Buffer.BlockCopy(bytes, CInt(bodyOffset), body, 0, CInt(bodySize))
            If CRC32.Compute(body) <> bodyCrc Then Throw New InvalidDataException("章文件正文 CRC 校验失败，文件已损坏。")

            Dim doc As New StampFile()
            doc.FileStampId = sid
            doc.CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(CLng(createdMs))
            doc.Keys = New EcKeys()

            Dim curveName As String = "nistP256"
            Dim prot As Byte = 0
            Dim br As New ByteReader(body)
            While br.Remaining > 0
                Dim tag = br.ReadU8()
                Dim len = CInt(br.ReadU32())
                Dim data = br.ReadBytes(len)
                Select Case tag
                    Case StampTags.Label
                        doc.Label = Bin.Utf8(data)
                    Case StampTags.Description
                        doc.Description = Bin.Utf8(data)
                    Case StampTags.Svg
                        doc.SvgMarkup = Bin.Utf8(data)
                    Case StampTags.SealBytes
                        doc.SealRawBytes = data
                    Case StampTags.PublicKey
                        doc.Keys.PublicSpki = data
                    Case StampTags.Curve
                        curveName = Bin.Utf8(data)
                    Case StampTags.KeyProtection
                        If data.Length > 0 Then prot = data(0)
                    Case StampTags.KdfSalt
                        doc.KdfSalt = data
                    Case StampTags.KdfIterations
                        Dim t As New ByteReader(data)
                        doc.KdfIterations = CInt(t.ReadU32())
                    Case StampTags.EncNonce
                        doc.EncNonce = data
                    Case StampTags.EncTag
                        doc.EncTag = data
                    Case StampTags.EncPrivateKey
                        doc.EncPrivate = data
                    Case StampTags.PrivateKey
                        doc.Keys.PrivatePkcs8 = data
                        doc._privatePlain = data
                End Select
            End While

            doc.Keys.CurveId = Ecies.ParseCurveName(curveName)
            doc.PassphraseProtected = (prot = 1)
            If Not doc.PassphraseProtected Then doc._privatePlain = doc.Keys.PrivatePkcs8
            If doc.Keys.PublicSpki Is Nothing Then Throw New InvalidDataException("章文件缺少公钥。")
            Return doc
        End Function

        Public Shared Function TryParse(bytes As Byte()) As StampFile
            Try
                Return Parse(bytes)
            Catch
                Return Nothing
            End Try
        End Function

        Public Shared Function HasMagic(path As String) As Boolean
            Try
                Using fs As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    If fs.Length < Magic.Length Then Return False
                    Dim b(Magic.Length - 1) As Byte
                    fs.Read(b, 0, b.Length)
                    Return Bin.Same(b, Magic)
                End Using
            Catch
                Return False
            End Try
        End Function

    End Class

End Namespace
