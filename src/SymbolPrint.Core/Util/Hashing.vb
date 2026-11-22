Imports System
Imports System.IO
Imports Org.BouncyCastle.Crypto.Digests

Namespace Util

    ''' <summary>
    ''' 对"文件前缀 + 若干字节补丁 + 若干置零区间"做流式 SHA3-512。
    ''' 用途：验证时要证明"原文件未被改动"，而我们的信封是追加/插入进去的，
    ''' 必须把文件还原成"没有我们那段"的样子再算哈希。逐个载体自己知道怎么还原，
    ''' 这里只提供通用的"读一块、打补丁、喂摘要"能力（1 MiB 一块，带进度与取消）。
    ''' </summary>
    Public Module Hashing

        Public NotInheritable Class Patch
            Public Property Offset As Long
            Public Property Bytes As Byte()
        End Class

        Private Const ChunkSize As Integer = 1 << 20

        Public Function HashPrefixWithPatches(path As String, limit As Long,
                                              Optional patches As Patch() = Nothing,
                                              Optional sink As IProgressSink = Nothing,
                                              Optional stage As String = "") As Byte()
            If limit < 0 Then Throw New ArgumentOutOfRangeException(NameOf(limit))
            Dim name As String = If(String.IsNullOrEmpty(stage), "计算原文件 SHA3-512", stage)
            Dim digest As New Sha3Digest(512)
            Dim buf(ChunkSize - 1) As Byte

            Using fs As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                Dim pos As Long = 0
                While pos < limit
                    ProgressExt.ThrowIfCancelled(sink)
                    Dim want As Integer = CInt(Math.Min(CLng(ChunkSize), limit - pos))
                    fs.Position = pos
                    Dim got As Integer = 0
                    While got < want
                        Dim n = fs.Read(buf, got, want - got)
                        If n <= 0 Then Exit While
                        got += n
                    End While
                    If got <= 0 Then Exit While

                    If patches IsNot Nothing Then ApplyPatches(buf, pos, got, patches)

                    digest.BlockUpdate(buf, 0, got)
                    pos += got
                    ProgressExt.Push(sink, name, pos / CDbl(limit), ProgressExt.BytesDetail(pos, limit))
                End While
            End Using

            Dim outp(63) As Byte
            digest.DoFinal(outp, 0)
            ProgressExt.Push(sink, name, 1.0)
            Return outp
        End Function

        ''' <summary>
        ''' 计算"整份文件去掉 [skipStart, skipStart+skipLen) 这一段"之后的 SHA3-512（可选再打补丁）。
        ''' 这是验证"原文件未被改动"的统一手段：不管我们把信封追加在尾部、塞进注释、还是插在中间，
        ''' 都能把文件还原成盖章前的样子再算哈希。
        ''' </summary>
        Public Function HashFileSkipping(path As String, skipStart As Long, skipLen As Long,
                                         Optional patches As Patch() = Nothing,
                                         Optional sink As IProgressSink = Nothing,
                                         Optional stage As String = "") As Byte()
            If skipStart < 0 Then skipStart = 0
            If skipLen < 0 Then skipLen = 0
            Dim total As Long = New FileInfo(path).Length
            Dim skipEnd As Long = Math.Min(total, skipStart + skipLen)
            Dim name As String = If(String.IsNullOrEmpty(stage), "计算原文件 SHA3-512", stage)

            Dim digest As New Sha3Digest(512)
            Dim buf(ChunkSize - 1) As Byte
            Dim done As Long = 0
            Dim effectiveTotal As Long = total - Math.Max(0L, skipEnd - skipStart)
            If effectiveTotal <= 0 Then effectiveTotal = 1

            Using fs As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                Dim pos As Long = 0
                While pos < total
                    ProgressExt.ThrowIfCancelled(sink)
                    ' 跳过我们的区间
                    If pos >= skipStart AndAlso pos < skipEnd Then
                        pos = skipEnd
                        Continue While
                    End If

                    Dim available As Long = total - pos
                    If pos < skipStart Then available = Math.Min(available, skipStart - pos)
                    Dim want As Integer = CInt(Math.Min(CLng(ChunkSize), available))

                    fs.Position = pos
                    Dim got As Integer = 0
                    While got < want
                        Dim n = fs.Read(buf, got, want - got)
                        If n <= 0 Then Exit While
                        got += n
                    End While
                    If got <= 0 Then Exit While

                    If patches IsNot Nothing Then ApplyPatches(buf, pos, got, patches)

                    digest.BlockUpdate(buf, 0, got)
                    pos += got
                    done += got
                    ProgressExt.Push(sink, name, done / CDbl(effectiveTotal), ProgressExt.BytesDetail(done, effectiveTotal))
                End While
            End Using

            Dim outp(63) As Byte
            digest.DoFinal(outp, 0)
            ProgressExt.Push(sink, name, 1.0)
            Return outp
        End Function

        Private Sub ApplyPatches(buf As Byte(), chunkStart As Long, chunkLen As Integer, patches As Patch())
            Dim chunkEnd = chunkStart + chunkLen - 1
            For Each p In patches
                If p Is Nothing OrElse p.Bytes Is Nothing OrElse p.Bytes.Length = 0 Then Continue For
                Dim pStart = p.Offset
                Dim pEnd = p.Offset + p.Bytes.Length - 1
                If pEnd < chunkStart OrElse pStart > chunkEnd Then Continue For
                Dim fromAbs = Math.Max(pStart, chunkStart)
                Dim toAbs = Math.Min(pEnd, chunkEnd)
                Dim idxAbs As Long = fromAbs
                While idxAbs <= toAbs
                    buf(CInt(idxAbs - chunkStart)) = p.Bytes(CInt(idxAbs - pStart))
                    idxAbs += 1
                End While
            Next
        End Sub

        ''' <summary>造一个"把 [offset, offset+len) 全部置零"的补丁。</summary>
        Public Function ZeroPatch(offset As Long, len As Integer) As Patch
            Return New Patch With {.Offset = offset, .Bytes = New Byte(Math.Max(len - 1, 0)) {}}
        End Function

        ''' <summary>造一个"把 offset 处的 2 字节改写为 v（小端）"的补丁。</summary>
        Public Function U16Patch(offset As Long, v As Integer) As Patch
            Return New Patch With {.Offset = offset, .Bytes = New Byte() {CByte(v And &HFF), CByte((v >> 8) And &HFF)}}
        End Function

    End Module

End Namespace
