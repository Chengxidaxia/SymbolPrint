using System;
using System.IO;
using SymbolPrint.Core.Crypto;
using SymbolPrint.Core.Util;

namespace SymbolPrint.Core.Model
{
    /// <summary>
    /// 盖章记录（写入宿主文件空白区的原始内容）。必须与 VB 版逐字节一致。
    ///
    /// 版本 1 布局（小端）：
    ///   u8   sealVersion
    ///   u8   sealFlags          bit0 视觉章已烧入（图片/PDF）
    ///   u16  headerSize = 96
    ///   u64  stampTimeMs
    ///   [64] ownContentSha3     本记录自身内容的 SHA3-512
    ///   [32] key1               加密节1 的密钥
    ///   [12] nonce1
    ///   [16] tag1
    ///   u32  section1Len
    ///   ...  section1Cipher     AES-GCM(key1) 密文
    ///
    /// 版本 2 在头部插入了各参数长度与 HKDF info，便于参数可调。
    /// </summary>
    public sealed class SealRecord
    {
        /// <summary>v1 的固定布局（老记录）。仍需能解析，否则已经盖过章的文件全部验不过。</summary>
        public const byte Version = 1;
        /// <summary>当前写出的版本。</summary>
        public const byte CurrentVersion = 2;
        public const int HeaderSize = 96;
        public const int Pbkdf2Len = 32;

        public DateTimeOffset StampTime { get; set; } = DateTimeOffset.Now;
        public bool VisualBurned { get; set; }
        public byte[] Key1 { get; set; }
        public byte[] Nonce1 { get; set; }
        public byte[] Tag1 { get; set; }
        public byte[] Section1Cipher { get; set; }
        public byte[] OwnContentSha3 { get; set; }

        // ---- v2 才有的长度描述；v1 记录解析后保持默认值 ----
        public byte RecordVer { get; set; } = CurrentVersion;
        public int SaltLen { get; set; } = 16;
        public int Key1Len { get; set; } = 32;
        public int NonceLen { get; set; } = 12;
        public int TagLen { get; set; } = 16;
        public int Pbkdf2LenUsed { get; set; } = Pbkdf2Len;
        public string HkdfInfo { get; set; } = "SymbolPrint-ECIES-v1";

        /// <summary>把本记录序列化为最终写入宿主文件的原始字节。</summary>
        public byte[] Serialize()
        {
            var w = new ByteWriter();
            if (RecordVer >= 2)
            {
                var info = Bin.Utf8Bytes(HkdfInfo ?? "");
                w.WriteU8(CurrentVersion);
                w.WriteU8((byte)(VisualBurned ? 1 : 0));
                w.WriteU8((byte)Key1Len);
                w.WriteU8((byte)NonceLen);
                w.WriteU8((byte)TagLen);
                w.WriteU8((byte)Pbkdf2LenUsed);
                w.WriteU8((byte)SaltLen);
                w.WriteU8((byte)Math.Min(255, info.Length));
                w.WriteBytes(info);
                w.WriteU16((ushort)HeaderSize);
                w.WriteU64((ulong)StampTime.ToUnixTimeMilliseconds());
                w.WriteBytes(OwnContentSha3);
                w.WriteBytes(Key1);
                w.WriteBytes(Nonce1);
                w.WriteBytes(Tag1);
                w.WriteU32((uint)Section1Cipher.Length);
                w.WriteBytes(Section1Cipher);
                return w.ToArray();
            }

            // v1：保持原样，不要动（旧向量要能复现）
            w.WriteU8(Version);
            w.WriteU8((byte)(VisualBurned ? 1 : 0));
            w.WriteU16((ushort)HeaderSize);
            w.WriteU64((ulong)StampTime.ToUnixTimeMilliseconds());
            w.WriteBytes(OwnContentSha3);
            w.WriteBytes(Key1);
            w.WriteBytes(Nonce1);
            w.WriteBytes(Tag1);
            w.WriteU32((uint)Section1Cipher.Length);
            w.WriteBytes(Section1Cipher);
            return w.ToArray();
        }

        /// <summary>计算 ownContentSha3：覆盖版本/标志/长度描述/时间戳/密钥/nonce/tag/密文。</summary>
        public void SealOwnHash()
        {
            OwnContentSha3 = Sha3Kdf.Hash512(OwnHashInput());
        }

