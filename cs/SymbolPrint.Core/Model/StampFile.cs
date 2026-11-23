using System;
using System.IO;
using SymbolPrint.Core.Crypto;
using SymbolPrint.Core.Util;

namespace SymbolPrint.Core.Model
{
    public static class StampTags
    {
        public const byte Label = 0x1;
        public const byte SealBytes = 0x2;
        public const byte Description = 0x3;
        public const byte Svg = 0x4;
        public const byte CreatedAt = 0x5;
        public const byte PublicKey = 0x6;
        public const byte PrivateKey = 0x7;
        public const byte Curve = 0x8;
        public const byte KeyProtection = 0x9;
        public const byte KdfSalt = 0xA;
        public const byte KdfIterations = 0xB;
        public const byte EncNonce = 0xC;
        public const byte EncTag = 0xD;
        public const byte EncPrivateKey = 0xE;
        public const byte Generator = 0xF;
    }

    /// <summary>
    /// 章文件（.bin）。必须与 VB 版逐字节一致。结构（小端）：
    ///   [0,128)          固定文件头
    ///   [128,128+65536)  自指空白区（正常全 0；自章数据对齐写在末尾）
    ///   [65664, ...)     TLV 正文
    ///
    /// 头内偏移：0 魔数(8) / 8 版本(2) / 10 标志(2) / 12 头长(4) / 16 空白区偏移(8) /
    /// 24 空白区长度(8) / 32 正文偏移(8) / 40 正文长度(8) / 48 创建时间(8) / 56 保留(8) /
    /// 64 章ID(32) / 96 正文CRC(4) / 100 头CRC(4) / 104 保留(24)
    /// </summary>
    public sealed class StampFile
    {
        public const ushort FormatVersion = 1;
        public const int HeaderSize = 128;
        public const int ReservedSize = 65536;
        public const long BodyOffset = HeaderSize + ReservedSize;
        public const int DefaultKdfIterations = 40000;

        public static readonly byte[] Magic = { 0x53, 0x50, 0x53, 0x54, 0x41, 0x4D, 0x50, 0x1A };

        // ---- 内容 ----
        public string Label { get; set; } = "";
        public string Description { get; set; } = "";
        public string SvgMarkup { get; set; } = "";
        public byte[] SealRawBytes { get; set; } = Array.Empty<byte>();
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        public EcKeys Keys { get; set; }
        public bool PassphraseProtected { get; set; }
        public byte[] KdfSalt { get; set; }
        public int KdfIterations { get; set; } = DefaultKdfIterations;
        public byte[] EncNonce { get; set; }
        public byte[] EncTag { get; set; }
        public byte[] EncPrivate { get; set; }
        public byte[] FileStampId { get; set; }

        public string SourcePath { get; set; } = "";
        private byte[] _privatePlain;

        public StampFile()
        {
            Keys = Ecies.GenerateKeys(EcCurveId.NistP256);
        }

        public bool HasSvg => !string.IsNullOrWhiteSpace(SvgMarkup);
        public bool HasSealBytes => SealRawBytes != null && SealRawBytes.Length > 0;
        public bool IsLocked => PassphraseProtected && _privatePlain == null;

        /// <summary>章身份 = SHA3-512(公钥 SPKI)。</summary>
        public byte[] StampId => Sha3Kdf.Hash512(Keys.PublicSpki);

        // ================= 序列化 =================

        private static void Tlv(ByteWriter w, byte tag, byte[] data)
        {
            data ??= Array.Empty<byte>();
            w.WriteU8(tag);
            w.WriteU32((uint)data.Length);
            w.WriteBytes(data);
        }

        private static byte[] U64Of(ulong v)
        {
            var w = new ByteWriter();
            w.WriteU64(v);
            return w.ToArray();
        }

        private static byte[] U32Of(uint v)
        {
            var w = new ByteWriter();
            w.WriteU32(v);
            return w.ToArray();
        }

