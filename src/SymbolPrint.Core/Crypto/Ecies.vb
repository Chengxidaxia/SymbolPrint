Imports System
Imports System.Security.Cryptography

Namespace Crypto

    Public Enum EcCurveId As Byte
        NistP256 = 1
        NistP384 = 2
        NistP521 = 3
    End Enum

    Public NotInheritable Class EcKeys
        Public Property CurveId As EcCurveId
        Public Property PublicSpki As Byte()
        Public Property PrivatePkcs8 As Byte()
        Public ReadOnly Property CurveName As String
            Get
                Return Ecies.CurveNameOf(CurveId)
            End Get
        End Property
    End Class

    ''' <summary>
    ''' ECIES：ECDH（临时密钥对 ↔ 接收方公钥）→ HKDF-SHA3-512 → AES-256-GCM。
    ''' 信封格式（小端）：
    '''   u8  ver=1 | u8 curveId | u8 pointLen | point | nonce(12) | tag(16) | ciphertext
    ''' point 为非压缩点 0x04||X||Y。
    ''' </summary>
    Public Module Ecies

        Public Const EnvelopeVersion As Byte = 1
        Private Const KdfInfo As String = "SymbolPrint-ECIES-v1"

        Public Function CurveOf(id As EcCurveId) As ECCurve
            Select Case id
                Case EcCurveId.NistP256
                    Return ECCurve.NamedCurves.nistP256
                Case EcCurveId.NistP384
                    Return ECCurve.NamedCurves.nistP384
                Case EcCurveId.NistP521
                    Return ECCurve.NamedCurves.nistP521
                Case Else
                    Throw New ArgumentOutOfRangeException(NameOf(id), "未知的椭圆曲线编号。")
            End Select
        End Function

        Public Function CurveNameOf(id As EcCurveId) As String
            Select Case id
                Case EcCurveId.NistP256
                    Return "nistP256"
                Case EcCurveId.NistP384
                    Return "nistP384"
                Case EcCurveId.NistP521
                    Return "nistP521"
                Case Else
                    Return "unknown"
            End Select
        End Function

        Public Function ParseCurveName(name As String) As EcCurveId
            Dim s As String = If(name, "").Trim().ToLowerInvariant()
            Select Case s
                Case "nistp256", "p-256", "p256", "secp256r1", "prime256v1"
                    Return EcCurveId.NistP256
                Case "nistp384", "p-384", "p384", "secp384r1"
                    Return EcCurveId.NistP384
                Case "nistp521", "p-521", "p521", "secp521r1"
                    Return EcCurveId.NistP521
                Case Else
                    Throw New ArgumentException($"不支持的椭圆曲线名称：{name}")
            End Select
        End Function

        ''' <summary>生成新的 ECC 密钥对（默认 P-256）。</summary>
        Public Function GenerateKeys(Optional curve As EcCurveId = EcCurveId.NistP256) As EcKeys
            Using ecdh = ECDiffieHellman.Create(CurveOf(curve))
                Dim k As New EcKeys()
                k.CurveId = curve
                k.PrivatePkcs8 = ecdh.ExportPkcs8PrivateKey()
                k.PublicSpki = ecdh.ExportSubjectPublicKeyInfo()
                Return k
            End Using
        End Function

        Private Function PointOf(id As EcCurveId, spki As Byte()) As Byte()
            Using ecdh = ECDiffieHellman.Create()
                Dim read As Integer = 0
                ecdh.ImportSubjectPublicKeyInfo(spki, read)
                Dim p = ecdh.ExportParameters(False)
                Dim qx = p.Q.X
                Dim qy = p.Q.Y
                Dim r(1 + qx.Length + qy.Length - 1) As Byte
                r(0) = &H4
                Buffer.BlockCopy(qx, 0, r, 1, qx.Length)
                Buffer.BlockCopy(qy, 0, r, 1 + qx.Length, qy.Length)
                Return r
            End Using
        End Function

        Private Function PublicFromPoint(id As EcCurveId, point As Byte()) As ECDiffieHellman
            If point.Length < 3 OrElse point(0) <> &H4 Then Throw New InvalidOperationException("ECIES 临时公钥格式无效（应为非压缩点）。")
            Dim half As Integer = (point.Length - 1) \ 2
            Dim x(half - 1) As Byte
            Dim y(half - 1) As Byte
            Buffer.BlockCopy(point, 1, x, 0, half)
            Buffer.BlockCopy(point, 1 + half, y, 0, half)
            Dim prm As New ECParameters()
            prm.Curve = CurveOf(id)
            prm.Q = New ECPoint With {.X = x, .Y = y}
            Return ECDiffieHellman.Create(prm)
        End Function

        Private Function DeriveAesKey(secret As Byte(), ephPoint As Byte(), peerPoint As Byte()) As Byte()
            Dim salt = Util.Bin.Concat(ephPoint, peerPoint)
            Return Sha3Kdf.Hkdf(secret, salt, Util.Bin.Utf8Bytes(KdfInfo), AesGcmUtil.KeyLen)
        End Function

        ''' <summary>
        ''' 用接收方公钥（SPKI）加密明文，产出 ECIES 信封。
        ''' ephPrivatePkcs8 / nonceOverride 仅供一致性向量测试注入固定值（正常调用不要传）。
        ''' </summary>
        Public Function Encrypt(recipientSpki As Byte(), plain As Byte(), curve As EcCurveId,
                                Optional ephPrivatePkcs8 As Byte() = Nothing,
                                Optional nonceOverride As Byte() = Nothing) As Byte()
            If plain Is Nothing Then plain = Array.Empty(Of Byte)()

            Dim recipPoint = PointOf(curve, recipientSpki)

            Using recip = ECDiffieHellman.Create()
                Dim read As Integer = 0
                recip.ImportSubjectPublicKeyInfo(recipientSpki, read)

                Dim eph As ECDiffieHellman
                If ephPrivatePkcs8 IsNot Nothing Then
                    eph = ECDiffieHellman.Create()
                    Dim got As Integer = 0
                    eph.ImportPkcs8PrivateKey(ephPrivatePkcs8, got)
                Else
                    eph = ECDiffieHellman.Create(CurveOf(curve))
                End If

                Using eph
                    Dim ephPrm = eph.ExportParameters(True)
                    Dim ephPoint(1 + ephPrm.Q.X.Length + ephPrm.Q.Y.Length - 1) As Byte
                    ephPoint(0) = &H4
                    Buffer.BlockCopy(ephPrm.Q.X, 0, ephPoint, 1, ephPrm.Q.X.Length)
                    Buffer.BlockCopy(ephPrm.Q.Y, 0, ephPoint, 1 + ephPrm.Q.X.Length, ephPrm.Q.Y.Length)

                    Dim secret = eph.DeriveRawSecretAgreement(recip.PublicKey)
                    Dim aesKey = DeriveAesKey(secret, ephPoint, recipPoint)
                    Dim nonce = If(nonceOverride, AesGcmUtil.NewNonce())
                    Dim r = AesGcmUtil.Encrypt(aesKey, nonce, plain)

                    Dim w As New Util.ByteWriter()
                    w.WriteU8(EnvelopeVersion)
                    w.WriteU8(CByte(curve))
                    w.WriteU8(CByte(ephPoint.Length))
                    w.WriteBytes(ephPoint)
                    w.WriteBytes(nonce)
                    w.WriteBytes(r.Tag)
                    w.WriteBytes(r.Cipher)
                    Return w.ToArray()
                End Using
            End Using
        End Function

        ''' <summary>用接收方私钥（PKCS#8）解出 ECIES 信封内容。</summary>
        Public Function Decrypt(privatePkcs8 As Byte(), envelope As Byte()) As Byte()
            Dim rdr As New Util.ByteReader(envelope)
            Dim ver = rdr.ReadU8()
            If ver <> EnvelopeVersion Then Throw New InvalidOperationException($"不支持的 ECIES 信封版本：{ver}")
            Dim curveId = CType(rdr.ReadU8(), EcCurveId)
            Dim plen = CInt(rdr.ReadU8())
            Dim ephPoint = rdr.ReadBytes(plen)
            Dim nonce = rdr.ReadBytes(AesGcmUtil.NonceLen)
            Dim tag = rdr.ReadBytes(AesGcmUtil.TagLen)
            Dim cipher = rdr.ReadBytes(rdr.Remaining)

            Using selfEcdh = ECDiffieHellman.Create()
                Dim read As Integer = 0
                selfEcdh.ImportPkcs8PrivateKey(privatePkcs8, read)

                Dim pub = selfEcdh.ExportParameters(False)
                Dim mine = Util.Bin.Concat(New Byte() {&H4}, pub.Q.X, pub.Q.Y)

                Using other = PublicFromPoint(curveId, ephPoint)
                    Dim secret = selfEcdh.DeriveRawSecretAgreement(other.PublicKey)
                    Dim aesKey = DeriveAesKey(secret, ephPoint, mine)
                    Return AesGcmUtil.Decrypt(aesKey, nonce, cipher, tag)
                End Using
            End Using
        End Function

        ''' <summary>公钥指纹（SHA3-512 前 8 字节，冒号分隔）。</summary>
        Public Function Fingerprint(spki As Byte()) As String
            Return Sha3Kdf.ShortFingerprint(Sha3Kdf.Hash512(spki), 8)
        End Function

    End Module

End Namespace