        /// <summary>ownContentSha3 覆盖的字节；v1 与 v2 布局不同，必须按记录版本各算各的。</summary>
        private byte[] OwnHashInput()
        {
            var w = new ByteWriter();
            if (RecordVer >= 2)
            {
                var info = Bin.Utf8Bytes(HkdfInfo ?? "");
                w.WriteU8(CurrentVersion);
                w.WriteU8((byte)(VisualBurned ? 1 : 0));
                w.WriteU8((byte)Key1Len);
                w.WriteU8((byte)NonceLen);
                w.WriteU8((byte)TagLen);
                w.WriteU8((byte)Pbkdf2LenUsed);
                w.WriteU8((byte)SaltLen);
                w.WriteU8((byte)Math.Min(255, info.Length));
                w.WriteBytes(info);
                w.WriteU64((ulong)StampTime.ToUnixTimeMilliseconds());
                w.WriteBytes(Key1);
                w.WriteBytes(Nonce1);
                w.WriteBytes(Tag1);
                w.WriteBytes(Section1Cipher);
                return w.ToArray();
            }

            w.WriteU8(Version);
            w.WriteU8((byte)(VisualBurned ? 1 : 0));
            w.WriteU64((ulong)StampTime.ToUnixTimeMilliseconds());
            w.WriteBytes(Key1);
            w.WriteBytes(Nonce1);
            w.WriteBytes(Tag1);
            w.WriteBytes(Section1Cipher);
            return w.ToArray();
        }

        public static SealRecord Parse(byte[] raw)
        {
            var r = new ByteReader(raw);
            var ver = r.ReadU8();
            if (ver != Version && ver != CurrentVersion)
            {
                throw new InvalidDataException($"不支持的盖章记录版本：{ver}（本程序支持 1 / {CurrentVersion}）。");
            }

            var rec = new SealRecord { RecordVer = ver };

            if (ver >= 2)
            {
                var flags2 = r.ReadU8();
                rec.Key1Len = r.ReadU8();
                rec.NonceLen = r.ReadU8();
                rec.TagLen = r.ReadU8();
                rec.Pbkdf2LenUsed = r.ReadU8();
                rec.SaltLen = r.ReadU8();
                int infoLen = r.ReadU8();
                rec.HkdfInfo = infoLen > 0 ? Bin.Utf8(r.ReadBytes(infoLen)) : "";

                var hs2 = r.ReadU16();
                if (hs2 != HeaderSize) throw new InvalidDataException($"盖章记录头长度异常：{hs2}");
                rec.VisualBurned = (flags2 & 1) != 0;
                rec.StampTime = DateTimeOffset.FromUnixTimeMilliseconds((long)r.ReadU64());
                rec.OwnContentSha3 = r.ReadBytes(64);
                rec.Key1 = r.ReadBytes(Math.Max(1, rec.Key1Len));
                rec.Nonce1 = r.ReadBytes(Math.Max(1, rec.NonceLen));
                rec.Tag1 = r.ReadBytes(Math.Max(1, rec.TagLen));
                int slen2 = (int)r.ReadU32();
                rec.Section1Cipher = r.ReadBytes(slen2);
                return rec;
            }

            var flags = r.ReadU8();
            var hs = r.ReadU16();
            if (hs != HeaderSize) throw new InvalidDataException($"盖章记录头长度异常：{hs}");
            rec.StampTime = DateTimeOffset.FromUnixTimeMilliseconds((long)r.ReadU64());
            rec.VisualBurned = (flags & 1) != 0;
            rec.OwnContentSha3 = r.ReadBytes(64);
            rec.Key1 = r.ReadBytes(32);
            rec.Nonce1 = r.ReadBytes(12);
            rec.Tag1 = r.ReadBytes(16);
            int slen = (int)r.ReadU32();
            rec.Section1Cipher = r.ReadBytes(slen);
            return rec;
        }

        /// <summary>校验 ownContentSha3 是否与内容一致。</summary>
        public bool VerifyOwnHash()
        {
            if (OwnContentSha3 == null || OwnContentSha3.Length != 64) return false;
            return Bin.Same(Sha3Kdf.Hash512(OwnHashInput()), OwnContentSha3);
        }
    }
}
