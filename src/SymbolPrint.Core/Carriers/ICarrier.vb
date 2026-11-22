Imports System
Imports System.IO

Namespace Carriers

    Public Enum CarrierKind
        TrailingAppend = 1     ' 文件尾部追加（PNG/JPEG/PDF 等）
        ZipComment = 2         ' ZIP EOCD 注释区（zip/docx/pptx/xlsx）
        SevenZipGap = 3        ' 7z 的 NextHeader 与 StartHeader 之间的空隙
        RtfGroup = 4           ' RTF 忽略型目标组 {\*\symprint ...}
        ZeroWidthText = 5      ' 文本尾部零宽字符
        StampReserved = 6      ' 章 .bin 自身保留区（自指）
    End Enum

    ''' <summary>载体写入选项。</summary>
    Public NotInheritable Class CarrierOptions
        ''' <summary>压缩包重打包时使用的口令（仅当必须重打包且归档加密时需要）。</summary>
        Public Property Password As String = Nothing
        ''' <summary>是否允许在必要时刻重打包归档（会改变归档字节，但保留全部原有条目与加密状态）。</summary>
        Public Property AllowRepack As Boolean = True
        Public Property Log As Action(Of String) = Nothing
    End Class

    Public Interface IStampCarrier
        ReadOnly Property Id As String
        ReadOnly Property DisplayName As String
        ''' <summary>该载体是否能处理此文件（按扩展名/结构判断）。</summary>
        Function CanHandle(filePath As String) As Boolean
        ''' <summary>读出当前文件里的完整信封（payload + 48 字节 footer）；没有则返回 Nothing。</summary>
        Function ReadBlob(filePath As String) As Byte()
        ''' <summary>写入信封。实现必须做到：只追加/只插入，绝不改动文件里任何已有字节（ZIP 注释长度字段除外，且可逆）。</summary>
        Sub WriteBlob(filePath As String, blob As Byte(), options As CarrierOptions)
        ''' <summary>精确摘除本程序写入的那一段，其余字节保持原样。</summary>
        Function RemoveBlob(filePath As String) As Boolean
        ''' <summary>可用容量（字节）；无限返回 Long.MaxValue。</summary>
        Function Capacity(filePath As String) As Long
    End Interface

    ''' <summary>
    ''' 可选能力：载体自己知道"怎么把文件还原成没有我们那段的样子"。
    ''' 不实现它的载体按默认规则处理：我们的信封就是文件最末尾那 blobLen 字节，还原 = 取前 len-blobLen 字节。
    ''' ZIP 注释区（改写了 2 字节注释长度）与章 .bin 自指区（要把保留区视作全零）必须实现它。
    ''' </summary>
    ''' <summary>
    ''' 载体若要读取用户设置（文本插入位置、RTF 组名、零宽码点、ZIP 注释上限等）就实现它。
    ''' 不实现的载体一律使用内置默认值——所以接口方法签名保持不变，不影响既有调用方。
    ''' </summary>
    Public Interface ISettingsAware
        Sub ApplySettings(s As SymbolPrint.Core.Model.CarrierSettings)
    End Interface

    Public Interface IOriginalHashAware
        ''' <summary>计算"摘掉我们信封后"的文件内容 SHA3-512。</summary>
        Function HashOriginal(filePath As String, blobLen As Integer,
                              Optional sink As Util.IProgressSink = Nothing,
                              Optional stage As String = "") As Byte()
    End Interface

    Public Module CarrierExt
        Public Function ExtOf(path As String) As String
            Return IO.Path.GetExtension(path).ToLowerInvariant()
        End Function

        Public Function ReadTail(path As String, count As Integer) As Byte()
            Using fs As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                If fs.Length < count Then count = CInt(fs.Length)
                fs.Position = fs.Length - count
                Dim buf(count - 1) As Byte
                Dim got As Integer = 0
                While got < count
                    Dim n = fs.Read(buf, got, count - got)
                    If n <= 0 Then Exit While
                    got += n
                End While
                Return buf
            End Using
        End Function

        Public Function ReadRange(path As String, offset As Long, count As Integer) As Byte()
            Using fs As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                If offset < 0 OrElse offset + count > fs.Length Then Return Nothing
                fs.Position = offset
                Dim buf(count - 1) As Byte
                Dim got As Integer = 0
                While got < count
                    Dim n = fs.Read(buf, got, count - got)
                    If n <= 0 Then Exit While
                    got += n
                End While
                If got <> count Then Return Nothing
                Return buf
            End Using
        End Function
    End Module

End Namespace
