Imports System
Imports Org.BouncyCastle.Crypto.Digests

Namespace Crypto

    ''' <summary>
    ''' SHA3-512 的薄封装（底层用 BouncyCastle 的 Sha3Digest）。
    ''' 为什么不直接用 .NET 的 SHA3_512：Windows 上 CNG 不提供 SHA3，会抛
    ''' PlatformNotSupportedException（本机 .NET 8/9 实测），而 BouncyCastle 是纯托管、跨平台一致。
    ''' 类名保留 Keccak512 以免大改调用点；实际算法是 **SHA3-512**（填充 0x06，不是老 Keccak）。
    ''' </summary>
    Public NotInheritable Class Keccak512

        Public Const OutputLen As Integer = 64
        ''' <summary>SHA3-512 的速率：576 bit = 72 字节（HMAC 块长也用它）。</summary>
        Public Const Rate As Integer = 72

        Public Shared Function Hash(data As Byte()) As Byte()
            Dim d As New Sha3Digest(512)
            If data IsNot Nothing AndAlso data.Length > 0 Then d.BlockUpdate(data, 0, data.Length)
            Dim out(OutputLen - 1) As Byte
            d.DoFinal(out, 0)
            Return out
        End Function

        ''' <summary>增量哈希器：大文件按块喂进来算，可边算边上报进度。</summary>
        Public NotInheritable Class Hasher

            Private ReadOnly _digest As Sha3Digest
            Private _finished As Boolean

            Public Sub New()
                _digest = New Sha3Digest(512)
            End Sub

            Public Sub Update(data As Byte())
                If data Is Nothing OrElse data.Length = 0 Then Return
                Update(data, 0, data.Length)
            End Sub

            Public Sub Update(data As Byte(), offset As Integer, count As Integer)
                If count <= 0 Then Return
                If _finished Then Throw New InvalidOperationException("哈希已收尾，不能再继续吸收数据。")
                If data Is Nothing Then Throw New ArgumentNullException(NameOf(data))
                If offset < 0 OrElse count < 0 OrElse offset + count > data.Length Then Throw New ArgumentOutOfRangeException(NameOf(offset))
                _digest.BlockUpdate(data, offset, count)
            End Sub

            ''' <summary>输出 64 字节摘要（只能调用一次）。</summary>
            Public Function Finish() As Byte()
                If _finished Then Throw New InvalidOperationException("哈希已收尾，不能重复取值。")
                _finished = True
                Dim out(OutputLen - 1) As Byte
                _digest.DoFinal(out, 0)
                Return out
            End Function
        End Class

    End Class

End Namespace
