Imports System
Imports SymbolPrint.Core.Util
Imports Org.BouncyCastle.Crypto.Digests
Imports Org.BouncyCastle.Crypto.Macs
Imports Org.BouncyCastle.Crypto.Parameters

Namespace Crypto

    ''' <summary>
    ''' SHA3-512 及基于它的 HMAC / HKDF / PBKDF2。
    ''' 底层用 BouncyCastle（纯托管、跨平台一致），不依赖操作系统 CNG —— Windows 上 .NET 的 SHA3 会抛异常。
    ''' HMAC 按 NIST SP 800-185：HMAC-SHA3-512 的块长 B = 72 字节（= 速率 576 bit）。
    ''' 所有长循环都带进度上报与取消检查（sink.ThrowIfCancelled）。
    ''' </summary>
    Public Module Sha3Kdf

        Public Const HashLen As Integer = 64
        Public Const HmacBlockLen As Integer = 72

        Public Function Hash512(data As Byte()) As Byte()
            Return Keccak512.Hash(data)
        End Function

        Public Function Hash512OfParts(ParamArray parts As Byte()()) As Byte()
            Return Keccak512.Hash(Util.Bin.Concat(parts))
        End Function

        ''' <summary>
        ''' 流式计算文件的 SHA3-512（1 MiB 一块），边算边上报进度、可取消。
        ''' 大文件必须走这条路：整块读入既吃内存，又没法给用户任何进度反馈。
        ''' </summary>
        Public Function HashFile(path As String, Optional sink As Util.IProgressSink = Nothing,
                                 Optional stage As String = "") As Byte()
            Using fs As New IO.FileStream(path, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.ReadWrite)
                Return HashStream(fs, fs.Length, sink, stage)
            End Using
        End Function

        ''' <summary>流式计算 SHA3-512；totalBytes &lt;= 0 表示总长未知（只报"不确定进度"+已处理量）。</summary>
        Public Function HashStream(input As IO.Stream, totalBytes As Long, Optional sink As Util.IProgressSink = Nothing,
                                   Optional stage As String = "") As Byte()
            Dim name As String = If(String.IsNullOrEmpty(stage), "计算 SHA3-512", stage)
            Dim hasher As New Keccak512.Hasher()
            Dim buf((1 << 20) - 1) As Byte
            Dim doneBytes As Long = 0

            Util.ProgressExt.Push(sink, name & "…", -1.0)

            While True
                sink.ThrowIfCancelled()
                Dim n As Integer = input.Read(buf, 0, buf.Length)
                If n <= 0 Then Exit While
                hasher.Update(buf, 0, n)
                doneBytes += n
                If totalBytes > 0 Then
                    Util.ProgressExt.Push(sink, name, doneBytes / CDbl(totalBytes), Util.ProgressExt.BytesDetail(doneBytes, totalBytes))
                Else
                    Util.ProgressExt.Push(sink, name, -1.0, Util.Comp.HumanSize(doneBytes))
                End If
            End While

            Dim digest = hasher.Finish()
            Util.ProgressExt.Push(sink, name, 1.0)
            Return digest
        End Function

        ''' <summary>HMAC-SHA3-512（一次算完）。</summary>
        Public Function HmacSha3_512(key As Byte(), message As Byte()) As Byte()
            Dim mac As New HMac(New Sha3Digest(512))
            mac.Init(New KeyParameter(If(key, Array.Empty(Of Byte)())))
            If message IsNot Nothing AndAlso message.Length > 0 Then mac.BlockUpdate(message, 0, message.Length)
            Dim out(HashLen - 1) As Byte
            mac.DoFinal(out, 0)
            Return out
        End Function

        ''' <summary>复用同一个 HMac 实例算一段（算完自动 Reset，可继续用）。</summary>
        Private Function MacOf(mac As HMac, data As Byte(), offset As Integer, count As Integer) As Byte()
            If count > 0 Then mac.BlockUpdate(data, offset, count)
            Dim o(HashLen - 1) As Byte
            mac.DoFinal(o, 0)
            mac.Reset()
            Return o
        End Function

        ''' <summary>PBKDF2-HMAC-SHA3-512（RFC 8018）。迭代多时按步长上报进度，可取消。</summary>
        Public Function Pbkdf2(password As Byte(), salt As Byte(), iterations As Integer, derivedLen As Integer,
                               Optional sink As Util.IProgressSink = Nothing, Optional stage As String = "") As Byte()
            If iterations < 1 Then Throw New ArgumentOutOfRangeException(NameOf(iterations), "迭代次数必须 >= 1。")
            If derivedLen < 1 Then Throw New ArgumentOutOfRangeException(NameOf(derivedLen))
            If salt Is Nothing Then salt = Array.Empty(Of Byte)()

            Dim mac As New HMac(New Sha3Digest(512))
            mac.Init(New KeyParameter(If(password, Array.Empty(Of Byte)())))

            Dim blocks As Integer = CInt(Math.Ceiling(derivedLen / CDbl(HashLen)))
            Dim out(derivedLen - 1) As Byte
            Dim produced As Integer = 0
            Dim name As String = If(String.IsNullOrEmpty(stage), "派生密钥（PBKDF2）", stage)

            ' 进度粒度：总共约上报 200 次
            Dim totalSteps As Long = CLng(blocks) * iterations
            Dim reportStep As Long = Math.Max(1L, totalSteps \ 200L)
            Dim stepsDone As Long = 0
            Dim nextReport As Long = 0

            Util.ProgressExt.Push(sink, name, 0.0, $"0 / {totalSteps} 次迭代")

            Dim saltBlock(salt.Length + 3) As Byte
            Buffer.BlockCopy(salt, 0, saltBlock, 0, salt.Length)

            For blk As Integer = 1 To blocks
                saltBlock(salt.Length) = CByte((blk >> 24) And &HFF)
                saltBlock(salt.Length + 1) = CByte((blk >> 16) And &HFF)
                saltBlock(salt.Length + 2) = CByte((blk >> 8) And &HFF)
                saltBlock(salt.Length + 3) = CByte(blk And &HFF)

                Dim u = MacOf(mac, saltBlock, 0, saltBlock.Length)
                Dim t(HashLen - 1) As Byte
                Buffer.BlockCopy(u, 0, t, 0, HashLen)
                stepsDone += 1

                For it As Integer = 2 To iterations
                    u = MacOf(mac, u, 0, u.Length)
                    For j As Integer = 0 To HashLen - 1
                        t(j) = CByte(t(j) Xor u(j))
                    Next
                    stepsDone += 1
                    If stepsDone >= nextReport Then
                        sink.ThrowIfCancelled()
                        Util.ProgressExt.Push(sink, name, stepsDone / CDbl(totalSteps), $"{stepsDone} / {totalSteps} 次迭代")
                        nextReport = stepsDone + reportStep
                    End If
                Next

                Dim take As Integer = Math.Min(HashLen, derivedLen - produced)
                Buffer.BlockCopy(t, 0, out, produced, take)
                produced += take
            Next

            Util.ProgressExt.Push(sink, name, 1.0)
            Return out
        End Function

        ''' <summary>HKDF-SHA3-512（RFC 5869）。</summary>
        Public Function Hkdf(ikm As Byte(), salt As Byte(), info As Byte(), outputLen As Integer) As Byte()
            If salt Is Nothing OrElse salt.Length = 0 Then salt = New Byte(63) {}
            Dim prk = HmacSha3_512(salt, ikm)
            Dim mac As New HMac(New Sha3Digest(512))
            mac.Init(New KeyParameter(prk))

            Dim okm(outputLen - 1) As Byte
            Dim t As Byte() = Array.Empty(Of Byte)()
            Dim produced As Integer = 0
            Dim counter As Byte = 1

            While produced < outputLen
                Dim parts As New Collections.Generic.List(Of Byte())
                If t.Length > 0 Then parts.Add(t)
                If info IsNot Nothing AndAlso info.Length > 0 Then parts.Add(info)
                parts.Add(New Byte() {counter})
                Dim blockInput = Util.Bin.Concat(parts.ToArray())
                t = MacOf(mac, blockInput, 0, blockInput.Length)
                Dim take As Integer = Math.Min(t.Length, outputLen - produced)
                Buffer.BlockCopy(t, 0, okm, produced, take)
                produced += take
                counter = CByte(counter + 1)
            End While
            Return okm
        End Function

        ''' <summary>用于显示/比对的短指纹（每 2 字节一组，冒号分隔）。</summary>
        Public Function ShortFingerprint(hash As Byte(), Optional groups As Integer = 8) As String
            If hash Is Nothing Then Return ""
            Dim n As Integer = Math.Min(hash.Length, groups * 2)
            Dim sb As New Text.StringBuilder()
            For i As Integer = 0 To n - 1
                If i > 0 AndAlso i Mod 2 = 0 Then sb.Append(":"c)
                sb.Append(hash(i).ToString("X2"))
            Next
            Return sb.ToString()
        End Function

    End Module

End Namespace
