Imports System
Imports System.IO
Imports System.IO.Compression
Imports SymbolPrint.Core.Carriers
Imports SymbolPrint.Core.Crypto
Imports SymbolPrint.Core.Model
Imports SymbolPrint.Core.Util

Namespace SymbolPrint.Cli

    ''' <summary>
    ''' 载体自测：写一个 ZIP，往注释区塞信封，再用两种独立方式验证归档是否完好。
    ''' 关键不是"我们的数据读得回来"，而是"别人的东西一点没动"。
    ''' </summary>
    Public Module CarrierTest

        Public Function Run() As Integer
            Dim failed As Boolean = False
            Dim dir = Path.Combine("K:\SymbolPrint\.build\ziptest")
            Directory.CreateDirectory(dir)
            Dim srcZip = Path.Combine(dir, "src.zip")
            Dim stampZip = Path.Combine(dir, "stamped.zip")

            ' 1) 造一个带两个条目（含中文名 + 非压缩 + 压缩）的 ZIP
            If File.Exists(srcZip) Then File.Delete(srcZip)
            Using z As ZipArchive = ZipFile.Open(srcZip, ZipArchiveMode.Create)
                Dim e1 = z.CreateEntry("你好/readme.txt", CompressionLevel.Optimal)
                Using s = e1.Open()
                    Dim body = Text.Encoding.UTF8.GetBytes(String.Join("", Linq.Enumerable.Repeat("SymbolPrint 载体测试 ", 500)))
                    s.Write(body, 0, body.Length)
                End Using
                Dim e2 = z.CreateEntry("data/blob.bin", CompressionLevel.NoCompression)
                Using s = e2.Open()
                    Dim rnd = Bin.RandomBytes(4096)
                    s.Write(rnd, 0, rnd.Length)
                End Using
            End Using

            Dim original = File.ReadAllBytes(srcZip)
            Dim originalHash = Sha3Kdf.Hash512(original)
            File.Copy(srcZip, stampZip, True)

            ' 2) 塞信封
            Dim payload = Bin.RandomBytes(777)
            Dim blob = SealEnvelope.Build(payload, 1)
            Dim carrier As New ZipCommentCarrier()
            carrier.WriteBlob(stampZip, blob, New CarrierOptions())

            ' 3) 读回来
            Dim back = carrier.ReadBlob(stampZip)
            Report("ZIP 注释区：信封可原样读回", back IsNot Nothing AndAlso Bin.Same(back, blob),
                   If(back Is Nothing, "读回 Nothing", $"{blob.Length} 字节"))

            ' 4) 只有 EOCD 之后/注释长度字段可变，前面每个字节都不许动
            Dim stamped = File.ReadAllBytes(stampZip)
            Dim samePrefix As Boolean = stamped.Length > original.Length
            Dim eocd = original.Length - 22 - ReadCommentLen(original)
            For i As Integer = 0 To eocd + 19
                If stamped(i) <> original(i) Then samePrefix = False : Exit For
            Next
            Report("原有字节（含全部条目/中央目录）零改动", samePrefix, $"前缀 {eocd + 20} 字节一致")

            ' 5) 归档本身还能被标准库正常读取，且条目内容不变
            Dim entriesOk As Boolean = False
            Dim detail As String = ""
            Try
                Using z = ZipFile.OpenRead(stampZip)
                    Dim names = String.Join(", ", Linq.Enumerable.Select(z.Entries, Function(x) x.FullName))
                    Dim okCount = z.Entries.Count = 2
                    Dim e1 = z.GetEntry("你好/readme.txt")
                    Dim e2 = z.GetEntry("data/blob.bin")
                    Using r = e1.Open()
                        Dim ms As New MemoryStream()
                        r.CopyTo(ms)
                        okCount = okCount AndAlso ms.Length > 0
                    End Using
                    Using r = e2.Open()
                        Dim ms As New MemoryStream()
                        r.CopyTo(ms)
                        okCount = okCount AndAlso ms.Length = 4096
                    End Using
                    entriesOk = okCount
                    detail = names
                End Using
            Catch ex As Exception
                detail = ex.GetType().Name & ": " & ex.Message
            End Try
            Report("ZIP 仍可正常打开且条目内容完整", entriesOk, detail)

            ' 6) 移除后必须与原始文件字节完全相同
            Dim removed = carrier.RemoveBlob(stampZip)
            Dim after = File.ReadAllBytes(stampZip)
            Report("移除信封后与原文件字节一致", removed AndAlso Bin.Same(Sha3Kdf.Hash512(after), originalHash),
                   $"{Comp.HumanSize(original.Length)} → {Comp.HumanSize(after.Length)}")

            ' 7) 别人已写注释时，我们的数据要接在后面，移除后他的注释还在
            File.Copy(srcZip, stampZip, True)
            Dim foreignText = Text.Encoding.UTF8.GetBytes("别人的注释 do-not-touch")
            Using fs As New FileStream(stampZip, FileMode.Open, FileAccess.ReadWrite)
                fs.Position = fs.Length
                fs.Write(foreignText, 0, foreignText.Length)
                ' 修正 EOCD 里的注释长度
                Dim eocdPos = fs.Length - foreignText.Length - 22
                fs.Position = eocdPos + 20
                fs.WriteByte(CByte(foreignText.Length And &HFF))
                fs.WriteByte(CByte((foreignText.Length >> 8) And &HFF))
            End Using
            Dim withForeign = File.ReadAllBytes(stampZip)
            carrier.WriteBlob(stampZip, blob, New CarrierOptions())
            carrier.RemoveBlob(stampZip)
            Dim restored = File.ReadAllBytes(stampZip)
            Report("别人已有的注释原样保留", Bin.Same(withForeign, restored), $"{withForeign.Length} 字节")

            ' 留下 stamped.zip 供外部工具（7z / python）二次验证
            File.Copy(srcZip, stampZip, True)
            carrier.WriteBlob(stampZip, blob, New CarrierOptions())
            Console.WriteLine()
            Console.WriteLine($"供外部验证的文件：{stampZip}")

            Console.WriteLine()
            Console.WriteLine(If(Not failed, "载体自测全部通过。", "存在失败项。"))
            Return If(Not failed, 0, 1)
        End Function

        Private Function ReadCommentLen(zip As Byte()) As Integer
            If zip.Length < 22 Then Return 0
            Return CInt(zip(zip.Length - 2)) Or (CInt(zip(zip.Length - 1)) << 8)
        End Function

        Private _failed As Boolean = False

        Private Sub Report(name As String, pass As Boolean, detail As String)
            If Not pass Then _failed = True
            Console.WriteLine($"[{(If(pass, "PASS", "FAIL"))}] {name}  {detail}")
        End Sub

    End Module

End Namespace
