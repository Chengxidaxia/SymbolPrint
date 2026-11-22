Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.IO.Compression
Imports SymbolPrint.Core.Carriers
Imports SymbolPrint.Core.Crypto
Imports SymbolPrint.Core.Model
Imports SymbolPrint.Core.Service
Imports SymbolPrint.Core.Visual
Imports SymbolPrint.Core.Util

Namespace SymbolPrint.Cli

    ''' <summary>盖章相关命令：make / stamp / verify / unseal / e2etest。</summary>
    Public Module SealCommand

        Public Function Run(args As String()) As Integer
            Select Case args(0).ToLowerInvariant()
                Case "make" : Return CmdMake(args)
                Case "stamp" : Return CmdStamp(args)
                Case "verify" : Return CmdVerify(args)
                Case "unseal" : Return CmdUnseal(args)
                Case "svg2png" : Return CmdSvg2Png(args)
                Case "e2etest" : Return CmdE2E()
                Case Else : Return 2
            End Select
        End Function

        ' ==================== 参数解析 ====================

        Private Function Opt(args As String(), name As String) As String
            For i As Integer = 0 To args.Length - 2
                If String.Equals(args(i), name, StringComparison.OrdinalIgnoreCase) Then Return args(i + 1)
            Next
            Return Nothing
        End Function

        Private Function OptInt(args As String(), name As String, fallback As Integer) As Integer
            Dim v = Opt(args, name)
            Dim n As Integer
            If v IsNot Nothing AndAlso Integer.TryParse(v, n) Then Return n
            Return fallback
        End Function

        Private Function OptDbl(args As String(), name As String, fallback As Double) As Double
            Dim v = Opt(args, name)
            Dim d As Double
            If v IsNot Nothing AndAlso Double.TryParse(v, Globalization.NumberStyles.Float,
                                                       Globalization.CultureInfo.InvariantCulture, d) Then Return d
            Return fallback
        End Function

        Private Function Positional(args As String()) As List(Of String)
            Dim l As New List(Of String)
            Dim skipNext = False
            For i As Integer = 1 To args.Length - 1
                If skipNext Then
                    skipNext = False
                    Continue For
                End If
                If args(i).StartsWith("--") Then
                    skipNext = True
                    Continue For
                End If
                l.Add(args(i))
            Next
            Return l
        End Function

        ' ==================== make：做章 ====================

        Private Function CmdMake(args As String()) As Integer
            Dim pos = Positional(args)
            If pos.Count < 1 Then
                Console.WriteLine("用法: spcli make <输出.bin> [--label 名称] [--desc 描述] [--seal-bytes 文件|hex:xx..] [--svg 图案.svg] [--pass 口令]")
                Return 2
            End If
            Dim outPath = pos(0)
            Dim seal As StampFile

            ' 已有同路径章文件时保留其保留区（自指数据）
            If File.Exists(outPath) AndAlso StampFile.HasMagic(outPath) Then
                seal = StampFile.Load(outPath)
            Else
                seal = New StampFile()
            End If

            seal.Label = If(Opt(args, "--label"), "未命名章")
            seal.Description = If(Opt(args, "--desc"), "")
            seal.CreatedAt = DateTimeOffset.Now

            Dim sb = Opt(args, "--seal-bytes")
            If sb IsNot Nothing Then
                If sb.StartsWith("hex:", StringComparison.OrdinalIgnoreCase) Then
                    seal.SealRawBytes = Bin.FromHex(sb.Substring(4))
                ElseIf File.Exists(sb) Then
                    seal.SealRawBytes = File.ReadAllBytes(sb)
                Else
                    seal.SealRawBytes = Bin.Utf8Bytes(sb)
                End If
            End If

            Dim pw = Opt(args, "--pass")
            If Not String.IsNullOrEmpty(pw) Then seal.SetPassphrase(pw)

            Dim svgFile = Opt(args, "--svg")
            If svgFile IsNot Nothing Then
                If Not File.Exists(svgFile) Then
                    Console.WriteLine("SVG 文件不存在：" & svgFile)
                    Return 2
                End If
                seal.SvgMarkup = File.ReadAllText(svgFile, Text.Encoding.UTF8)
            End If

            seal.Save(outPath)

            Console.WriteLine($"已生成章文件：{outPath}")
            Console.WriteLine($"  章名称：{seal.Label}")
            Console.WriteLine($"  章指纹：{seal.StampIdShort}")
            Console.WriteLine($"  曲线  ：{Ecies.CurveNameOf(seal.Keys.CurveId)}")
            Console.WriteLine($"  章字节：{If(seal.SealRawBytes Is Nothing, 0, seal.SealRawBytes.Length)} 字节")
            Console.WriteLine($"  私钥  ：{If(seal.PassphraseProtected, "口令保护", "明文（持有 .bin 即可盖章，请妥善保管）")}")
            Console.WriteLine($"  文件长：{New FileInfo(outPath).Length} 字节（含 {StampFile.ReservedSize} 字节自指保留区）")
            Return 0
        End Function

        ' ==================== stamp：盖章 ====================

        Private Function CmdStamp(args As String()) As Integer
            Dim pos = Positional(args)
            Dim sealPath = Opt(args, "--seal")
            If pos.Count < 1 OrElse sealPath Is Nothing Then
                Console.WriteLine("用法: spcli stamp <文件> --seal <章.bin> [--pass 口令] [--quality 0-11] [--kdf 迭代数]")
                Return 2
            End If
            Dim target = pos(0)

            Using ui As New ConsoleProgress()
                Dim seal = LoadSeal(sealPath, Opt(args, "--pass"))
                Dim sealOpt As New SealOptions()
                sealOpt.BrotliQuality = Comp.ClampQuality(OptInt(args, "--quality", Comp.DefaultBrotliQuality))
                sealOpt.Pbkdf2Iterations = OptInt(args, "--kdf", StampFile.DefaultKdfIterations)
                sealOpt.Carrier.Log = Sub(msg) Console.WriteLine("  " & msg)

                ' 视觉章：给 SVG 或现成 PNG，按相对坐标贴到图片/PDF 上
                Dim markSvg = Opt(args, "--mark-svg")
                Dim markPng = Opt(args, "--mark-png")
                If markSvg IsNot Nothing OrElse markPng IsNot Nothing Then
                    Dim vm As New VisualMark()
                    If markPng IsNot Nothing Then
                        vm.PngBytes = File.ReadAllBytes(markPng)
                    Else
                        vm.SvgMarkup = File.ReadAllText(markSvg, Text.Encoding.UTF8)
                    End If
                    vm.XRatio = OptDbl(args, "--mark-x", vm.XRatio)
                    vm.YRatio = OptDbl(args, "--mark-y", vm.YRatio)
                    vm.WidthRatio = OptDbl(args, "--mark-w", vm.WidthRatio)
                    vm.Opacity = OptDbl(args, "--mark-opacity", vm.Opacity)
                    vm.RotationDegrees = OptDbl(args, "--mark-rotate", vm.RotationDegrees)
                    vm.PdfPage = OptInt(args, "--mark-page", vm.PdfPage)
                    sealOpt.VisualMark = vm
                    sealOpt.BackupBeforeVisualMark = (Opt(args, "--no-backup") Is Nothing)
                End If

                Dim r = SealService.Stamp(target, seal, sealOpt, ui.Sink)
                Console.WriteLine()
                Console.WriteLine($"盖章成功：{Path.GetFileName(target)}")
                Console.WriteLine($"  载体  ：{r.CarrierName}（{r.CarrierId}）")
                Console.WriteLine($"  记录  ：{r.BlobLength} 字节（压缩后）")
                Console.WriteLine($"  章指纹：{r.StampIdShort}")
                Console.WriteLine($"  原文件：{Bin.Hex(r.OriginalHash).Substring(0, 32).ToUpperInvariant()}…")
                If r.VisualBurned Then Console.WriteLine("  视觉章：已绘制到文件上")
                If Not String.IsNullOrEmpty(r.BackupPath) Then Console.WriteLine($"  备份  ：{r.BackupPath}")
                If r.ReplacedExisting Then Console.WriteLine("  （已摘掉本文件里原有的 SymbolPrint 记录）")
            End Using
            Return 0
        End Function

        ' ==================== verify：验章 ====================

        Private Function CmdVerify(args As String()) As Integer
            Dim pos = Positional(args)
            If pos.Count < 1 Then
                Console.WriteLine("用法: spcli verify <文件> [--seal <章.bin>] [--pass 口令]")
                Return 2
            End If
            Dim target = pos(0)
            Dim sealPath = Opt(args, "--seal")

            Using ui As New ConsoleProgress()
                Dim seal As StampFile = Nothing
                If sealPath IsNot Nothing Then seal = LoadSeal(sealPath, Opt(args, "--pass"))
                Dim r = SealService.Verify(target, seal, ui.Sink)
                Console.WriteLine()
                PrintResultColorized(r)
                Return If(r.Severity = "fail", 1, 0)
            End Using
        End Function

        Private Sub PrintResultColorized(r As SealVerifyResult)
            Dim old = Console.ForegroundColor
            Select Case r.Severity
                Case "ok" : Console.ForegroundColor = ConsoleColor.Green
                Case "fail" : Console.ForegroundColor = ConsoleColor.Red
                Case "limited" : Console.ForegroundColor = ConsoleColor.Yellow
                Case Else : Console.ForegroundColor = ConsoleColor.Gray
            End Select
            Console.WriteLine($"■ {r.Title}")
            Console.ForegroundColor = old

            Console.WriteLine($"  载体：{r.CarrierName}（{r.CarrierId}）  记录长度：{r.BlobLength} 字节")
            If r.HasRecord Then
                Console.WriteLine($"  记录版本：v{r.RecordVersion}    盖章时间：{If(r.StampTime.HasValue, r.StampTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), "—")}")
                Console.WriteLine($"  章指纹：{r.StampIdShort}")
                If r.StampProvided Then
                    Console.WriteLine($"  章名称：{r.StampLabel}")
                    If Not String.IsNullOrEmpty(r.StampDescription) Then Console.WriteLine($"  章描述：{r.StampDescription}")
                    Console.WriteLine($"  章字节：{r.SealBytesLength} 字节")
                End If
            End If
            Console.WriteLine()
            For Each c In r.Checks
                Select Case c.State
                    Case SealPartState.Ok : Console.ForegroundColor = ConsoleColor.DarkGreen
                    Case SealPartState.Failed : Console.ForegroundColor = ConsoleColor.Red
                    Case SealPartState.Warning : Console.ForegroundColor = ConsoleColor.Yellow
                    Case Else : Console.ForegroundColor = ConsoleColor.DarkGray
                End Select
                Dim mark As String
                Select Case c.State
                    Case SealPartState.Ok : mark = "√ 通过"
                    Case SealPartState.Failed : mark = "× 失败"
                    Case SealPartState.Warning : mark = "! 注意"
                    Case Else : mark = "- 跳过"
                End Select
                Console.WriteLine($"{mark}  {c.Part}：{c.Message}")
            Next
            Console.ForegroundColor = old
        End Sub

        ' ==================== unseal：摘章 ====================

        Private Function CmdUnseal(args As String()) As Integer
            Dim pos = Positional(args)
            If pos.Count < 1 Then
                Console.WriteLine("用法: spcli unseal <文件> [--all]")
                Console.WriteLine("  --all  摘到干净为止（同一文件盖过多次时会叠多条记录）")
                Return 2
            End If
            If Array.IndexOf(args, "--all") >= 0 Then
                Dim n = SealService.RemoveAllSeals(pos(0))
                Console.WriteLine(If(n > 0,
                                    $"已摘除 {n} 条盖章记录，文件恢复成未盖章状态。",
                                    "该文件里没有可摘除的 SymbolPrint 记录。"))
                Return If(n > 0, 0, 1)
            End If
            Dim ok = SealService.RemoveSeal(pos(0))
            Console.WriteLine(If(ok, "已摘除盖章记录（其余字节未改动）。", "该文件里没有可摘除的 SymbolPrint 记录。"))
            Return If(ok, 0, 1)
        End Function

        Private Function LoadSeal(sealPath As String, password As String) As StampFile
            Dim seal = StampFile.Load(sealPath, password)
            If seal.IsLocked Then
                Throw New InvalidOperationException($"章文件「{Path.GetFileName(sealPath)}」的私钥受口令保护，请用 --pass 提供口令。")
            End If
            Return seal
        End Function

        ' ==================== svg2png：SVG 光栅化自检 ====================

        Private Function CmdSvg2Png(args As String()) As Integer
            Dim pos = Positional(args)
            If pos.Count < 2 Then
                Console.WriteLine("用法: spcli svg2png <输入.svg> <输出.png> [--width 512]")
                Return 2
            End If
            Dim w = OptInt(args, "--width", 512)
            Dim svgText = File.ReadAllText(pos(0), Text.Encoding.UTF8)
            Dim png = VisualMarkApplier.RenderSvgToPng(svgText, w)
            File.WriteAllBytes(pos(1), png)
            Console.WriteLine($"已渲染：{pos(1)}（{png.Length} 字节，宽 {w}px）")
            Return 0
        End Function

        ' ==================== e2etest：全格式端到端 ====================

        Private Function CmdE2E() As Integer
            Dim dir = Path.Combine(Path.GetTempPath(), "symbolprint-e2e-" & Guid.NewGuid().ToString("N").Substring(0, 8))
            Directory.CreateDirectory(dir)
            Dim failures As New List(Of String)

            Try
                Dim sealPath = Path.Combine(dir, "seal.bin")
                Console.WriteLine("== 1) 做章 ==")
                CmdMake(New String() {"make", sealPath, "--label", "测试章", "--desc", "SymbolPrint 端到端自测",
                                      "--seal-bytes", "symbolprint-e2e-payload"})
                Console.WriteLine()

                ' 真实文件样本
                Dim samples As New List(Of String)
                samples.Add(WritePng(Path.Combine(dir, "sample.png")))
                samples.Add(WritePdf(Path.Combine(dir, "sample.pdf")))
                samples.Add(WriteText(Path.Combine(dir, "sample.txt")))
                samples.Add(WriteText(Path.Combine(dir, "sample.md")))
                samples.Add(WriteRtf(Path.Combine(dir, "sample.rtf")))
                samples.Add(WriteZip(Path.Combine(dir, "sample.zip"), False))
                samples.Add(WriteZip(Path.Combine(dir, "sample.docx"), True))
                samples.Add(sealPath) ' 自指：章给自己盖章

                Console.WriteLine("== 2) 逐格式盖章 + 验章 ==")
                For Each f In samples
                    Dim name = Path.GetFileName(f)
                    Dim carrier = CarrierRegistry.Find(f)
                    Dim before = New FileInfo(f).Length
                    Dim originalBytes = File.ReadAllBytes(f)

                    If carrier Is Nothing Then
                        failures.Add($"{name}：没有可用载体")
                        Console.WriteLine($"  [失败] {name}：没有可用载体")
                        Continue For
                    End If

                    Try
                        Using ui As New ConsoleProgress()
                            SealService.Stamp(f, StampFile.Load(sealPath), New SealOptions(), ui.Sink)
                        End Using
                        Dim after = New FileInfo(f).Length

                        Dim r = SealService.Verify(f, StampFile.Load(sealPath), NullProgress.Instance)
                        Dim okVerify = (r.Severity = "ok")

                        ' 无章验证
                        Dim r2 = SealService.Verify(f, Nothing, NullProgress.Instance)
                        Dim limitedOk = (r2.Severity = "limited")

                        ' 篡改检测：改一个字节后必须失败
                        Dim tampered = Path.Combine(dir, "tamper-" & name)
                        File.Copy(f, tampered, True)
                        TamperMiddleByte(tampered)
                        Dim r3 = SealService.Verify(tampered, StampFile.Load(sealPath), NullProgress.Instance)
                        Dim tamperCaught = (r3.Severity = "fail")

                        Dim tag = If(okVerify AndAlso limitedOk AndAlso tamperCaught, "[通过]", "[失败]")
                        Console.WriteLine($"  {tag} {name,-14} 载体={carrier.Id,-14} +{after - before,5}B  有章={r.Title}  无章={r2.Title}  篡改={r3.Title}")

                        If Not okVerify Then failures.Add($"{name}：有章验证未通过（{r.Title}）")
                        If Not limitedOk Then failures.Add($"{name}：无章验证结论异常（{r2.Title}）")
                        If Not tamperCaught Then failures.Add($"{name}：篡改后仍然通过（漏检）")
                    Catch ex As Exception
                        failures.Add($"{name}：{ex.Message}")
                        Console.WriteLine($"  [异常] {name}：{ex.Message}")
                    End Try
                Next

                Console.WriteLine()
                Console.WriteLine("== 3) 结果 ==")
                If failures.Count = 0 Then
                    Console.WriteLine("全部通过。")
                    Console.WriteLine($"样本目录：{dir}")
                    Return 0
                End If
                Console.WriteLine($"{failures.Count} 项失败：")
                For Each f In failures
                    Console.WriteLine("  - " & f)
                Next
                Console.WriteLine($"样本目录：{dir}")
                Return 1
            Catch ex As Exception
                Console.WriteLine("端到端测试异常：" & ex.ToString())
                Return 1
            End Try
        End Function

        ' ==================== 样本生成 ====================

        Private Function WritePng(path As String) As String
            ' 1×1 透明 PNG（标准字节）
            Dim b64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="
            File.WriteAllBytes(path, Convert.FromBase64String(b64))
            Return path
        End Function

        Private Function WritePdf(path As String) As String
            Dim sb As New Text.StringBuilder()
            Dim objs As New List(Of String)
            objs.Add("<< /Type /Catalog /Pages 2 0 R >>")
            objs.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            objs.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>")
            Dim stream = "BT /F1 18 Tf 20 100 Td (SymbolPrint E2E) Tj ET"
            objs.Add($"<< /Length {stream.Length} >>" & vbLf & "stream" & vbLf & stream & vbLf & "endstream")
            objs.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")

            sb.Append("%PDF-1.4" & vbLf)
            Dim offsets As New List(Of Integer)
            For i As Integer = 0 To objs.Count - 1
                offsets.Add(sb.Length)
                sb.Append($"{i + 1} 0 obj" & vbLf & objs(i) & vbLf & "endobj" & vbLf)
            Next
            Dim xrefPos = sb.Length
            sb.Append($"xref" & vbLf & $"0 {objs.Count + 1}" & vbLf & "0000000000 65535 f " & vbLf)
            For Each o In offsets
                sb.Append(o.ToString("D10") & " 00000 n " & vbLf)
            Next
            sb.Append($"trailer" & vbLf & $"<< /Size {objs.Count + 1} /Root 1 0 R >>" & vbLf & "startxref" & vbLf & xrefPos & vbLf & "%%EOF" & vbLf)

            Dim bytes = Text.Encoding.ASCII.GetBytes(sb.ToString())
            File.WriteAllBytes(path, bytes)
            Return path
        End Function

        Private Function WriteText(path As String) As String
            Dim lines As New List(Of String)
            lines.Add("# SymbolPrint 端到端测试")
            lines.Add("")
            lines.Add("这是用于验证文本载体（不可见零宽字符）的样本文件。")
            lines.Add("**加粗**、*斜体*、`代码` 都应正常渲染。")
            lines.Add("")
            lines.Add("- 列表项一")
            lines.Add("- 列表项二")
            File.WriteAllText(path, String.Join(vbLf, lines), New Text.UTF8Encoding(False))
            Return path
        End Function

        Private Function WriteRtf(path As String) As String
            Dim rtf = "{\rtf1\ansi\deff0{\fonttbl{\f0 Calibri;}}" & vbLf &
                      "\f0\fs24 SymbolPrint E2E RTF sample.\par" & vbLf &
                      "This line must stay visible after stamping.\par" & vbLf & "}" & vbLf
            File.WriteAllText(path, rtf, Text.Encoding.ASCII)
            Return path
        End Function

        Private Function WriteZip(path As String, asDocx As Boolean) As String
            Using fs As New FileStream(path, FileMode.Create, FileAccess.Write)
                Using za As New ZipArchive(fs, ZipArchiveMode.Create)
                    If asDocx Then
                        AddEntry(za, "[Content_Types].xml",
                            "<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>" &
                            "<Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">" &
                            "<Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/>" &
                            "<Default Extension=""xml"" ContentType=""application/xml""/>" &
                            "<Override PartName=""/word/document.xml"" ContentType=""application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml""/>" &
                            "</Types>")
                        AddEntry(za, "_rels/.rels",
                            "<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>" &
                            "<Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">" &
                            "<Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""word/document.xml""/>" &
                            "</Relationships>")
                        AddEntry(za, "word/document.xml",
                            "<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>" &
                            "<w:document xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">" &
                            "<w:body><w:p><w:r><w:t>SymbolPrint E2E docx</w:t></w:r></w:p></w:body></w:document>")
                    Else
                        AddEntry(za, "readme.txt", "SymbolPrint E2E zip")
                        AddEntry(za, "data/values.csv", "a,b,c" & vbLf & "1,2,3" & vbLf)
                    End If
                End Using
            End Using
            Return path
        End Function

        Private Sub AddEntry(za As ZipArchive, name As String, content As String)
            Dim e = za.CreateEntry(name, CompressionLevel.Optimal)
            Using s = e.Open()
                Dim b = Text.Encoding.UTF8.GetBytes(content)
                s.Write(b, 0, b.Length)
            End Using
        End Sub

        ''' <summary>改动文件"有效内容"中间的一个字节（不是我们追加的尾部），模拟篡改。</summary>
        Private Sub TamperMiddleByte(path As String)
            Dim len = New FileInfo(path).Length
            If len < 16 Then Return
            Using fs As New FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                ' 取文件开头 12 字节处：所有受支持格式的这个位置都属于"原文件内容"
                ' （绝不能取尾部/注释/保留区 —— 那属于"记录被破坏"，不是"原文件被改动"）
                Dim pos = 12L
                fs.Position = pos
                Dim b = fs.ReadByte()
                fs.Position = pos
                fs.WriteByte(CByte((b Xor &H5A) And &HFF))
                fs.Flush()
            End Using
        End Sub

    End Module

End Namespace
