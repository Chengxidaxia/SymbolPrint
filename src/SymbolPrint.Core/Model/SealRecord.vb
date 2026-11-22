Imports System
Imports System.IO
Imports SymbolPrint.Core.Crypto
Imports SymbolPrint.Core.Util

Namespace Model

    ''' <summary>
    ''' 盖章记录（写入宿主文件空白区的原始内容）。版本 1 布局（小端）：
    '''   u8   sealVersion
    '''   u8   sealFlags          bit0 视觉章已烧入（图片/PDF）
    '''   u16  headerSize = 96
    '''   u64  stampTimeMs
    '''   [64] ownContentSha3     本记录自身内容的 SHA3-512
    '''   [32] key1               加密节1 的密钥
    '''   [12] nonce1
    '''   [16] tag1
    '''   u32  section1Len
    '''   ...  section1Cipher     AES-GCM(key1) 密文
    '''
    ''' section1 明文：
    '''   u8 ver | u32 section2Len | section2Cipher | [32]stampPbkdf2 | [16]pbkdf2Salt | u32 kdfIterations | [8]stampId8
    ''' section2 明文（ECIES 加密）：
    '''   u8 ver | u32 sealLen | 章字节 | [64]原文件SHA3-512 | [32]章字节PBKDF2 | [16]pbkdf2Salt
    ''' </summary>
    Public NotInheritable Class SealRecord

        ''' <summary>v1 的固定布局（老记录）。仍需能解析，否则已经盖过章的文件全部验不过。</summary>
        Public Const Version As Byte = 1
        ''' <summary>当前写出的版本：头部带长度字段与 HKDF info，允许各参数可调。</summary>
        Public Const CurrentVersion As Byte = 2
        Public Const HeaderSize As Integer = 96
        Public Const Pbkdf2Len As Integer = 32

        Public Property StampTime As DateTimeOffset = DateTimeOffset.Now
        Public Property VisualBurned As Boolean = False
        Public Property Key1 As Byte()
        Public Property Nonce1 As Byte()
        Public Property Tag1 As Byte()
        Public Property Section1Cipher As Byte()
        Public Property OwnContentSha3 As Byte()

        ' ---- v2 才有的长度描述；v1 记录解析后这些保持默认值 ----
        Public Property RecordVer As Byte = CurrentVersion
        Public Property SaltLen As Integer = 16
        Public Property Key1Len As Integer = 32
        Public Property NonceLen As Integer = 12
        Public Property TagLen As Integer = 16
        Public Property Pbkdf2LenUsed As Integer = Pbkdf2Len
        Public Property HkdfInfo As String = "SymbolPrint-ECIES-v1"

        ''' <summary>把本记录序列化为最终写入宿主文件的原始字节（再经压缩与信封封装）。</summary>
        Public Function Serialize() As Byte()
            Dim w As New ByteWriter()
            If RecordVer >= 2 Then
                Dim info = Util.Bin.Utf8Bytes(If(HkdfInfo, ""))
                w.WriteU8(CurrentVersion)
                Dim flags2 As Byte = 0
                If VisualBurned Then flags2 = CByte(flags2 Or 1)
                w.WriteU8(flags2)
                w.WriteU8(CByte(Key1Len))
                w.WriteU8(CByte(NonceLen))
                w.WriteU8(CByte(TagLen))
                w.WriteU8(CByte(Pbkdf2LenUsed))
                w.WriteU8(CByte(SaltLen))
                w.WriteU8(CByte(Math.Min(255, info.Length)))
                w.WriteBytes(info)
                w.WriteU16(CUShort(HeaderSize))
                w.WriteU64(CULng(StampTime.ToUnixTimeMilliseconds()))
                w.WriteBytes(OwnContentSha3)
                w.WriteBytes(Key1)
                w.WriteBytes(Nonce1)
                w.WriteBytes(Tag1)
                w.WriteU32(CUInt(Section1Cipher.Length))
                w.WriteBytes(Section1Cipher)
                Return w.ToArray()
            End If

            ' v1：保持原样，不要动（旧向量要能复现）
            w.WriteU8(Version)
            Dim flags As Byte = 0
            If VisualBurned Then flags = CByte(flags Or 1)
            w.WriteU8(flags)
            w.WriteU16(CUShort(HeaderSize))
            w.WriteU64(CULng(StampTime.ToUnixTimeMilliseconds()))
            w.WriteBytes(OwnContentSha3)
            w.WriteBytes(Key1)
            w.WriteBytes(Nonce1)
            w.WriteBytes(Tag1)
            w.WriteU32(CUInt(Section1Cipher.Length))
            w.WriteBytes(Section1Cipher)
            Return w.ToArray()
        End Function

        ''' <summary>计算 ownContentSha3：覆盖版本/标志/长度描述/时间戳/密钥/nonce/tag/密文。</summary>
        Public Sub SealOwnHash()
            OwnContentSha3 = Sha3Kdf.Hash512(OwnHashInput())
        End Sub

        ''' <summary>ownContentSha3 覆盖的字节；v1 与 v2 布局不同，必须按记录版本各算各的。</summary>
        Private Function OwnHashInput() As Byte()
            Dim w As New ByteWriter()
            If RecordVer >= 2 Then
                Dim info = Util.Bin.Utf8Bytes(If(HkdfInfo, ""))
                w.WriteU8(CurrentVersion)
                w.WriteU8(CByte(If(VisualBurned, 1, 0)))
                w.WriteU8(CByte(Key1Len))
                w.WriteU8(CByte(NonceLen))
                w.WriteU8(CByte(TagLen))
                w.WriteU8(CByte(Pbkdf2LenUsed))
                w.WriteU8(CByte(SaltLen))
                w.WriteU8(CByte(Math.Min(255, info.Length)))
                w.WriteBytes(info)
                w.WriteU64(CULng(StampTime.ToUnixTimeMilliseconds()))
                w.WriteBytes(Key1)
                w.WriteBytes(Nonce1)
                w.WriteBytes(Tag1)
                w.WriteBytes(Section1Cipher)
                Return w.ToArray()
            End If

            w.WriteU8(Version)
            w.WriteU8(CByte(If(VisualBurned, 1, 0)))
            w.WriteU64(CULng(StampTime.ToUnixTimeMilliseconds()))
            w.WriteBytes(Key1)
            w.WriteBytes(Nonce1)
            w.WriteBytes(Tag1)
            w.WriteBytes(Section1Cipher)
            Return w.ToArray()
        End Function

        Public Shared Function Parse(raw As Byte()) As SealRecord
            Dim r As New ByteReader(raw)
            Dim ver = r.ReadU8()
            If ver <> Version AndAlso ver <> CurrentVersion Then
                Throw New InvalidDataException($"不支持的盖章记录版本：{ver}（本程序支持 1 / {CurrentVersion}）。")
            End If

            Dim rec As New SealRecord()
            rec.RecordVer = ver

            If ver >= 2 Then
                Dim flags2 = r.ReadU8()
                rec.Key1Len = r.ReadU8()
                rec.NonceLen = r.ReadU8()
                rec.TagLen = r.ReadU8()
                rec.Pbkdf2LenUsed = r.ReadU8()
                rec.SaltLen = r.ReadU8()
                Dim infoLen = CInt(r.ReadU8())
                If infoLen > 0 Then rec.HkdfInfo = Util.Bin.Utf8(r.ReadBytes(infoLen)) Else rec.HkdfInfo = ""

                Dim hs2 = r.ReadU16()
                If hs2 <> HeaderSize Then Throw New InvalidDataException($"盖章记录头长度异常：{hs2}")
                rec.VisualBurned = (flags2 And 1) <> 0
                rec.StampTime = DateTimeOffset.FromUnixTimeMilliseconds(CLng(r.ReadU64()))
                rec.OwnContentSha3 = r.ReadBytes(64)
                rec.Key1 = r.ReadBytes(Math.Max(1, rec.Key1Len))
                rec.Nonce1 = r.ReadBytes(Math.Max(1, rec.NonceLen))
                rec.Tag1 = r.ReadBytes(Math.Max(1, rec.TagLen))
                Dim slen2 = CInt(r.ReadU32())
                rec.Section1Cipher = r.ReadBytes(slen2)
                Return rec
            End If

            Dim flags = r.ReadU8()
            Dim hs = r.ReadU16()
            If hs <> HeaderSize Then Throw New InvalidDataException($"盖章记录头长度异常：{hs}")
            rec.StampTime = DateTimeOffset.FromUnixTimeMilliseconds(CLng(r.ReadU64()))
            rec.VisualBurned = (flags And 1) <> 0
            rec.OwnContentSha3 = r.ReadBytes(64)
            rec.Key1 = r.ReadBytes(32)
            rec.Nonce1 = r.ReadBytes(12)
            rec.Tag1 = r.ReadBytes(16)
            Dim slen = CInt(r.ReadU32())
            rec.Section1Cipher = r.ReadBytes(slen)
            Return rec
        End Function

        ''' <summary>校验 ownContentSha3 是否与内容一致。</summary>
        Public Function VerifyOwnHash() As Boolean
            If OwnContentSha3 Is Nothing OrElse OwnContentSha3.Length <> 64 Then Return False
            Return Bin.Same(Sha3Kdf.Hash512(OwnHashInput()), OwnContentSha3)
        End Function

        Public Function HeaderLength() As Integer
            Return HeaderSize
        End Function

    End Class

    ''' <summary>EOCD 尾部信封：payload || footer(48B)。所有"尾部追加型"载体共用这一封装。</summary>
    Public NotInheritable Class SealEnvelope

        Public Const FooterSize As Integer = 48
        Public Const FooterVersion As UShort = 1
        Public Shared ReadOnly FooterMagic As Byte() = Text.Encoding.ASCII.GetBytes("SYMBOLPRINTTAIL" & ChrW(0))

        Public Shared Function Build(sealedPayload As Byte(), sealVersion As Byte) As Byte()
            Dim sha = Sha3Kdf.Hash512(sealedPayload)
            Dim w As New ByteWriter()
            w.WriteBytes(sealedPayload)
            w.WriteBytes(FooterMagic)
            w.WriteU16(FooterVersion)
            w.WriteU16(sealVersion)
            w.WriteU32(CUInt(sealedPayload.Length))
            w.WriteU64(BitConverter.ToUInt64(sha, 0))
            w.WriteU64(CULng(sealedPayload.Length + FooterSize))
            w.WriteU64(0)
            Return w.ToArray()
        End Function

        Public NotInheritable Class FooterInfo
            Public Property FooterVersion As UShort
            Public Property SealVersion As Byte
            Public Property PayloadLength As Integer
            Public Property TotalLength As Integer
            Public Property PayloadHash8 As ULong
        End Class

        ''' <summary>从一段字节的末尾解析 footer（要求该段末尾正好是我们的 footer）。</summary>
        Public Shared Function TryParseFooter(tail As Byte(), offset As Integer) As FooterInfo
            If tail Is Nothing OrElse offset < 0 OrElse offset + FooterSize > tail.Length Then Return Nothing
            For i As Integer = 0 To FooterMagic.Length - 1
                If tail(offset + i) <> FooterMagic(i) Then Return Nothing
            Next
            Dim r As New ByteReader(tail, offset + FooterMagic.Length, FooterSize - FooterMagic.Length)
            Dim f As New FooterInfo()
            f.FooterVersion = r.ReadU16()
            f.SealVersion = CByte(r.ReadU16())
            f.PayloadLength = CInt(r.ReadU32())
            f.PayloadHash8 = r.ReadU64()
            f.TotalLength = CInt(Math.Min(r.ReadU64(), Integer.MaxValue))
            Return f
        End Function

        Public Shared Function VerifyPayloadHash(payload As Byte(), hash8 As ULong) As Boolean
            Dim sha = Sha3Kdf.Hash512(payload)
            Return BitConverter.ToUInt64(sha, 0) = hash8
        End Function

    End Class

End Namespace
