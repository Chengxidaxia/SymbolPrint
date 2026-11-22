Imports System
Imports SymbolPrint.Core.Crypto
Imports SymbolPrint.Core.Util
Imports SymbolPrint.Core.Model

Namespace SymbolPrint.Cli

    ''' <summary>
    ''' 进度显示组合：延迟门控（快操作不显示）→ 节流（跨线程/重绘不过载）→ 文本条。
    ''' Ctrl+C 不是直接杀进程，而是置取消标记，让操作在下一个检查点优雅退出（半截文件不会留下）。
    ''' </summary>
    Public NotInheritable Class ConsoleProgress
        Implements IDisposable

        Private ReadOnly _renderer As TextProgress
        Private ReadOnly _cts As New Threading.CancellationTokenSource()
        Private ReadOnly _handler As ConsoleCancelEventHandler

        Public ReadOnly Property Sink As IProgressSink

        Public Sub New()
            _renderer = New TextProgress(Function() _cts.IsCancellationRequested)
            Sink = New DelayedProgress(New ThrottledProgress(_renderer, 60, 0.005), 250)
            _handler = Sub(sender, e)
                           e.Cancel = True
                           If Not _cts.IsCancellationRequested Then _cts.Cancel()
                       End Sub
            AddHandler Console.CancelKeyPress, _handler
        End Sub

        ''' <summary>确保动态条收尾换行，避免残留半行。</summary>
        Public Sub Dispose() Implements IDisposable.Dispose
            RemoveHandler Console.CancelKeyPress, _handler
            _renderer.FinishLine()
            _cts.Dispose()
        End Sub
    End Class

    Module Program

        Function Main(args As String()) As Integer
            Console.OutputEncoding = Text.Encoding.UTF8
            If args.Length = 0 Then
                Console.WriteLine("SymbolPrint CLI 1.0")
                Console.WriteLine("用法:")
                Console.WriteLine("  spcli selftest          密码学/载体自检（含进度机制）")
                Console.WriteLine("  spcli hash <文件>        流式算 SHA3-512（带进度条）")
                Console.WriteLine("  spcli demo              进度条实况演示（大文件哈希 / PBKDF2 / 压缩）")
                Console.WriteLine("  spcli settings [--all]   列出全部参数（高级选项面板的字段清单）")
                Console.WriteLine("  spcli carriertest       载体自测（ZIP 注释区：不动原有字节）")
                Console.WriteLine("  spcli vectors write|check [文件]  双版本字节级一致性向量")
                Console.WriteLine("  spcli make <输出.bin> [--label 名] [--desc 描述] [--seal-bytes 文件] [--pass 口令]")
                Console.WriteLine("  spcli stamp <文件> --seal <章.bin> [--pass 口令] [--quality 0-11]")
                Console.WriteLine("  spcli verify <文件> [--seal <章.bin>] [--pass 口令]")
                Console.WriteLine("  spcli unseal <文件>      摘除盖章记录")
                Console.WriteLine("  spcli e2etest           全格式端到端（做章→盖章→验章→篡改检测）")
                Console.WriteLine("  spcli svg2png <in.svg> <out.png> [--width 512]   SVG 光栅化")
                Console.WriteLine("    盖章贴图参数：--mark-svg/--mark-png/--mark-x/--mark-y/--mark-w/--mark-opacity/--mark-rotate/--mark-page/--no-backup")
                Return 0
            End If

            Try
                Return Dispatch(args)
            Catch ex As OperationCanceledException
                Console.WriteLine()
                Console.WriteLine("已取消。")
                Return 130
            Catch ex As Exception
                Console.WriteLine()
                ' 给用户看的是"发生了什么 + 怎么处理"，不是堆栈；堆栈只在 Debug 环境变量打开时打
                Console.Error.WriteLine("出错了：" & ex.Message)
                If Environment.GetEnvironmentVariable("SP_DEBUG") = "1" Then Console.Error.WriteLine(ex.ToString())
                Return 1
            End Try
        End Function

        Private Function Dispatch(args As String()) As Integer
            Select Case args(0).ToLowerInvariant()
                Case "selftest"
                    Return SelfTest()
                Case "hash"
                    If args.Length < 2 Then
                        Console.WriteLine("用法: spcli hash <文件>")
                        Return 2
                    End If
                    Return CmdHash(args(1))
                Case "demo"
                    Return CmdDemo()
                Case "settings"
                    Return SettingsCommand.Run(args)
                Case "carriertest"
                    Return CarrierTest.Run()
                Case "make", "stamp", "verify", "unseal", "e2etest", "svg2png"
                    Return SealCommand.Run(args)
                Case "vectors"
                    ' 默认向量文件按「当前目录 → 程序目录向上」找，不能写死本机路径（公开仓库后别人也要能跑）
                    Dim def = FindDefaultVectors()
                    Return VectorsCommand.Run(New String() {If(args.Length > 1, args(1), "check"),
                                                             If(args.Length > 2, args(2), def)})
                Case Else
                    Console.WriteLine($"未知命令: {args(0)}")
                    Return 2
            End Select
        End Function

        ''' <summary>找测试向量文件：当前目录优先，其次从程序目录逐级往上找，最后退回程序目录。</summary>
        Private Function FindDefaultVectors() As String
            Dim name = IO.Path.Combine("tests", "vectors", "crypto.json")
            Dim here = IO.Path.Combine(Environment.CurrentDirectory, name)
            If IO.File.Exists(here) Then Return here

            Dim d = New IO.DirectoryInfo(AppContext.BaseDirectory)
            While d IsNot Nothing
                Dim p = IO.Path.Combine(d.FullName, name)
                If IO.File.Exists(p) Then Return p
                d = d.Parent
            End While

            Return IO.Path.Combine(AppContext.BaseDirectory, "crypto.json")
        End Function

        ' ==================== 命令 ====================

        Private Function CmdHash(path As String) As Integer
            If Not IO.File.Exists(path) Then
                Console.WriteLine($"文件不存在：{path}")
                Return 2
            End If
            Dim len As Long = New IO.FileInfo(path).Length
            Dim sw = Diagnostics.Stopwatch.StartNew()
            Using ui As New ConsoleProgress()
                Dim digest = Sha3Kdf.HashFile(path, ui.Sink, "SHA3-512")
                sw.Stop()
                Dim mbps = If(sw.ElapsedMilliseconds > 0, len / 1048576.0 / (sw.ElapsedMilliseconds / 1000.0), 0)
                Console.WriteLine($"SHA3-512 = {Bin.Hex(digest)}")
                Console.WriteLine($"{Comp.HumanSize(len)}，耗时 {sw.ElapsedMilliseconds} ms（{mbps:0.0} MB/s）")
            End Using
            Return 0
        End Function

        Private Function CmdDemo() As Integer
            Dim tmp = IO.Path.Combine(IO.Path.GetTempPath(), "sp_demo_" & Guid.NewGuid().ToString("N") & ".dat")
            Try
                ' 1) 造一个 64 MB 的测试文件（顺便让用户看到写入进度）
                Const total As Long = 64L * 1024 * 1024
                Using ui As New ConsoleProgress()
                    Using fs As New IO.FileStream(tmp, IO.FileMode.Create, IO.FileAccess.Write, IO.FileShare.None)
                        Dim buf(1024 * 1024 - 1) As Byte
                        Dim rnd As New Random(12345)
                        rnd.NextBytes(buf)
                        Dim wrote As Long = 0
                        While wrote < total
                            Dim n As Integer = CInt(Math.Min(buf.Length, total - wrote))
                            fs.Write(buf, 0, n)
                            wrote += n
                            ui.Sink.Report("写入测试文件", wrote / CDbl(total), ProgressExt.BytesDetail(wrote, total))
                        End While
                    End Using
                    ui.Sink.Completed("写入测试文件")
                End Using
                Console.WriteLine($"测试文件：{Comp.HumanSize(New IO.FileInfo(tmp).Length)}")

                ' 2) 流式哈希（真实需要进度条的操作）
                Dim sw = Diagnostics.Stopwatch.StartNew()
                Using ui As New ConsoleProgress()
                    Dim d = Sha3Kdf.HashFile(tmp, ui.Sink, "SHA3-512（流式）")
                    sw.Stop()
                    Console.WriteLine($"摘要 {Bin.Hex(d).Substring(0, 32)}…  耗时 {sw.ElapsedMilliseconds} ms")
                End Using

                ' 3) PBKDF2（纯计算，最容易让人以为卡死）
                sw.Restart()
                Using ui As New ConsoleProgress()
                    Dim k = Sha3Kdf.Pbkdf2(Bin.Utf8Bytes("p@ssw0rd"), Bin.RandomBytes(16), 40000, 64, ui.Sink, "PBKDF2-HMAC-SHA3-512")
                    sw.Stop()
                    Console.WriteLine($"派生密钥 {Bin.Hex(k).Substring(0, 16)}…  耗时 {sw.ElapsedMilliseconds} ms")
                End Using

                ' 4) Brotli 压缩
                Dim sample = IO.File.ReadAllBytes(tmp)
                sw.Restart()
                Using ui As New ConsoleProgress()
                    Dim packed2 = Comp.Pack(sample, 9, ui.Sink, "Brotli 压缩")
                    sw.Stop()
                    Console.WriteLine($"压缩 {Comp.HumanSize(sample.Length)} → {Comp.HumanSize(packed2.Data.Length)}（方法 {packed2.Method}），耗时 {sw.ElapsedMilliseconds} ms")
                End Using
            Finally
                Try
                    IO.File.Delete(tmp)
                Catch
                End Try
            End Try
            Return 0
        End Function

        ' ==================== 自检 ====================

        Private Function SelfTest() As Integer
            _failed = False

            ' 1) SHA3-512 标准测试向量（空串）
            Dim expectEmpty = "A69F73CCA23A9AC5C8B567DC185A756E97C982164FE25859E0D1DCC1475C80A615B2123AF1F5F94C11E3E9402C3AC558F500199D95B6D3E301758586281DCD26"
            Dim got = Bin.Hex(Sha3Kdf.Hash512(Array.Empty(Of Byte)()))
            Report("SHA3-512(空串) 测试向量", got = expectEmpty, got)

            ' 2) SHA3-512("abc")
            Dim expectAbc = "B751850B1A57168A5693CD924B6B096E08F621827444F70D884F5D0240D2712E10E116E9192AF3C91A7EC57647E3934057340B4CF408D5A56592F8274EEC53F0"
            Dim gotAbc = Bin.Hex(Sha3Kdf.Hash512(Bin.Utf8Bytes("abc")))
            Report("SHA3-512(abc) 测试向量", gotAbc = expectAbc, gotAbc)

            ' 3) PBKDF2 稳定性 + 耗时
            Dim sw = Diagnostics.Stopwatch.StartNew()
            Dim k1 = Sha3Kdf.Pbkdf2(Bin.Utf8Bytes("password"), Bin.RandomBytes(16), 10000, 32)
            sw.Stop()
            Dim sw2 = Diagnostics.Stopwatch.StartNew()
            Dim k2 = Sha3Kdf.Pbkdf2(Bin.Utf8Bytes("password"), Bin.RandomBytes(16), 10000, 32)
            sw2.Stop()
            Report("PBKDF2-HMAC-SHA3-512 (1万次)", k1.Length = 32 AndAlso Not Bin.Same(k1, k2), $"{sw.ElapsedMilliseconds}ms / {sw2.ElapsedMilliseconds}ms")

            ' 4) Brotli 往返
            Dim sample = Bin.Utf8Bytes(String.Join("", Linq.Enumerable.Repeat("SymbolPrint 电子盖章 test data 0123456789 ", 40)))
            Dim packed2 = Comp.Pack(sample)
            Dim back = Comp.Unpack(packed2.Method, packed2.Data)
            Report("Brotli 压缩往返", Bin.Same(sample, back), $"{sample.Length} -> {packed2.Data.Length} 字节，方法={packed2.Method}")

            ' 5) ECIES 往返
            Dim keys = Ecies.GenerateKeys(EcCurveId.NistP256)
            Dim env = Ecies.Encrypt(keys.PublicSpki, sample, EcCurveId.NistP256)
            Dim dec = Ecies.Decrypt(keys.PrivatePkcs8, env)
            Report("ECIES(P-256) 加解密往返", Bin.Same(sample, dec), $"信封 {env.Length} 字节")

            ' 6) ECIES 错误私钥应失败
            Dim other = Ecies.GenerateKeys(EcCurveId.NistP256)
            Dim wrongFailed As Boolean = False
            Try
                Ecies.Decrypt(other.PrivatePkcs8, env)
            Catch
                wrongFailed = True
            End Try
            Report("ECIES 错误私钥必须解密失败", wrongFailed, "")

            ' 7) AES-GCM 篡改检测
            Dim key1 = AesGcmUtil.NewKey()
            Dim nonce = AesGcmUtil.NewNonce()
            Dim r1 = AesGcmUtil.Encrypt(key1, nonce, sample)
            Dim tampered = CType(r1.Cipher.Clone(), Byte())
            tampered(0) = CByte(tampered(0) Xor 1)
            Dim tamperFailed As Boolean = False
            Try
                AesGcmUtil.Decrypt(key1, nonce, tampered, r1.Tag)
            Catch
                tamperFailed = True
            End Try
            Report("AES-GCM 篡改检测", tamperFailed, "")

            ' 8) 章文件往返
            Dim stamp As New StampFile()
            stamp.Label = "测试章"
            stamp.Description = "自检用章描述"
            stamp.SvgMarkup = "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><rect width='10' height='10'/></svg>"
            stamp.SealRawBytes = Bin.RandomBytes(120)
            Dim tmp = IO.Path.Combine(IO.Path.GetTempPath(), "sp_selftest_" & Guid.NewGuid().ToString("N") & ".bin")
            stamp.Save(tmp)
            Dim loaded = StampFile.Load(tmp)
            Dim roundTrip = loaded.Label = stamp.Label AndAlso loaded.Description = stamp.Description AndAlso
                            loaded.SvgMarkup = stamp.SvgMarkup AndAlso
                            Bin.Same(loaded.SealRawBytes, stamp.SealRawBytes) AndAlso
                            Bin.Same(loaded.StampId, stamp.StampId)
            Report("章 .bin 往返", roundTrip, $"文件 {Comp.HumanSize(New IO.FileInfo(tmp).Length)}，身份 {loaded.StampIdShort}")

            ' 9) 口令保护
            stamp.SetPassphrase("p@ss")
            Dim tmp2 = tmp & ".enc.bin"
            stamp.Save(tmp2)
            Dim locked = StampFile.Load(tmp2)
            Dim lockOk = locked.IsLocked
            locked.Unlock("p@ss")
            Dim unlockOk = Bin.Same(locked.GetPrivateKey(), stamp.Keys.PrivatePkcs8)
            Dim badPwdFailed As Boolean = False
            Try
                StampFile.Load(tmp2, "wrong")
            Catch
                badPwdFailed = True
            End Try
            Report("章口令保护 / 解锁 / 错误口令", lockOk AndAlso unlockOk AndAlso badPwdFailed, "")

            Try
                IO.File.Delete(tmp)
                IO.File.Delete(tmp2)
            Catch
            End Try

            ' 10) 流式哈希 == 一次性哈希（含各种跨块边界分片）
            Dim big As Byte() = Bin.RandomBytes(Keccak512.Rate * 1000 + 37)
            Dim oneShot = Keccak512.Hash(big)
            Dim chunks As Integer() = {1, 7, 71, 72, 73, Keccak512.Rate, Keccak512.Rate + 1, 1000, 65536}
            Dim streamOk As Boolean = True
            Dim detail As String = ""
            For Each cs In chunks
                Dim h As New Keccak512.Hasher()
                Dim off As Integer = 0
                While off < big.Length
                    Dim n As Integer = Math.Min(cs, big.Length - off)
                    h.Update(big, off, n)
                    off += n
                End While
                Dim d2 = h.Finish()
                If Not Bin.Same(d2, oneShot) Then
                    streamOk = False
                    detail = $"分片 {cs} 字节时不一致"
                    Exit For
                End If
            Next
            Report("流式 SHA3-512 == 一次性（1..64K 分片）", streamOk, If(streamOk, $"{big.Length} 字节", detail))

            ' 11) 文件流式哈希 == 内存哈希
            Dim fpath = IO.Path.Combine(IO.Path.GetTempPath(), "sp_hash_" & Guid.NewGuid().ToString("N") & ".dat")
            IO.File.WriteAllBytes(fpath, big)
            Dim fromFile = Sha3Kdf.HashFile(fpath)
            IO.File.Delete(fpath)
            Report("HashFile == Hash512", Bin.Same(fromFile, oneShot), Comp.HumanSize(big.Length))

            ' 12) 进度机制：单调递增、末值 1、延迟门控、作用域映射
            Dim rec2 As New Recorder()
            Using ms As New IO.MemoryStream(big)
                Sha3Kdf.HashStream(ms, big.Length, rec2, "哈希")
            End Using
            Report("哈希过程上报进度且末值=1", rec2.IsMonotonic AndAlso rec2.LastFraction >= 1.0, $"{rec2.Count} 次上报，末值 {rec2.LastFraction:0.000}")

            Dim rec3 As New Recorder()
            Sha3Kdf.Pbkdf2(Bin.Utf8Bytes("p"), Bin.RandomBytes(16), 2000, 64, rec3, "kdf")
            Report("PBKDF2 上报进度（0→1 单调）", rec3.Count > 3 AndAlso rec3.IsMonotonic AndAlso rec3.LastFraction >= 1.0, $"{rec3.Count} 次上报")

            Dim recGood As New Recorder()
            Dim gated As New DelayedProgress(recGood, 500)
            gated.Report("快操作", 0.5, Nothing)
            gated.Report("快操作", 1.0, Nothing)
            Report("快操作被延迟门控完全抑制", recGood.Count = 0, $"{recGood.Count} 次透传")

            Dim recScope As New Recorder()
            Dim root As New ProgressScope(recScope, 0.0, 1.0, "总")
            Using mid = root.Slice(0.2, 0.6, "阶段B")
                mid.Report("子步骤", 0.5, Nothing)
                mid.Report("子步骤", 1.0, Nothing)
            End Using
            Dim mapOk = recScope.Count = 2 AndAlso Math.Abs(recScope.Points(0).Fraction - 0.4) < 0.001 AndAlso
                        Math.Abs(recScope.Points(1).Fraction - 0.6) < 0.001 AndAlso
                        recScope.Points(0).Stage = "总 · 阶段B · 子步骤"
            Report("ProgressScope 区间映射", mapOk,
                   If(recScope.Count >= 2, $"{recScope.Points(0).Fraction:0.00} → {recScope.Points(1).Fraction:0.00}", "无上报"))

            Console.WriteLine()
            Console.WriteLine(If(Not _failed, "全部自检通过。", "存在失败项，见上。"))
            Return If(Not _failed, 0, 1)
        End Function

        ''' <summary>记录进度上报，用于验证。</summary>
        Private NotInheritable Class Recorder
            Implements IProgressSink

            Public Structure Pt
                Public Stage As String
                Public Fraction As Double
            End Structure

            Public ReadOnly Points As New Collections.Generic.List(Of Pt)()
            Public ReadOnly StageNames As New Collections.Generic.List(Of String)()

            Public ReadOnly Property Count As Integer
                Get
                    Return Points.Count
                End Get
            End Property

            Public ReadOnly Property LastFraction As Double
                Get
                    If Points.Count = 0 Then Return -1.0
                    Return Points(Points.Count - 1).Fraction
                End Get
            End Property

            Public ReadOnly Property IsMonotonic As Boolean
                Get
                    Dim prev As Double = -2.0
                    For Each p In Points
                        If p.Fraction < 0 Then Continue For
                        If p.Fraction < prev Then Return False
                        prev = p.Fraction
                    Next
                    Return True
                End Get
            End Property

            Public Property CancelFlag As Boolean = False

            Public ReadOnly Property IsCancelled As Boolean Implements IProgressSink.IsCancelled
                Get
                    Return CancelFlag
                End Get
            End Property

            Public Sub Report(stage As String, fraction As Double, detail As String) Implements IProgressSink.Report
                Dim p As New Pt()
                p.Stage = stage
                p.Fraction = fraction
                Points.Add(p)
                StageNames.Add(stage)
            End Sub
        End Class

        Private Sub Report(name As String, pass As Boolean, detail As String)
            If Not pass Then _failed = True
            Console.WriteLine($"[{(If(pass, "PASS", "FAIL"))}] {name}  {detail}")
        End Sub

        Private _failed As Boolean = False

    End Module

End Namespace