        public byte[] BuildBody()
        {
            var w = new ByteWriter();
            Tlv(w, StampTags.Label, Bin.Utf8Bytes(Label));
            Tlv(w, StampTags.Description, Bin.Utf8Bytes(Description));
            if (HasSvg) Tlv(w, StampTags.Svg, Bin.Utf8Bytes(SvgMarkup));
            if (HasSealBytes) Tlv(w, StampTags.SealBytes, SealRawBytes);
            Tlv(w, StampTags.CreatedAt, U64Of((ulong)CreatedAt.ToUnixTimeMilliseconds()));
            Tlv(w, StampTags.PublicKey, Keys.PublicSpki);
            Tlv(w, StampTags.Curve, Bin.Utf8Bytes(CurveNameOf(Keys.CurveId)));
            Tlv(w, StampTags.KeyProtection, new[] { PassphraseProtected ? (byte)1 : (byte)0 });
            if (PassphraseProtected)
            {
                Tlv(w, StampTags.KdfSalt, KdfSalt);
                Tlv(w, StampTags.KdfIterations, U32Of((uint)KdfIterations));
                Tlv(w, StampTags.EncNonce, EncNonce);
                Tlv(w, StampTags.EncTag, EncTag);
                Tlv(w, StampTags.EncPrivateKey, EncPrivate);
            }
            else
            {
                Tlv(w, StampTags.PrivateKey, Keys.PrivatePkcs8);
            }
            Tlv(w, StampTags.Generator, Bin.Utf8Bytes("SymbolPrint/1.0"));
            return w.ToArray();
        }

        public void Save(string targetPath)
        {
            var body = BuildBody();
            ushort flags = 0;
            if (HasSvg) flags |= 1;
            if (HasSealBytes) flags |= 2;
            if (PassphraseProtected) flags |= 4;

            var sid = StampId;
            var bw = new ByteWriter();
            bw.WriteBytes(Magic);                                        // 8
            bw.WriteU16(FormatVersion);                                  // 10
            bw.WriteU16(flags);                                          // 12
            bw.WriteU32((uint)HeaderSize);                               // 16
            bw.WriteU64((ulong)HeaderSize);                              // 24
            bw.WriteU64((ulong)ReservedSize);                            // 32
            bw.WriteU64((ulong)BodyOffset);                              // 40
            bw.WriteU64((ulong)body.Length);                             // 48
            bw.WriteU64((ulong)CreatedAt.ToUnixTimeMilliseconds());      // 56
            bw.WriteU64(0UL);                                            // 64
            bw.WriteBytes(new byte[32]);                                 // 96
            bw.WriteU32(Crc32.Compute(body));                            // 100
            bw.WriteU32(0u);                                             // 104
            bw.WriteBytes(new byte[24]);                                 // 128
            var hdr = bw.ToArray();
            if (hdr.Length != HeaderSize)
            {
                throw new InvalidOperationException($"章文件头长度异常：{hdr.Length}（应为 {HeaderSize}）。");
            }

            Buffer.BlockCopy(sid, 0, hdr, 64, 32);
            var crcBytes = U32Of(Crc32.Compute(hdr));
            Buffer.BlockCopy(crcBytes, 0, hdr, 100, 4);

            // 自指空白区：目标已存在时沿用旧内容（自章数据写在末尾），否则全 0
            var reserved = new byte[ReservedSize];
            if (File.Exists(targetPath))
            {
                try
                {
                    if (new FileInfo(targetPath).Length >= BodyOffset)
                    {
                        var old = File.ReadAllBytes(targetPath);
                        Buffer.BlockCopy(old, HeaderSize, reserved, 0, ReservedSize);
                    }
                }
                catch
                {
                    // 旧文件读不出来就按全 0 处理，不影响写出
                }
            }

            var dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(hdr, 0, hdr.Length);
                fs.Write(reserved, 0, reserved.Length);
                fs.Write(body, 0, body.Length);
            }

            SourcePath = targetPath;
            FileStampId = sid;
            _privatePlain = PassphraseProtected ? null : Keys.PrivatePkcs8;
        }

        public static StampFile Load(string sourcePath, string password = null)
        {
            var bytes = File.ReadAllBytes(sourcePath);
            var doc = Parse(bytes);
            doc.SourcePath = sourcePath;
            if (doc.PassphraseProtected && password != null) doc.Unlock(password);
            return doc;
        }

