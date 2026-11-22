Imports System
Imports System.Security.Cryptography

Namespace Crypto

    Public NotInheritable Class AesGcmUtil

        Public Const KeyLen As Integer = 32
        Public Const NonceLen As Integer = 12
        Public Const TagLen As Integer = 16

        Public Shared Function NewKey() As Byte()
            Return Util.Bin.RandomBytes(KeyLen)
        End Function

        Public Shared Function NewNonce() As Byte()
            Return Util.Bin.RandomBytes(NonceLen)
        End Function

        ''' <summary>加密，返回 (密文, 认证标签)。</summary>
        Public Shared Function Encrypt(key As Byte(), nonce As Byte(), plain As Byte(), Optional aad As Byte() = Nothing) As (Cipher As Byte(), Tag As Byte())
            If key Is Nothing OrElse key.Length <> KeyLen Then Throw New ArgumentException("AES-256 密钥必须为 32 字节。", NameOf(key))
            If nonce Is Nothing OrElse nonce.Length <> NonceLen Then Throw New ArgumentException("Nonce 必须为 12 字节。", NameOf(nonce))
            If plain Is Nothing Then plain = Array.Empty(Of Byte)()
            Dim cipher(plain.Length - 1) As Byte
            Dim tag(TagLen - 1) As Byte
            Using g As New AesGcm(key, TagLen)
                g.Encrypt(nonce, plain, cipher, tag, aad)
            End Using
            Return (cipher, tag)
        End Function

        ''' <summary>解密；认证失败抛 CryptographicException。</summary>
        Public Shared Function Decrypt(key As Byte(), nonce As Byte(), cipher As Byte(), tag As Byte(), Optional aad As Byte() = Nothing) As Byte()
            If key Is Nothing OrElse key.Length <> KeyLen Then Throw New ArgumentException("AES-256 密钥必须为 32 字节。", NameOf(key))
            If nonce Is Nothing OrElse nonce.Length <> NonceLen Then Throw New ArgumentException("Nonce 必须为 12 字节。", NameOf(nonce))
            If cipher Is Nothing Then cipher = Array.Empty(Of Byte)()
            If tag Is Nothing OrElse tag.Length <> TagLen Then Throw New ArgumentException("认证标签必须为 16 字节。", NameOf(tag))
            Dim plain(cipher.Length - 1) As Byte
            Using g As New AesGcm(key, TagLen)
                g.Decrypt(nonce, cipher, tag, plain, aad)
            End Using
            Return plain
        End Function

        ''' <summary>用口令派生密钥后加密（保护章 .bin 内的 ECC 私钥）。</summary>
        Public Shared Function SealWithPassword(plain As Byte(), password As String, iterations As Integer, ByRef salt As Byte(), ByRef nonce As Byte(), ByRef tag As Byte()) As Byte()
            salt = Util.Bin.RandomBytes(16)
            nonce = NewNonce()
            Dim key = Sha3Kdf.Pbkdf2(Util.Bin.Utf8Bytes(password), salt, iterations, KeyLen)
            Dim r = Encrypt(key, nonce, plain, Util.Bin.ConstBytes("SymbolPrint-PrivateKey-v1"))
            tag = r.Tag
            Return r.Cipher
        End Function

        Public Shared Function OpenWithPassword(cipher As Byte(), password As String, iterations As Integer, salt As Byte(), nonce As Byte(), tag As Byte()) As Byte()
            Dim key = Sha3Kdf.Pbkdf2(Util.Bin.Utf8Bytes(password), salt, iterations, KeyLen)
            Return Decrypt(key, nonce, cipher, tag, Util.Bin.ConstBytes("SymbolPrint-PrivateKey-v1"))
        End Function

    End Class

End Namespace
