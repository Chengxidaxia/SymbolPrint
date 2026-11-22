Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text.Json
Imports SymbolPrint.Core.Crypto
Imports SymbolPrint.Core.Model
Imports SymbolPrint.Core.Util

Namespace SymbolPrint.Cli

    ''' <summary>
    ''' 一致性向量：VB 版与 C# 版必须产出**完全相同**的字节。
    ''' `vectors write` 生成（含输入 + 期望输出），`vectors check` 校验。
    ''' C# 版只读输入、重算、比对 —— 输入永远只有一份，不在两边各写一遍常量。
    ''' 随机性环节（ECIES 临时密钥、GCM nonce、章密钥）通过注入固定值消除。
    ''' </summary>
    Public Module VectorsCommand

        Public Class VectorCase
            Public Property Name As String = ""
            Public Property Kind As String = ""
            Public Property Input As New Dictionary(Of String, String)()
            Public Property Expected As New Dictionary(Of String, String)()
        End Class

        Public Class VectorsFile
            Public Property Format As Integer = 1
            Public Property Generator As String = "SymbolPrint/1.0 (VB.NET)"
            Public Property CreatedUtc As String = ""
            Public Property Cases As New List(Of VectorCase)()
        End Class

        Private ReadOnly JsonOpts As New JsonSerializerOptions With {.WriteIndented = True}

        Public Function Run(args As String()) As Integer
            Dim write As Boolean = args.Length > 0 AndAlso args(0).Equals("write", StringComparison.OrdinalIgnoreCase)
            Dim vecPath As String = If(args.Length > 1, args(1), Path.Combine(AppContext.BaseDirectory, "vectors.json"))

            Dim doc As VectorsFile
            If write Then
                doc = FillExpected(BuildCases())
                doc.CreatedUtc = DateTime.UtcNow.ToString("o")
                Dim dir = Path.GetDirectoryName(Path.GetFullPath(vecPath))
                If Not String.IsNullOrEmpty(dir) AndAlso Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)
                File.WriteAllText(vecPath, JsonSerializer.Serialize(doc, JsonOpts))
                Console.WriteLine($"已写入 {doc.Cases.Count} 个用例：{vecPath}")
                Return 0
            End If

            If Not File.Exists(vecPath) Then
                Console.WriteLine($"向量文件不存在：{vecPath}（先用 vectors write 生成）")
                Return 2
            End If
            doc = JsonSerializer.Deserialize(Of VectorsFile)(File.ReadAllText(vecPath), JsonOpts)

            Dim ok As Integer = 0
            Dim bad As Integer = 0
            Console.WriteLine($"向量文件：{vecPath}")
            Console.WriteLine($"生成者：{doc.Generator}  {doc.CreatedUtc}")
            For Each c In doc.Cases
                Try
                    Dim actual = Compute(c)
                    Dim diffs As New List(Of String)()
                    For Each kv In c.Expected
                        Dim got As String = ""
                        If Not actual.TryGetValue(kv.Key, got) Then
                            diffs.Add($"{kv.Key}: 缺少输出")
                        ElseIf Not String.Equals(got, kv.Value, StringComparison.Ordinal) Then
                            diffs.Add($"{kv.Key}: 期望 {Brief(kv.Value)} 实际 {Brief(got)}")
                        End If
                    Next
                    If diffs.Count = 0 Then
                        ok += 1
                        Console.WriteLine($"[PASS] {c.Name}")
                    Else
                        bad += 1
                        Console.WriteLine($"[FAIL] {c.Name}  " & String.Join(" | ", diffs))
                    End If
                Catch ex As Exception
                    bad += 1
                    Console.WriteLine($"[FAIL] {c.Name}  异常 {ex.GetType().Name}: {ex.Message}")
                End Try
            Next
            Console.WriteLine()
            Console.WriteLine($"通过 {ok} / {ok + bad}")
            Return If(bad = 0, 0, 1)
        End Function

        Private Function Brief(hex As String) As String
            If hex Is Nothing Then Return "(null)"
            If hex.Length <= 24 Then Return hex
            Return hex.Substring(0, 24) & "…"
        End Function

        ' ==================== 用例定义 ====================

        Private Function NewCase(name As String, kind As String, ParamArray kv As String()) As VectorCase
            Dim c As New VectorCase()
            c.Name = name
            c.Kind = kind
            For i As Integer = 0 To kv.Length - 1 Step 2
                c.Input(kv(i)) = kv(i + 1)
            Next
            Return c
        End Function

        Private Function BuildCases() As VectorsFile
            Dim f As New VectorsFile()

            f.Cases.Add(NewCase("sha3_512/empty", "sha3_512", "data", ""))
            f.Cases.Add(NewCase("sha3_512/abc", "sha3_512", "data", Bin.Hex(Bin.Utf8Bytes("abc"))))
            f.Cases.Add(NewCase("sha3_512/1k", "sha3_512", "data", Bin.Hex(Pattern(1024))))

            f.Cases.Add(NewCase("hmac_sha3_512/basic", "hmac_sha3_512",
                                "key", Bin.Hex(Bin.Utf8Bytes("key-0123456789")),
                                "message", Bin.Hex(Bin.Utf8Bytes("SymbolPrint hmac test"))))

            f.Cases.Add(NewCase("hkdf/sha3_512", "hkdf",
                                "ikm", Bin.Hex(Pattern(32)),
                                "salt", Bin.Hex(Pattern(16)),
                                "info", Bin.Hex(Bin.Utf8Bytes("SymbolPrint-ECIES-v1")),
                                "len", "32"))

            f.Cases.Add(NewCase("pbkdf2/1000", "pbkdf2",
                                "password", Bin.Hex(Bin.Utf8Bytes("p@ssw0rd")),
                                "salt", Bin.Hex(Pattern(16)),
                                "iterations", "1000",
                                "len", "32"))

            ' AES-GCM：固定 key/nonce 才能复现
            Dim gcmKey = Pattern(32, 7)
            Dim gcmNonce = Pattern(12, 11)
            Dim gcmPlain = Bin.Utf8Bytes("电子盖章 SymbolPrint payload 0123456789")
            f.Cases.Add(NewCase("aes_gcm/encrypt", "aes_gcm",
                                "key", Bin.Hex(gcmKey),
                                "nonce", Bin.Hex(gcmNonce),
                                "plain", Bin.Hex(gcmPlain)))

            ' ECIES：接收方密钥 + 临时私钥 + nonce 全部固定
            Dim recip = Ecies.GenerateKeys(EcCurveId.NistP256)
            Dim eph = Ecies.GenerateKeys(EcCurveId.NistP256)
            f.Cases.Add(NewCase("ecies/p256", "ecies",
                                "recipientSpki", Bin.Hex(recip.PublicSpki),
                                "ephPriv", Bin.Hex(eph.PrivatePkcs8),
                                "nonce", Bin.Hex(Pattern(12, 13)),
                                "plain", Bin.Hex(gcmPlain),
                                "curve", CStr(CInt(EcCurveId.NistP256))))

            ' 章文件：整字节哈希（含 128B 头 + 64KB 空白区 + TLV 正文）
            f.Cases.Add(NewCase("stamp_bin/roundtrip", "stamp_bin",
                                "pub", Bin.Hex(recip.PublicSpki),
                                "priv", Bin.Hex(recip.PrivatePkcs8),
                                "label", Bin.Hex(Bin.Utf8Bytes("测试章 Alpha")),
                                "desc", Bin.Hex(Bin.Utf8Bytes("SymbolPrint 一致性向量用章")),
                                "svg", Bin.Hex(Bin.Utf8Bytes("<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64'><circle cx='32' cy='32' r='28' fill='none' stroke='red' stroke-width='4'/></svg>")),
                                "seal", Bin.Hex(Pattern(256, 17)),
                                "createdAtMs", "1700000000000",
                                "curve", "1"))

            ' 盖章记录的原始字节
            f.Cases.Add(NewCase("seal_record/v1", "seal_record",
                                "key1", Bin.Hex(Pattern(32, 19)),
                                "nonce1", Bin.Hex(Pattern(12, 23)),
                                "tag1", Bin.Hex(Pattern(16, 29)),
                                "section1", Bin.Hex(Pattern(180, 31)),
                                "stampTimeMs", "1700000123456",
                                "visualBurned", "1"))

            Return f
        End Function

        ''' <summary>确定性的伪随机字节（同一下标的输入永远相同，方便复现）。</summary>
        Private Function Pattern(n As Integer, Optional seed As Integer = 3) As Byte()
            If n <= 0 Then Return Array.Empty(Of Byte)()
            ' 用 SHA3 派生确定性字节：跨语言复现最简单（C# 版照抄这句话即可），也彻底避开算术溢出
            Dim b(n - 1) As Byte
            Dim off As Integer = 0
            Dim counter As Integer = 0
            While off < n
                Dim block = Sha3Kdf.Hash512(Bin.Utf8Bytes("SymbolPrint/vector/" & seed.ToString() & "/" & counter.ToString()))
                Dim take As Integer = Math.Min(64, n - off)
                Buffer.BlockCopy(block, 0, b, off, take)
                off += take
                counter += 1
            End While
            Return b
        End Function

        ' ==================== 计算 ====================

        Private Function Compute(c As VectorCase) As Dictionary(Of String, String)
            Dim r As New Dictionary(Of String, String)(StringComparer.Ordinal)
            Select Case c.Kind
                Case "sha3_512"
                    r("digest") = Bin.Hex(Sha3Kdf.Hash512(Bin.FromHex(c.Input("data"))))

                Case "hmac_sha3_512"
                    r("mac") = Bin.Hex(Sha3Kdf.HmacSha3_512(Bin.FromHex(c.Input("key")), Bin.FromHex(c.Input("message"))))

                Case "hkdf"
                    r("okm") = Bin.Hex(Sha3Kdf.Hkdf(Bin.FromHex(c.Input("ikm")), Bin.FromHex(c.Input("salt")),
                                                    Bin.FromHex(c.Input("info")), CInt(c.Input("len"))))

                Case "pbkdf2"
                    r("dk") = Bin.Hex(Sha3Kdf.Pbkdf2(Bin.FromHex(c.Input("password")), Bin.FromHex(c.Input("salt")),
                                                     CInt(c.Input("iterations")), CInt(c.Input("len"))))

                Case "aes_gcm"
                    Dim res = AesGcmUtil.Encrypt(Bin.FromHex(c.Input("key")), Bin.FromHex(c.Input("nonce")), Bin.FromHex(c.Input("plain")))
                    r("cipher") = Bin.Hex(res.Cipher)
                    r("tag") = Bin.Hex(res.Tag)

                Case "ecies"
                    Dim env = Ecies.Encrypt(Bin.FromHex(c.Input("recipientSpki")), Bin.FromHex(c.Input("plain")),
                                            CType(CInt(c.Input("curve")), EcCurveId),
                                            Bin.FromHex(c.Input("ephPriv")), Bin.FromHex(c.Input("nonce")))
                    r("envelope") = Bin.Hex(env)

                Case "stamp_bin"
                    Dim st As New StampFile()
                    st.Keys = New EcKeys()
                    st.Keys.PublicSpki = Bin.FromHex(c.Input("pub"))
                    st.Keys.PrivatePkcs8 = Bin.FromHex(c.Input("priv"))
                    st.Keys.CurveId = CType(CInt(c.Input("curve")), EcCurveId)
                    st.Label = Bin.Utf8(Bin.FromHex(c.Input("label")))
                    st.Description = Bin.Utf8(Bin.FromHex(c.Input("desc")))
                    st.SvgMarkup = Bin.Utf8(Bin.FromHex(c.Input("svg")))
                    st.SealRawBytes = Bin.FromHex(c.Input("seal"))
                    st.CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(CLng(c.Input("createdAtMs")))
                    Dim tmp = Path.Combine(Path.GetTempPath(), "sp_vec_" & Guid.NewGuid().ToString("N") & ".bin")
                    Try
                        st.Save(tmp)
                        r("fileSha3") = Bin.Hex(Sha3Kdf.Hash512(File.ReadAllBytes(tmp)))
                        r("size") = CStr(New FileInfo(tmp).Length)
                        r("stampId") = Bin.Hex(st.StampId)
                    Finally
                        Try
                            File.Delete(tmp)
                        Catch
                        End Try
                    End Try

                Case "seal_record"
                    Dim rec As New SealRecord()
                    ' 用例名带 /v1 的，按 v1 布局复现（守住老记录的字节形态）；
                    ' 其余按当前版本写出，这样新老两代格式都被向量钉住
                    If c.Name IsNot Nothing AndAlso c.Name.EndsWith("/v1", StringComparison.Ordinal) Then
                        rec.RecordVer = 1
                    End If
                    rec.Key1 = Bin.FromHex(c.Input("key1"))
                    rec.Nonce1 = Bin.FromHex(c.Input("nonce1"))
                    rec.Tag1 = Bin.FromHex(c.Input("tag1"))
                    rec.Section1Cipher = Bin.FromHex(c.Input("section1"))
                    rec.StampTime = DateTimeOffset.FromUnixTimeMilliseconds(CLng(c.Input("stampTimeMs")))
                    rec.VisualBurned = c.Input("visualBurned") = "1"
                    rec.SealOwnHash()
                    Dim raw = rec.Serialize()
                    r("recordSha3") = Bin.Hex(Sha3Kdf.Hash512(raw))
                    r("ownHash") = Bin.Hex(rec.OwnContentSha3)
                    r("size") = CStr(raw.Length)

                Case Else
                    Throw New NotSupportedException($"未知的用例类型：{c.Kind}")
            End Select
            Return r
        End Function

        ''' <summary>生成时把算出来的值填进 Expected。</summary>
        Private Function FillExpected(f As VectorsFile) As VectorsFile
            For Each c In f.Cases
                c.Expected = Compute(c)
            Next
            Return f
        End Function

    End Module

End Namespace
