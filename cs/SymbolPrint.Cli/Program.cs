using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SymbolPrint.Core.Crypto;
using SymbolPrint.Core.Util;

namespace SymbolPrint.Cli
{
    /// <summary>
    /// C# 版命令行（当前阶段：一致性校验器）。
    /// 用途：读 VB 版生成的 vectors.json，重算并逐字节比对 —— 两边不一致，这里立刻红。
    /// </summary>
    public static class Program
    {
        private sealed class VectorCase
        {
            public string Name { get; set; }
            public string Kind { get; set; }
            public Dictionary<string, string> Input { get; set; } = new();
            public Dictionary<string, string> Expected { get; set; } = new();
        }

        private sealed class VectorsFile
        {
            public int Format { get; set; }
            public string Generator { get; set; }
            public string CreatedUtc { get; set; }
            public List<VectorCase> Cases { get; set; } = new();
        }

        public static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            if (args.Length == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("SymbolPrint CLI (C#) 0.1");
                Console.WriteLine("用法:");
                Console.WriteLine("  spcli-cs conformance [vectors.json]   与 VB 版逐字节比对一致性向量");
                return 0;
            }

            if (args[0].Equals("conformance", StringComparison.OrdinalIgnoreCase))
            {
                string path = args.Length > 1 ? args[1] : FindVectors();
                return Conformance(path);
            }

            Console.WriteLine($"未知命令: {args[0]}");
            return 2;
        }

        private static string FindVectors()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var p = Path.Combine(dir.FullName, "tests", "vectors", "crypto.json");
                if (File.Exists(p)) return p;
                dir = dir.Parent;
            }
            return Path.Combine(AppContext.BaseDirectory, "vectors.json");
        }

        private static int Conformance(string path)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"向量文件不存在：{path}");
                return 2;
            }

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var doc = JsonSerializer.Deserialize<VectorsFile>(File.ReadAllText(path), opts);

            Console.WriteLine($"向量文件：{path}");
            Console.WriteLine($"生成者：{doc.Generator}  {doc.CreatedUtc}");
            Console.WriteLine();

            int pass = 0, fail = 0, skip = 0;
            foreach (var c in doc.Cases)
            {
                Dictionary<string, string> actual;
                try
                {
                    actual = Compute(c);
                }
                catch (NotSupportedException)
                {
                    skip++;
                    Console.WriteLine($"[SKIP] {c.Name}（本阶段 C# 版尚未移植：{c.Kind}）");
                    continue;
                }
                catch (Exception ex)
                {
                    fail++;
                    Console.WriteLine($"[FAIL] {c.Name}  异常 {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                var diffs = new List<string>();
                foreach (var kv in c.Expected)
                {
                    if (!actual.TryGetValue(kv.Key, out var got)) diffs.Add($"{kv.Key}: 缺少输出");
                    else if (!string.Equals(got, kv.Value, StringComparison.Ordinal))
                        diffs.Add($"{kv.Key}: 期望 {Brief(kv.Value)} 实际 {Brief(got)}");
                }

                if (diffs.Count == 0) { pass++; Console.WriteLine($"[PASS] {c.Name}"); }
                else { fail++; Console.WriteLine($"[FAIL] {c.Name}  {string.Join(" | ", diffs)}"); }
            }

            Console.WriteLine();
            Console.WriteLine($"通过 {pass} / 失败 {fail} / 跳过 {skip}");
            return fail == 0 ? 0 : 1;
        }

        private static string Brief(string hex)
        {
            if (hex == null) return "(null)";
            return hex.Length <= 24 ? hex : hex.Substring(0, 24) + "…";
        }

        private static Dictionary<string, string> Compute(VectorCase c)
        {
            var r = new Dictionary<string, string>(StringComparer.Ordinal);
            switch (c.Kind)
            {
                case "sha3_512":
                    r["digest"] = Bin.Hex(Sha3Kdf.Hash512(Bin.FromHex(c.Input["data"])));
                    break;

                case "hmac_sha3_512":
                    r["mac"] = Bin.Hex(Sha3Kdf.HmacSha3_512(Bin.FromHex(c.Input["key"]), Bin.FromHex(c.Input["message"])));
                    break;

                case "hkdf":
                    r["okm"] = Bin.Hex(Sha3Kdf.Hkdf(Bin.FromHex(c.Input["ikm"]), Bin.FromHex(c.Input["salt"]),
                                                    Bin.FromHex(c.Input["info"]), int.Parse(c.Input["len"])));
                    break;

                case "pbkdf2":
                    r["dk"] = Bin.Hex(Sha3Kdf.Pbkdf2(Bin.FromHex(c.Input["password"]), Bin.FromHex(c.Input["salt"]),
                                                     int.Parse(c.Input["iterations"]), int.Parse(c.Input["len"])));
                    break;

                case "aes_gcm":
                    var (cipher, tag) = AesGcmUtil.Encrypt(Bin.FromHex(c.Input["key"]), Bin.FromHex(c.Input["nonce"]),
                                                           Bin.FromHex(c.Input["plain"]));
                    r["cipher"] = Bin.Hex(cipher);
                    r["tag"] = Bin.Hex(tag);
                    break;

                case "ecies":
                    var env = Ecies.Encrypt(Bin.FromHex(c.Input["recipientSpki"]), Bin.FromHex(c.Input["plain"]),
                                            (EcCurveId)int.Parse(c.Input["curve"]),
                                            Bin.FromHex(c.Input["ephPriv"]), Bin.FromHex(c.Input["nonce"]));
                    r["envelope"] = Bin.Hex(env);
                    break;

                default:
                    throw new NotSupportedException(c.Kind);
            }
            return r;
        }
    }
}
