Imports System
Imports System.IO
Imports SymbolPrint.Core.Util

Namespace Model

    ''' <summary>
    ''' 写进宿主文件空白区的"压缩载荷"。布局（小端）：
    '''   u8   method     压缩方法（0=原样，1=Brotli）
    '''   u32  rawLength  压缩前的原始长度（用于解压后自检）
    '''   ...  data       压缩后的字节
    ''' 外层再由 SealEnvelope 套上 48 字节 footer（含长度、版本、哈希前 8 字节），交给载体写入。
    ''' </summary>
    Public NotInheritable Class SealedPayload

        Public Property Method As Byte = Comp.MethodNone
        Public Property RawLength As Integer
        Public Property Data As Byte()

        Public Shared Function Build(recordRaw As Byte(), quality As Integer,
                                     Optional sink As IProgressSink = Nothing,
                                     Optional stage As String = "",
                                     Optional algorithm As Byte = Comp.MethodBrotli,
                                     Optional skipIfLarger As Boolean = True) As Byte()
            Dim p = Comp.Pack(recordRaw, quality, sink, stage, algorithm, skipIfLarger)
            Dim w As New ByteWriter()
            w.WriteU8(p.Method)
            w.WriteU32(CUInt(recordRaw.Length))
            w.WriteBytes(p.Data)
            Return w.ToArray()
        End Function

        Public Shared Function Parse(payload As Byte()) As SealedPayload
            If payload Is Nothing OrElse payload.Length < 5 Then
                Throw New InvalidDataException("载荷太短，无法解析。")
            End If
            Dim r As New ByteReader(payload)
            Dim sp As New SealedPayload()
            sp.Method = r.ReadU8()
            sp.RawLength = CInt(r.ReadU32())
            sp.Data = r.ReadBytes(r.Remaining)
            Return sp
        End Function

        Public Function Unpack(Optional sink As IProgressSink = Nothing,
                               Optional stage As String = "") As Byte()
            Dim raw = Comp.Unpack(Method, Data, sink, stage)
            If RawLength > 0 AndAlso raw.Length <> RawLength Then
                Throw New InvalidDataException($"载荷长度不符：记录声明 {RawLength} 字节，实际解出 {raw.Length} 字节。")
            End If
            Return raw
        End Function

    End Class

End Namespace