        public static StampFile Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length < HeaderSize)
                throw new InvalidDataException("文件太小，不是有效的章文件。");

            var r = new ByteReader(bytes);
            var m = r.ReadBytes(Magic.Length);
            if (!Bin.Same(m, Magic))
                throw new InvalidDataException("魔数不匹配：这不是 SymbolPrint 章文件（.bin）。");
            var ver = r.ReadU16();
            if (ver > FormatVersion)
                throw new InvalidDataException($"章文件版本 {ver} 高于本程序支持的 {FormatVersion}，请升级 SymbolPrint。");
            r.ReadU16();
            r.ReadU32();
            var reservedOffset = r.ReadU64();
            var reservedSize = r.ReadU64();
            var bodyOffset = r.ReadU64();
            var bodySize = r.ReadU64();
            var createdMs = r.ReadU64();
            r.ReadU64();
            var sid = r.ReadBytes(32);
            var bodyCrc = r.ReadU32();

            if (reservedOffset != HeaderSize || reservedSize != ReservedSize)
                throw new InvalidDataException("章文件的空白区参数与当前版本不一致，无法解析。");
            if (bodyOffset + bodySize > (ulong)bytes.LongLength)
                throw new InvalidDataException("章文件正文长度越界，文件可能已被截断。");

            var body = new byte[(int)bodySize];
            Buffer.BlockCopy(bytes, (int)bodyOffset, body, 0, (int)bodySize);
            if (Crc32.Compute(body) != bodyCrc)
                throw new InvalidDataException("章文件正文 CRC 校验失败，文件已损坏。");

            var doc = new StampFile
            {
                FileStampId = sid,
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)createdMs),
                Keys = new EcKeys()
            };

            var curveName = "nistP256";
            byte prot = 0;
            var br = new ByteReader(body);
            while (br.Remaining > 0)
            {
                var tag = br.ReadU8();
                var len = (int)br.ReadU32();
                var data = br.ReadBytes(len);
                switch (tag)
                {
                    case StampTags.Label: doc.Label = Bin.Utf8(data); break;
                    case StampTags.Description: doc.Description = Bin.Utf8(data); break;
                    case StampTags.Svg: doc.SvgMarkup = Bin.Utf8(data); break;
                    case StampTags.SealBytes: doc.SealRawBytes = data; break;
                    case StampTags.PublicKey: doc.Keys.PublicSpki = data; break;
                    case StampTags.Curve: curveName = Bin.Utf8(data); break;
                    case StampTags.KeyProtection:
                        if (data.Length > 0) prot = data[0];
                        break;
                    case StampTags.KdfSalt: doc.KdfSalt = data; break;
                    case StampTags.KdfIterations:
                        doc.KdfIterations = (int)new ByteReader(data).ReadU32();
                        break;
                    case StampTags.EncNonce: doc.EncNonce = data; break;
                    case StampTags.EncTag: doc.EncTag = data; break;
                    case StampTags.EncPrivateKey: doc.EncPrivate = data; break;
                    case StampTags.PrivateKey:
                        doc.Keys.PrivatePkcs8 = data;
                        doc._privatePlain = data;
                        break;
                }
            }

            doc.Keys.CurveId = ParseCurveName(curveName);
            doc.PassphraseProtected = prot == 1;
            if (!doc.PassphraseProtected) doc._privatePlain = doc.Keys.PrivatePkcs8;
            if (doc.Keys.PublicSpki == null) throw new InvalidDataException("章文件缺少公钥。");
            return doc;
        }

        /// <summary>解锁受口令保护的章；口令错误抛 InvalidOperationException。</summary>
        public void Unlock(string password)
        {
            if (!PassphraseProtected) return;
            try
            {
                _privatePlain = AesGcmUtil.OpenWithPassword(EncPrivate, password, KdfIterations, KdfSalt, EncNonce, EncTag);
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                throw new InvalidOperationException("口令不正确（私钥解密失败）。", ex);
            }
        }

        public static bool HasMagic(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < Magic.Length) return false;
                var b = new byte[Magic.Length];
                fs.Read(b, 0, b.Length);
                return Bin.Same(b, Magic);
            }
            catch
            {
                return false;
            }
        }

        // ================= 曲线名（与 VB 版 CurvenameOf/ParseCurveName 同一套字符串） =================

        private static string CurveNameOf(EcCurveId id) => id switch
        {
            EcCurveId.NistP256 => "nistP256",
            EcCurveId.NistP384 => "nistP384",
            EcCurveId.NistP521 => "nistP521",
            _ => "unknown"
        };

        private static EcCurveId ParseCurveName(string name)
        {
            var s = (name ?? "").Trim().ToLowerInvariant();
            return s switch
            {
                "nistp256" or "p-256" or "p256" or "secp256r1" or "prime256v1" => EcCurveId.NistP256,
                "nistp384" or "p-384" or "p384" or "secp384r1" => EcCurveId.NistP384,
                "nistp521" or "p-521" or "p521" or "secp521r1" => EcCurveId.NistP521,
                _ => EcCurveId.NistP256
            };
        }
    }
}
