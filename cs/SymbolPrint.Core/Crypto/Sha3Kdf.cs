using System;
using System.IO;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;
using SymbolPrint.Core.Util;

namespace SymbolPrint.Core.Crypto
{
    /// <summary>
    /// SHA3-512 / HMAC-SHA3-512 / HKDF / PBKDF2。
    /// 与 VB 版严格等价（同一 BouncyCastle 实现），任何字节差异都会被一致性向量抓到。
    /// </summary>
    public static class Sha3Kdf
    {
        public const int HashLen = 64;
        public const int HmacBlockLen = 72;

        public static byte[] Hash512(byte[] data)
        {
            var d = new Sha3Digest(512);
            if (data != null && data.Length > 0) d.BlockUpdate(data, 0, data.Length);
            var outp = new byte[HashLen];
            d.DoFinal(outp, 0);
            return outp;
        }

        public static byte[] HashFile(string path, IProgressSink sink = null, string stage = "")
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return HashStream(fs, fs.Length, sink, stage);
        }

        public static byte[] HashStream(Stream input, long totalBytes, IProgressSink sink = null, string stage = "")
        {
            string name = string.IsNullOrEmpty(stage) ? "计算 SHA3-512" : stage;
            var digest = new Sha3Digest(512);
            var buf = new byte[1 << 20];
            long done = 0;
            ProgressExt.Push(sink, name + "…", -1.0);
            while (true)
            {
                ProgressExt.ThrowIfCancelled(sink);
                int n = input.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                digest.BlockUpdate(buf, 0, n);
                done += n;
                ProgressExt.Push(sink, name, totalBytes > 0 ? done / (double)totalBytes : -1.0,
                                 ProgressExt.BytesDetail(done, totalBytes));
            }
            var outp = new byte[HashLen];
            digest.DoFinal(outp, 0);
            ProgressExt.Push(sink, name, 1.0);
            return outp;
        }

        public static byte[] HmacSha3_512(byte[] key, byte[] message)
        {
            var mac = new HMac(new Sha3Digest(512));
            mac.Init(new KeyParameter(key ?? Array.Empty<byte>()));
            if (message != null && message.Length > 0) mac.BlockUpdate(message, 0, message.Length);
            var outp = new byte[HashLen];
            mac.DoFinal(outp, 0);
            return outp;
        }

        private static byte[] MacOf(HMac mac, byte[] data, int offset, int count)
        {
            if (count > 0) mac.BlockUpdate(data, offset, count);
            var o = new byte[HashLen];
            mac.DoFinal(o, 0);
            mac.Reset();
            return o;
        }

        public static byte[] Pbkdf2(byte[] password, byte[] salt, int iterations, int derivedLen,
                                    IProgressSink sink = null, string stage = "")
        {
            if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations));
            if (derivedLen < 1) throw new ArgumentOutOfRangeException(nameof(derivedLen));
            salt ??= Array.Empty<byte>();

            var mac = new HMac(new Sha3Digest(512));
            mac.Init(new KeyParameter(password ?? Array.Empty<byte>()));

            int blocks = (int)Math.Ceiling(derivedLen / (double)HashLen);
            var outp = new byte[derivedLen];
            int produced = 0;
            string name = string.IsNullOrEmpty(stage) ? "派生密钥（PBKDF2）" : stage;

            long totalSteps = (long)blocks * iterations;
            long reportStep = Math.Max(1L, totalSteps / 200L);
            long stepsDone = 0;
            long nextReport = 0;

            ProgressExt.Push(sink, name, 0.0, $"0 / {totalSteps} 次迭代");

            // 注意：VB 的 Dim a(n) 是"上界 n"，长度 n+1；C# 的 new byte[n] 是"长度 n"。
            // 下面要写索引 salt.Length..salt.Length+3，所以长度必须是 +4（这里曾因 +3 越界）。
            var saltBlock = new byte[salt.Length + 4];
            Buffer.BlockCopy(salt, 0, saltBlock, 0, salt.Length);

            for (int blk = 1; blk <= blocks; blk++)
            {
                saltBlock[salt.Length] = (byte)((blk >> 24) & 0xFF);
                saltBlock[salt.Length + 1] = (byte)((blk >> 16) & 0xFF);
                saltBlock[salt.Length + 2] = (byte)((blk >> 8) & 0xFF);
                saltBlock[salt.Length + 3] = (byte)(blk & 0xFF);

                var u = MacOf(mac, saltBlock, 0, saltBlock.Length);
                var t = new byte[HashLen];
                Buffer.BlockCopy(u, 0, t, 0, HashLen);
                stepsDone++;

                for (int it = 2; it <= iterations; it++)
                {
                    u = MacOf(mac, u, 0, u.Length);
                    for (int j = 0; j < HashLen; j++) t[j] ^= u[j];
                    stepsDone++;
                    if (stepsDone >= nextReport)
                    {
                        ProgressExt.ThrowIfCancelled(sink);
                        ProgressExt.Push(sink, name, stepsDone / (double)totalSteps, $"{stepsDone} / {totalSteps} 次迭代");
                        nextReport = stepsDone + reportStep;
                    }
                }

                int take = Math.Min(HashLen, derivedLen - produced);
                Buffer.BlockCopy(t, 0, outp, produced, take);
                produced += take;
            }

            ProgressExt.Push(sink, name, 1.0);
            return outp;
        }

        public static byte[] Hkdf(byte[] ikm, byte[] salt, byte[] info, int outputLen)
        {
            if (salt == null || salt.Length == 0) salt = new byte[64];
            var prk = HmacSha3_512(salt, ikm);
            var mac = new HMac(new Sha3Digest(512));
            mac.Init(new KeyParameter(prk));

            var okm = new byte[outputLen];
            var t = Array.Empty<byte>();
            int produced = 0;
            byte counter = 1;

            while (produced < outputLen)
            {
                var parts = new System.Collections.Generic.List<byte[]>();
                if (t.Length > 0) parts.Add(t);
                if (info != null && info.Length > 0) parts.Add(info);
                parts.Add(new[] { counter });
                var blockInput = Bin.Concat(parts.ToArray());
                t = MacOf(mac, blockInput, 0, blockInput.Length);
                int take = Math.Min(t.Length, outputLen - produced);
                Buffer.BlockCopy(t, 0, okm, produced, take);
                produced += take;
                counter++;
            }
            return okm;
        }
    }
}
