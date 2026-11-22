Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports SymbolPrint.Core.Carriers
Imports SymbolPrint.Core.Crypto
Imports SymbolPrint.Core.Model
Imports SymbolPrint.Core.Util

Namespace Service

    ''' <summary>按扩展名/结构挑选载体。</summary>
    Public NotInheritable Class CarrierRegistry

        Private Shared ReadOnly All As IStampCarrier() = {
            New StampFileCarrier(),
            New ZipCommentCarrier(),
            New SevenZipCarrier(),
            New RtfGroupCarrier(),
            New ZeroWidthTextCarrier(),
            New TrailerCarrier()
        }

        Public Shared ReadOnly Property Carriers As IReadOnlyList(Of IStampCarrier)
            Get
                Return All
            End Get
        End Property

        ''' <summary>
        ''' 把「载体」设置分发给支持它��载体（文本码点、RTF 组名、ZIP 注释上限），
        ''' 并记住载体优先级。不改设置的调用方完全不受影响。
        ''' </summary>
        Public Shared Sub Configure(s As CarrierSettings)
            If s Is Nothing Then Return
            _preferred = If(String.IsNullOrWhiteSpace(s.PreferredOrder),
                            Array.Empty(Of String)(),
                            s.PreferredOrder.Split(","c).Select(Function(x) x.Trim().ToLowerInvariant()).
                                Where(Function(x) x.Length > 0).ToArray())
            For Each c In All
                Dim aware = TryCast(c, ISettingsAware)
                If aware IsNot Nothing Then
                    Try
                        aware.ApplySettings(s)
                    Catch
                        ' 单个载体配置失败不能影响整个程序
                    End Try
                End If
            Next
        End Sub

        Private Shared _preferred As String() = Array.Empty(Of String)()

        ''' <summary>按「载体优先级」排列后的候选顺序；没配置过就是内置顺序。</summary>
        Private Shared Function InPreferredOrder() As List(Of IStampCarrier)
            Dim list As New List(Of IStampCarrier)
            For Each id In _preferred
                Dim c = ById(id)
                If c IsNot Nothing AndAlso Not list.Contains(c) Then list.Add(c)
            Next
            For Each c In All
                If Not list.Contains(c) Then list.Add(c)
            Next
            Return list
        End Function

        Public Shared Function Find(filePath As String) As IStampCarrier
            ' 1) 先按文件头判定真实容器：改成 .png 的 zip、改成 .bin 的图片都不会选错载体
            Dim byHead = ForKind(FileSniffer.KindOf(filePath), filePath)
            If byHead IsNot Nothing Then Return byHead
            ' 2) 头认不出（空文件、加密/私有格式）→ 按载体优先级 + 扩展名兜底
            For Each c In InPreferredOrder()
                Try
                    If c.CanHandle(filePath) Then Return c
                Catch
                    ' 载体探测失败就继续试下一个
                End Try
            Next
            Return Nothing
        End Function

        ''' <summary>按容器类型挑载体；PlainText 只在扩展名也像文本时采纳，避免误伤无魔数的图片。</summary>
        Public Shared Function ForKind(kind As ContainerKind, Optional filePath As String = Nothing) As IStampCarrier
            Select Case kind
                Case ContainerKind.Png, ContainerKind.Jpeg, ContainerKind.Gif, ContainerKind.Bmp,
                     ContainerKind.Tiff, ContainerKind.Webp, ContainerKind.Ico, ContainerKind.Pdf,
                     ContainerKind.Psd
                    Return ById("trailer")
                Case ContainerKind.Zip
                    Return ById("zip-comment")
                Case ContainerKind.SevenZip
                    Return ById("7z-tail")
                Case ContainerKind.Rtf
                    Return ById("rtf-group")
                Case ContainerKind.StampBin
                    Return ById("stamp-reserved")
                Case ContainerKind.PlainText
                    Dim ext = If(filePath, "").ToLowerInvariant()
                    ext = Path.GetExtension(ext)
                    If ext = "" OrElse ext = ".txt" OrElse ext = ".md" OrElse ext = ".markdown" OrElse ext = ".log" Then
                        Return ById("zerowidth-text")
                    End If
            End Select
            Return Nothing
        End Function

        Private Shared Function ById(id As String) As IStampCarrier
            For Each c In All
                If String.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase) Then Return c
            Next
            Return Nothing
        End Function

        ''' <summary>受支持的扩展名清单（给 UI 与报错提示用）。</summary>
        ''' <summary>
        ''' 可作为载体的文件扩展名（小写，带点）。
        ''' 只能放纯扩展名：这些值会被直接拼进文件对话框的过滤器（"*" &amp; ext），
        ''' 之前在 ".bin" 后面跟了中文注释，过滤器就变成了 *.bin(章文件)，导致 .bin 永远选不到。
        ''' 给人看的说明请用 <see cref="SupportedExtensionsDisplay"/>。
        ''' </summary>
        Public Shared Function SupportedExtensions() As String()
            Return New String() {".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".ico", ".pdf", ".psd",
                                 ".zip", ".docx", ".docm", ".pptx", ".xlsx", ".jar", ".apk", ".epub",
                                 ".7z", ".txt", ".md", ".markdown", ".rtf", ".bin"}
        End Function

        ''' <summary>给人看的支持列表（带中文说明），只用于提示文本。</summary>
        Public Shared Function SupportedExtensionsDisplay() As String()
            Return SupportedExtensions().
                Select(Function(e) If(e = ".bin", ".bin（章文件）", e)).ToArray()
        End Function

    End Class

    ''' <summary>盖章参数（高级选项面板里的值最终落到这里）。</summary>
    Public NotInheritable Class SealOptions
        ''' <summary>Brotli 质量 0–11（默认 9）。</summary>
        Public Property BrotliQuality As Integer = Comp.DefaultBrotliQuality
        ''' <summary>压缩算法：None 表示记录不压缩（省 CPU，记录更大）。</summary>
        Public Property CompressionAlgorithm As Byte = Comp.MethodBrotli
        ''' <summary>压缩后反而更大时是否退回原文。</summary>
        Public Property SkipIfLarger As Boolean = True
        ''' <summary>小于该字节数就不压缩（Brotli 头本身就有几十字节）。</summary>
        Public Property MinSizeToCompress As Integer = 256
        ''' <summary>章字节上限（超过就拒绝盖章，避免把超大记录写进别人的文件）。</summary>
        Public Property SealBytesMax As Long = 64L * 1024 * 1024
        ''' <summary>章字节指纹（PBKDF2）迭代次数。</summary>
        Public Property Pbkdf2Iterations As Integer = StampFile.DefaultKdfIterations
        ''' <summary>载体写入选项（压缩包重打包口令等）。</summary>
        Public Property Carrier As New CarrierOptions()
        ''' <summary>
        ''' 若已有旧记录，是否先摘掉再盖。
        ''' 默认 <c>False</c>：再次盖章时保留原有记录、直接追加一条新记录——
        ''' 已盖章的文件就当普通文件处理，不再被「自动换新章」。
        ''' 后果是文件里可能有多条记录，验章以最后一条为准，摘章要摘到干净为止
        ''' （<see cref="SealService.RemoveAllSeals"/>）。
        ''' 若某载体只能存一份（ZIP 注释区、章保留区），写入本身就会覆盖，等价于替换。
        ''' </summary>
        Public Property ReplaceExisting As Boolean = False
        ''' <summary>视觉章：画到图片/PDF 的指定位置（会改写文件本身）。Nothing = 只写数据不贴图。</summary>
        Public Property VisualMark As Visual.VisualMark = Nothing
        ''' <summary>贴图前是否先备份一份（贴图会重新编码图片/重存 PDF，属破坏性操作）。</summary>
        Public Property BackupBeforeVisualMark As Boolean = True
        ''' <summary>备份文件后缀（默认 .bak，可在设置里改）。</summary>
        Public Property BackupSuffix As String = ".bak"
    End Class

    Public NotInheritable Class StampResult
        Public Property CarrierId As String = ""
        Public Property CarrierName As String = ""
        Public Property BlobLength As Integer
        Public Property ReplacedExisting As Boolean
        Public Property StampTime As DateTimeOffset
        Public Property StampIdShort As String = ""
        Public Property OriginalHash As Byte()
        Public Property RecordVersion As Byte = SealRecord.Version
        Public Property VisualBurned As Boolean = False
        ''' <summary>若贴图前做了备份，这里是备份文件路径。</summary>
        Public Property BackupPath As String = ""
    End Class

    Public Enum SealPartState
        Skipped = 0
        Ok = 1
        Failed = 2
        Warning = 3
    End Enum

    Public NotInheritable Class SealCheck
        Public Property Part As String = ""
        Public Property State As SealPartState = SealPartState.Skipped
        Public Property Message As String = ""
    End Class

    ''' <summary>验章结果。UI 按 State 上色，Title 里拿到一句话结论。</summary>
    Public NotInheritable Class SealVerifyResult

        Public Property HasRecord As Boolean = False
        Public Property StampProvided As Boolean = False
        Public Property CarrierId As String = ""
        Public Property CarrierName As String = ""
        Public Property RecordVersion As Byte
        Public Property StampTime As DateTimeOffset?
        Public Property VisualBurned As Boolean
        Public Property StampIdShort As String = ""
        Public Property StampLabel As String = ""
        Public Property StampDescription As String = ""
        Public Property SealBytesLength As Integer
        Public Property OriginalHashHex As String = ""
        Public Property BlobLength As Integer
        Public Property Checks As New List(Of SealCheck)

        ''' <summary>
        ''' 严格模式：任何一项校验失败都判「失败」；关闭则降级为「警告」——
        ''' 用于"只想知道有没有明显问题、不想被某一项小瑕疵卡住"的场景。
        ''' 降级在 Add 里统一处理，避免每个检查点各写一遍判断。
        ''' </summary>
        Public Property StrictMode As Boolean = True

        Public Function Add(part As String, state As SealPartState, message As String) As SealCheck
            Dim eff = state
            If Not StrictMode AndAlso eff = SealPartState.Failed Then eff = SealPartState.Warning
            Dim c As New SealCheck With {.Part = part, .State = eff, .Message = message}
            Checks.Add(c)
            Return c
        End Function

        Public ReadOnly Property AnyFailed As Boolean
            Get
                For Each c In Checks
                    If c.State = SealPartState.Failed Then Return True
                Next
                Return False
            End Get
        End Property

        Public ReadOnly Property FailureParts() As List(Of String)
            Get
                Dim l As New List(Of String)
                For Each c In Checks
                    If c.State = SealPartState.Failed Then l.Add(c.Part)
                Next
                Return l
            End Get
        End Property

        Public ReadOnly Property WarningParts() As List(Of String)
            Get
                Dim l As New List(Of String)
                For Each c In Checks
                    If c.State = SealPartState.Warning Then l.Add(c.Part)
                Next
                Return l
            End Get
        End Property

        ''' <summary>一句话结论。</summary>
        Public ReadOnly Property Title As String
            Get
                If Not HasRecord Then Return "未发现盖章记录"
                If AnyFailed Then
                    Dim names As New List(Of String)
                    For Each c In Checks
                        If c.State = SealPartState.Failed Then names.Add(c.Part)
                    Next
                    Return "验证失败：" & String.Join("、", names)
                End If
                If Not StampProvided Then Return "记录完整，但缺少原章，无法确认来源"
                Return "验证通过"
            End Get
        End Property

        Public ReadOnly Property Severity As String
            Get
                If Not HasRecord Then Return "none"
                If AnyFailed Then Return "fail"
                If Not StampProvided Then Return "limited"
                Return "ok"
            End Get
        End Property

        ''' <summary>
        ''' 生成报告。format: Text / Markdown / Json；showRawHex 为真时附完整哈希。
        ''' 三种格式由同一份数据渲染，字段一致，不会出现"界面有、导出没有"。
        ''' </summary>
        Public Function FormatReport(Optional format As String = "Text",
                                     Optional showRawHex As Boolean = False) As String
            Dim fmt = If(format, "Text").Trim().ToLowerInvariant()
            If fmt = "json" Then Return ReportJson(showRawHex)
            If fmt = "markdown" Then Return ReportMarkdown(showRawHex)
            Return ReportText(showRawHex)
        End Function

        Private Function ReportText(showRawHex As Boolean) As String
            Dim w As New Text.StringBuilder()
            w.AppendLine($"印章状态：{Title}")
            w.AppendLine($"载体：{CarrierName}（{CarrierId}）  记录长度：{BlobLength} 字节")
            If HasRecord Then
                w.AppendLine($"记录版本：v{RecordVersion}    盖章时间：{If(StampTime.HasValue, StampTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"), "—")}")
                w.AppendLine($"章指纹：{StampIdShort}")
                If StampProvided Then
                    w.AppendLine($"章名称：{StampLabel}")
                    If Not String.IsNullOrEmpty(StampDescription) Then w.AppendLine($"章描述：{StampDescription}")
                    w.AppendLine($"章字节：{SealBytesLength} 字节")
                End If
                If showRawHex AndAlso Not String.IsNullOrEmpty(OriginalHashHex) Then
                    w.AppendLine($"原文件 SHA3-512：{OriginalHashHex}")
                End If
            End If
            w.AppendLine()
            For Each c In Checks
                w.AppendLine($"{MarkText(c.State)} {c.Part}：{c.Message}")
            Next
            Return w.ToString()
        End Function

        Private Function ReportMarkdown(showRawHex As Boolean) As String
            Dim w As New Text.StringBuilder()
            w.AppendLine($"# SymbolPrint 验章报告 — {Title}")
            w.AppendLine()
            w.AppendLine($"- 载体：{CarrierName}（{CarrierId}）")
            w.AppendLine($"- 记录长度：{BlobLength} 字节")
            If HasRecord Then
                w.AppendLine($"- 记录版本：v{RecordVersion}")
                w.AppendLine($"- 盖章时间：{If(StampTime.HasValue, StampTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), "—")}")
                w.AppendLine($"- 章指纹：{StampIdShort}")
                If StampProvided Then w.AppendLine($"- 章名称：{StampLabel}")
                If showRawHex AndAlso Not String.IsNullOrEmpty(OriginalHashHex) Then
                    w.AppendLine($"- 原文件 SHA3-512：`{OriginalHashHex}`")
                End If
            End If
            w.AppendLine()
            w.AppendLine("| 结果 | 检查项 | 说明 |")
            w.AppendLine("| --- | --- | --- |")
            For Each c In Checks
                w.AppendLine($"| {MarkText(c.State)} | {c.Part} | {c.Message.Replace("|", "\|")} |")
            Next
            Return w.ToString()
        End Function

        Private Function ReportJson(showRawHex As Boolean) As String
            Dim w As New Text.StringBuilder()
            w.Append("{""title"":").Append(JsonStr(Title))
            w.Append(",""carrier"":").Append(JsonStr(CarrierName)).Append(",""carrierId"":").Append(JsonStr(CarrierId))
            w.Append(",""blobLength"":").Append(BlobLength)
            w.Append(",""hasRecord"":").Append(If(HasRecord, "true", "false"))
            If HasRecord Then
                w.Append(",""recordVersion"":").Append(RecordVersion)
                w.Append(",""stampTime"":").Append(If(StampTime.HasValue, JsonStr(StampTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")), "null"))
                w.Append(",""stampId"":").Append(JsonStr(StampIdShort))
                If showRawHex AndAlso Not String.IsNullOrEmpty(OriginalHashHex) Then
                    w.Append(",""originalHash"":").Append(JsonStr(OriginalHashHex))
                End If
            End If
            w.Append(",""checks"":[")
            For i = 0 To Checks.Count - 1
                Dim c = Checks(i)
                If i > 0 Then w.Append(",")
                w.Append("{""part"":").Append(JsonStr(c.Part))
                w.Append(",""state"":").Append(JsonStr(c.State.ToString()))
                w.Append(",""message"":").Append(JsonStr(c.Message)).Append("}")
            Next
            w.Append("]}")
            Return w.ToString()
        End Function

        Private Shared Function MarkText(st As SealPartState) As String
            Select Case st
                Case SealPartState.Ok : Return "[通过]"
                Case SealPartState.Failed : Return "[失败]"
                Case SealPartState.Warning : Return "[注意]"
                Case Else : Return "[跳过]"
            End Select
        End Function

        Private Shared Function JsonStr(v As String) As String
            If v Is Nothing Then Return "null"
            Dim sb As New Text.StringBuilder("""")
            For Each ch As Char In v
                Select Case ch
                    Case """"c
                        sb.Append("\""")
                    Case "\"c
                        sb.Append("\\")
                    Case ChrW(10)
                        sb.Append("\n")
                    Case ChrW(13)
                        sb.Append("\r")
                    Case ChrW(9)
                        sb.Append("\t")
                    Case Else
                        If AscW(ch) < 32 Then
                            sb.Append("\u").Append(AscW(ch).ToString("x4"))
                        Else
                            sb.Append(ch)
                        End If
                End Select
            Next
            sb.Append("""")
            Return sb.ToString()
        End Function

    End Class

    ''' <summary>
    ''' 盖章 / 验章编排。所有耗时环节都通过 IProgressSink 上报（带取消）。
    ''' 铁律：写载体时只追加/只插入，绝不改动文件里已有的字节。
    ''' </summary>
    Public NotInheritable Class SealService

        ' ==================== 盖章 ====================

        Public Shared Function Stamp(filePath As String, seal As StampFile,
                                     Optional options As SealOptions = Nothing,
                                     Optional sink As IProgressSink = Nothing) As StampResult
            If String.IsNullOrEmpty(filePath) Then Throw New ArgumentNullException(NameOf(filePath))
            If Not File.Exists(filePath) Then Throw New FileNotFoundException("要盖章的文件不存在。", filePath)
            If seal Is Nothing Then Throw New ArgumentNullException(NameOf(seal))
            If options Is Nothing Then options = New SealOptions()

            Dim carrier = CarrierRegistry.Find(filePath)
            If carrier Is Nothing Then
                Throw New NotSupportedException(
                    $"暂不支持该文件类型（{FileSniffer.Describe(filePath)}）。当前支持：{String.Join("、", CarrierRegistry.SupportedExtensionsDisplay())}")
            End If

            Dim priv = seal.GetPrivateKey() ' 受口令保护的章未解锁时这里会抛出明确错误

            Dim res As New StampResult()
            res.CarrierId = carrier.Id
            res.CarrierName = carrier.DisplayName
            res.StampIdShort = seal.StampIdShort

            Using scope As New ProgressScope(sink, 0.0, 1.0, "盖章")

                ' 1) 先摘掉旧记录（如有），保证接下来哈希到的是"原文件"
                If options.ReplaceExisting Then
                    res.ReplacedExisting = carrier.RemoveBlob(filePath)
                    If res.ReplacedExisting Then options.Carrier?.Log?.Invoke("已摘除本文件原有的 SymbolPrint 记录。")
                End If

                ' 1.5) 视觉贴图 —— 会改写文件本身，必须在算"原文件哈希"之前做，
                '      这样记录里的哈希指的是"已经带图案的那份文件"，语义才自洽。
                If options.VisualMark IsNot Nothing Then
                    If options.BackupBeforeVisualMark Then
                        Dim bak = filePath & If(String.IsNullOrEmpty(options.BackupSuffix), ".bak", options.BackupSuffix)
                        Try
                            File.Copy(filePath, bak, True)
                            res.BackupPath = bak
                            options.Carrier?.Log?.Invoke("贴图前已备份原文件：" & bak)
                        Catch ex As Exception
                            options.Carrier?.Log?.Invoke("备份失败（继续贴图）：" & ex.Message)
                        End Try
                    End If
                    Dim vm = Visual.VisualMarkApplier.Apply(filePath, options.VisualMark, sink, options.Carrier?.Log)
                    res.VisualBurned = vm.Applied
                    If Not vm.Applied AndAlso Not String.IsNullOrEmpty(vm.Reason) Then
                        options.Carrier?.Log?.Invoke("未贴图：" & vm.Reason)
                    End If
                End If

                ' 2) 原文件 SHA3-512
                Dim origHash = Sha3Kdf.HashFile(filePath, scope.Slice(0.0, 0.45, "计算原文件 SHA3-512"))
                res.OriginalHash = origHash
                ProgressExt.ThrowIfCancelled(sink)

                ' 3) 章字节指纹（PBKDF2）
                Dim sealBytes = If(seal.SealRawBytes, Array.Empty(Of Byte)())
            If sealBytes.Length > options.SealBytesMax Then
                Throw New InvalidOperationException(
                    $"章字节 {Comp.HumanSize(sealBytes.Length)} 超过上限 {Comp.HumanSize(options.SealBytesMax)}" &
                    "（可在「高级设置 → 压缩 → 章字节上限」调整）。")
            End If
                Dim pbSalt = Bin.RandomBytes(16)
                Dim pb = Sha3Kdf.Pbkdf2(sealBytes, pbSalt, options.Pbkdf2Iterations, SealRecord.Pbkdf2Len,
                                        scope.Slice(0.45, 0.65, "派生章字节指纹"))

                ' 4) 加密节2 明文
                Dim s2 As New ByteWriter()
                s2.WriteU8(1)
                s2.WriteU32(CUInt(sealBytes.Length))
                s2.WriteBytes(sealBytes)
                s2.WriteBytes(origHash)
                s2.WriteBytes(pb)
                s2.WriteBytes(pbSalt)
                Dim s2Plain = s2.ToArray()

                ' 5) ECIES 加密（用章的公钥）
                Dim s2Cipher = Ecies.Encrypt(seal.Keys.PublicSpki, s2Plain, seal.Keys.CurveId)

                ' 6) 加密节1 明文
                Dim ephPoint = EciesPointOf(s2Cipher)
                Dim sid = seal.StampId
                Dim sid8(7) As Byte
                Buffer.BlockCopy(sid, 0, sid8, 0, 8)

                Dim s1 As New ByteWriter()
                s1.WriteU8(1)
                s1.WriteU32(CUInt(options.Pbkdf2Iterations))
                s1.WriteBytes(pbSalt)
                s1.WriteBytes(pb)
                s1.WriteU32(CUInt(s2Cipher.Length))
                s1.WriteBytes(s2Cipher)
                s1.WriteU16(CUShort(ephPoint.Length))
                s1.WriteBytes(ephPoint)
                s1.WriteBytes(sid8)
                Dim s1Plain = s1.ToArray()

                ' 7) 加密节1 + 记录头
                Dim key1 = AesGcmUtil.NewKey()
                Dim nonce1 = AesGcmUtil.NewNonce()
                Dim enc = AesGcmUtil.Encrypt(key1, nonce1, s1Plain)

                Dim rec As New SealRecord()
                rec.StampTime = DateTimeOffset.Now
                rec.VisualBurned = res.VisualBurned
                rec.Key1 = key1
                rec.Nonce1 = nonce1
                rec.Tag1 = enc.Tag
                rec.Section1Cipher = enc.Cipher
                rec.SealOwnHash()
                Dim raw = rec.Serialize()

                res.StampTime = rec.StampTime
                res.RecordVersion = SealRecord.Version

                ' 8) 压缩 + 信封
                Dim payload = SealedPayload.Build(raw, options.BrotliQuality,
                                                  scope.Slice(0.65, 0.85, "压缩盖章记录"),
                                                  "", options.CompressionAlgorithm, options.SkipIfLarger,
                                                  options.MinSizeToCompress)
                Dim blob = SealEnvelope.Build(payload, SealRecord.Version)
                res.BlobLength = blob.Length

                ProgressExt.ThrowIfCancelled(sink)

                ' 9) 容量检查 + 写入
                Dim cap = carrier.Capacity(filePath)
                If cap < 0 Then
                    Throw New InvalidDataException(
                        $"目标文件结构无效（找不到归档结束标记），无法安全写入。载体：{carrier.DisplayName}")
                End If
                If blob.Length > cap Then
                    Throw New InvalidOperationException(
                        $"该文件的空白区容量不足：本次记录需要 {blob.Length} 字节，可用 {cap} 字节。" &
                        "（ZIP 注释区上限 65535 字节；可考虑调低章字节体积或改用其它载体。）")
                End If

                carrier.WriteBlob(filePath, blob, options.Carrier)
                ProgressExt.Push(sink, "盖章", 1.0, $"{Comp.HumanSize(blob.Length)} 已写入")
            End Using

            Return res
        End Function

        ''' <summary>摘章（恢复成未盖章状态；只精确摘掉我们那一段）。</summary>
        Public Shared Function RemoveSeal(filePath As String,
                                          Optional sink As Util.IProgressSink = Nothing) As Boolean
            Dim carrier = CarrierRegistry.Find(filePath)
            If carrier Is Nothing Then
                Throw New NotSupportedException(
                    $"暂不支持该文件类型（{FileSniffer.Describe(filePath)}）。")
            End If
            Return carrier.RemoveBlob(filePath)
        End Function

        ''' <summary>
        ''' 把文件上所有本程序的记录都摘掉，恢复成"从未盖过章"。
        ''' 因为盖章默认保留旧记录，同一文件可能叠了多条；只摘一条会留下残章。
        ''' 返回实际摘除的条数；0 表示本来就没有。
        ''' </summary>
        Public Shared Function RemoveAllSeals(filePath As String,
                                              Optional sink As Util.IProgressSink = Nothing) As Integer
            Dim n = 0
            ' 上限只是防呆：正常情况一两个就到底了
            For i = 1 To 32
                ProgressExt.ThrowIfCancelled(sink)
                If Not RemoveSeal(filePath, sink) Then Exit For
                n += 1
            Next
            Return n
        End Function

        ' ==================== 验章 ====================

        ''' <summary>
        ''' 验章。stamp 为 Nothing 时是"无章验证"，只能证明记录本身没被改，不能证明来源；
        ''' 结果里会明确标注这一点。
        ''' </summary>
        Public Shared Function Verify(filePath As String, Optional stamp As StampFile = Nothing,
                                      Optional sink As IProgressSink = Nothing,
                                      Optional options As VerifyOptions = Nothing) As SealVerifyResult
            If String.IsNullOrEmpty(filePath) Then Throw New ArgumentNullException(NameOf(filePath))
            If Not File.Exists(filePath) Then Throw New FileNotFoundException("要验证的文件不存在。", filePath)
            If options Is Nothing Then options = New VerifyOptions()

            Dim res As New SealVerifyResult()
            res.StrictMode = options.StrictMode
            res.StampProvided = stamp IsNot Nothing
            If stamp IsNot Nothing Then
                res.StampLabel = If(stamp.Label, "")
                res.StampDescription = If(stamp.Description, "")
                res.StampIdShort = stamp.StampIdShort
            End If

            Dim carrier = CarrierRegistry.Find(filePath)
            If carrier Is Nothing Then
                res.CarrierId = "unknown"
                res.CarrierName = "（未知）"
                res.Add("文件类型", SealPartState.Failed,
                        $"暂不支持该文件类型（{Path.GetExtension(filePath)}），无法判断它是否有 SymbolPrint 记录。")
                Return res
            End If
            res.CarrierId = carrier.Id
            res.CarrierName = carrier.DisplayName

            Using scope As New ProgressScope(sink, 0.0, 1.0, "验章")

                Dim blob = carrier.ReadBlob(filePath)
                If blob Is Nothing Then
                    res.HasRecord = False
                    res.Add("盖章记录", SealPartState.Skipped, "该文件的空白区里没有 SymbolPrint 盖章记录。")
                    If options.WarnIfNoSeal Then
                        res.Add("局限性说明", SealPartState.Warning,
                                "没有盖章记录时，无法证明这个文件的来源，也无法证明它没有被改动过（任何人都可以换成任意内容）。" &
                                "如果确实应该带章，请确认文件是否被其它工具重新保存过（有些工具会丢掉文件尾部/注释区数据）。")
                    End If
                    Return res
                End If

                res.HasRecord = True
                res.BlobLength = blob.Length

                ' 1) 信封结构
                Dim footer = SealEnvelope.TryParseFooter(blob, blob.Length - SealEnvelope.FooterSize)
                If footer Is Nothing Then
                    res.Add("信封结构", SealPartState.Failed, "文件尾部数据不符合 SymbolPrint 信封格式（尾部标记损坏或记录被截断）。")
                    Return res
                End If
                res.RecordVersion = footer.SealVersion
                If footer.TotalLength <> blob.Length Then
                    res.Add("信封结构", SealPartState.Warning,
                            $"信封声明的总长（{footer.TotalLength}）与实读长度（{blob.Length}）不一致，文件可能被追加过其它数据。")
                Else
                    res.Add("信封结构", SealPartState.Ok, $"{blob.Length} 字节，尾部标记与长度字段自洽。")
                End If

                ' 2) 载荷哈希
                Dim payloadLen = blob.Length - SealEnvelope.FooterSize
                Dim payload(payloadLen - 1) As Byte
                Buffer.BlockCopy(blob, 0, payload, 0, payloadLen)
                If SealEnvelope.VerifyPayloadHash(payload, footer.PayloadHash8) Then
                    res.Add("载荷完整性", SealPartState.Ok, "SHA3-512 前 8 字节与 footer 记录一致。")
                Else
                    res.Add("载荷完整性", SealPartState.Failed, "载荷 SHA3-512 与 footer 记录不一致，记录内容已被改动。")
                End If

                ' 3) 解压 + 解析记录
                Dim rec As SealRecord
                Try
                    Dim sp = SealedPayload.Parse(payload)
                    Dim raw = sp.Unpack(scope.Slice(0.05, 0.25, "解压盖章记录"))
                    rec = SealRecord.Parse(raw)
                Catch ex As Exception
                    res.Add("记录解析", SealPartState.Failed, "无法解出盖章记录：" & ex.Message)
                    Return res
                End Try

                res.StampTime = rec.StampTime
                res.VisualBurned = rec.VisualBurned
                res.Add("记录解析", SealPartState.Ok, $"记录版本 v{SealRecord.Version}，盖章时间 {rec.StampTime.ToLocalTime():yyyy-MM-dd HH:mm:ss}。")

                ' 4) 记录自身哈希
                If Not options.CheckOwnHash Then
                    res.Add("记录自校验", SealPartState.Skipped, "已按设置跳过（高级设置 → 验证 → 校验记录自身哈希）。")
                ElseIf rec.VerifyOwnHash() Then
                    res.Add("记录自校验", SealPartState.Ok, "记录头、密钥、密文的 SHA3-512 与内嵌摘要一致。")
                Else
                    res.Add("记录自校验", SealPartState.Failed, "记录内嵌的 SHA3-512 与实际内容不符，记录已被篡改。")
                    Return res
                End If

                ' 5) 解密加密节1（key1 就明放在记录里，任何人都能解 —— 这里解出来的东西本身不需要保密，
                '    真正的机密在加密节2，需要用章的私钥才能解开）
                Dim s1Plain As Byte()
                Try
                    s1Plain = AesGcmUtil.Decrypt(rec.Key1, rec.Nonce1, rec.Section1Cipher, rec.Tag1)
                Catch ex As Exception
                    res.Add("加密节1", SealPartState.Failed, "AES-GCM 认证失败，记录密文被改动或损坏：" & ex.Message)
                    Return res
                End Try

                Dim s1r As New ByteReader(s1Plain)
                s1r.ReadU8()
                Dim kdfIter = CInt(s1r.ReadU32())
                Dim pbSalt = s1r.ReadBytes(16)
                Dim pb1 = s1r.ReadBytes(SealRecord.Pbkdf2Len)
                Dim s2Len = CInt(s1r.ReadU32())
                Dim s2Cipher = s1r.ReadBytes(s2Len)
                Dim ephLen = CInt(s1r.ReadU16())
                Dim ephPoint = s1r.ReadBytes(ephLen)
                Dim sid8 = s1r.ReadBytes(8)
                res.StampIdShort = Bin.Hex(sid8).ToUpperInvariant()
                res.Add("加密节1", SealPartState.Ok,
                        $"已解密（KDF {kdfIter} 次，ECIES 临时公钥 {ephLen} 字节）。")

                If stamp Is Nothing Then
                    res.Add("章身份匹配", SealPartState.Skipped, $"记录声明的章指纹为 {res.StampIdShort}，但未提供原章，无法核对。")
                    res.Add("加密节2", SealPartState.Skipped, "加密节2 由章的公钥加密，必须提供原章（私钥）才能解开。")
                    res.Add("原文件哈希", SealPartState.Skipped, "无法取出记录里的原文件摘要，因此不能证明原文件未被改动。")
                    res.Add("局限性说明", SealPartState.Warning,
                            "无章验证只能证明「这段盖章记录本身没有被改动」，不能证明「文件是由谁盖的章」。" &
                            "要确认来源，请提供当初生成的那个章文件（.bin）。")
                    Return res
                End If

                ' 6) 章身份
                Dim mineId = stamp.StampId
                Dim mine8(7) As Byte
                Buffer.BlockCopy(mineId, 0, mine8, 0, 8)
                If Not options.CheckStampId Then
                    res.Add("章身份匹配", SealPartState.Skipped, "已按设置跳过（高级设置 → 验证 → 校验章身份）。")
                ElseIf Bin.Same(sid8, mine8) Then
                    res.Add("章身份匹配", SealPartState.Ok, $"记录声明的章指纹与提供的章一致（{res.StampIdShort}）。")
                Else
                    res.Add("章身份匹配", SealPartState.Failed,
                            $"记录声明的章指纹（{Bin.Hex(sid8).ToUpperInvariant()}）与提供的章（{Bin.Hex(mine8).ToUpperInvariant()}）不同 —— 这个章不是当初盖章用的那个。")
                End If

                ' 7) 解密加密节2
                Dim s2Plain As Byte()
                Try
                    s2Plain = Ecies.Decrypt(stamp.GetPrivateKey(), s2Cipher)
                Catch ex As Exception
                    res.Add("加密节2", SealPartState.Failed, "无法用该章的私钥解开加密节2：" & ex.Message)
                    Return res
                End Try

                Dim s2r As New ByteReader(s2Plain)
                s2r.ReadU8()
                Dim sealLen = CInt(s2r.ReadU32())
                Dim sealBytes = s2r.ReadBytes(sealLen)
                Dim origHash = s2r.ReadBytes(64)
                Dim pb2 = s2r.ReadBytes(SealRecord.Pbkdf2Len)
                Dim pbSalt2 = s2r.ReadBytes(16)
                res.SealBytesLength = sealLen
                res.OriginalHashHex = Bin.Hex(origHash).ToUpperInvariant()
                res.Add("加密节2", SealPartState.Ok, $"已用章私钥解密，取出章字节 {sealLen} 字节。")

                ' 8) 章字节指纹
                Dim pbRecalc = Sha3Kdf.Pbkdf2(sealBytes, pbSalt2, kdfIter, SealRecord.Pbkdf2Len,
                                              scope.Slice(0.35, 0.55, "重算章字节指纹"))
                If Bin.Same(pb1, pb2) AndAlso Bin.Same(pb2, pbRecalc) Then
                    res.Add("章字节指纹", SealPartState.Ok, "两处 PBKDF2 记录一致，且与章字节重算结果相同。")
                Else
                    res.Add("章字节指纹", SealPartState.Failed, "章字节的 PBKDF2 指纹对不上：加密节1、加密节2 与章字节三者不一致。")
                End If

                ' 9) 原文件哈希
                If Not options.CheckOriginalHash Then
                    res.Add("原文件哈希", SealPartState.Skipped, "已按设置跳过（高级设置 → 验证 → 校验原文件摘要）。")
                    ProgressExt.Push(sink, "验章", 1.0)
                    Return res
                End If
                Dim origNow As Byte()
                If TypeOf carrier Is IOriginalHashAware Then
                    origNow = DirectCast(carrier, IOriginalHashAware).HashOriginal(
                        filePath, blob.Length, scope.Slice(0.55, 0.95, "还原并重算原文件 SHA3-512"))
                Else
                    Dim limit As Long = New FileInfo(filePath).Length - blob.Length
                    If limit < 0 Then limit = 0
                    origNow = Hashing.HashPrefixWithPatches(filePath, limit, Nothing,
                                                            scope.Slice(0.55, 0.95, "重算原文件 SHA3-512"))
                End If

                If Bin.Same(origHash, origNow) Then
                    res.Add("原文件哈希", SealPartState.Ok, "还原后的原文件 SHA3-512 与盖章时记录的一致 —— 原文件内容未被改动。")
                Else
                    res.Add("原文件哈希", SealPartState.Failed,
                            "原文件内容与盖章时不一致：盖章时 " & Bin.Hex(origHash).Substring(0, 16).ToUpperInvariant() &
                            "…，现在 " & Bin.Hex(origNow).Substring(0, 16).ToUpperInvariant() & "…")
                End If

                ProgressExt.Push(sink, "验章", 1.0)
            End Using

            Return res
        End Function

        ''' <summary>从 ECIES 信封里取出临时公钥点（不解密就能读）。</summary>
        Private Shared Function EciesPointOf(envelope As Byte()) As Byte()
            Dim r As New ByteReader(envelope)
            r.ReadU8()
            r.ReadU8()
            Dim plen = CInt(r.ReadU8())
            Return r.ReadBytes(plen)
        End Function

    End Class

End Namespace
