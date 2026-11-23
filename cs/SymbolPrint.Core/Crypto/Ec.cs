using System;
using System.Security.Cryptography;
using SymbolPrint.Core.Util;

namespace SymbolPrint.Core.Crypto
{
    public enum EcCurveId : byte
    {
        NistP256 = 1,
        NistP384 = 2,
        NistP521 = 3
    }

    public sealed class EcKeys
    {
        public EcCurveId CurveId { get; set; }
        public byte[] PublicSpki { get; set; }
        public byte[] PrivatePkcs8 { get; set; }
    }

    public static class AesGcmUtil
    {
        public const int KeyLen = 32;
        public const int NonceLen = 12;
        public const int TagLen = 16;

        /// <summary>保护章私钥时用的 AAD，必须是 ASCII（与 VB 版 Bin.ConstBytes 一致）。</summary>
        private static readonly byte[] PrivateKeyAad = System.Text.Encoding.ASCII.GetBytes("SymbolPrint-PrivateKey-v1");

        public static byte[] NewKey() => Bin.RandomBytes(KeyLen);
        public static byte[] NewNonce() => Bin.RandomBytes(NonceLen);

        /// <summary>
        /// 加密，返回 (密文, 认证标签)。密钥 16/24/32 字节、tag 12–16、nonce ≥8，
        /// 与 VB 版同一套约束（这些由高级设置控制，不能在这里写死）。
        /// </summary>
        public static (byte[] Cipher, byte[] Tag) Encrypt(byte[] key, byte[] nonce, byte[] plain,
                                                          byte[] aad = null, int tagLen = TagLen)
        {
            if (key == null || (key.Length != 16 && key.Length != 24 && key.Length != 32))
                throw new ArgumentException("AES 密钥必须为 16 / 24 / 32 字节。", nameof(key));
            if (nonce == null || nonce.Length < 8) throw new ArgumentException("Nonce 至少 8 字节。", nameof(nonce));
            if (tagLen < 12 || tagLen > 16) throw new ArgumentException("认证标签长度必须在 12–16 字节之间。", nameof(tagLen));

            plain ??= Array.Empty<byte>();
            var cipher = new byte[plain.Length];
            var tag = new byte[tagLen];
            using var gcm = new AesGcm(key, tagLen);
            gcm.Encrypt(nonce, plain, cipher, tag, aad);
            return (cipher, tag);
        }

        /// <summary>解密；认证失败抛 CryptographicException。</summary>
        public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] cipher, byte[] tag, byte[] aad = null)
        {
            if (key == null || (key.Length != 16 && key.Length != 24 && key.Length != 32))
                throw new ArgumentException("AES 密钥必须为 16 / 24 / 32 字节。", nameof(key));
            if (nonce == null || nonce.Length < 8) throw new ArgumentException("Nonce 至少 8 字节。", nameof(nonce));
            if (tag == null || tag.Length < 12 || tag.Length > 16)
                throw new ArgumentException("认证标签长度必须在 12–16 字节之间。", nameof(tag));

            cipher ??= Array.Empty<byte>();
            var plain = new byte[cipher.Length];
            using var gcm = new AesGcm(key, tag.Length);
            gcm.Decrypt(nonce, cipher, tag, plain, aad);
            return plain;
        }

        /// <summary>用口令派生密钥后加密（保护章 .bin 内的 ECC 私钥）。</summary>
        public static byte[] SealWithPassword(byte[] plain, string password, int iterations,
                                              out byte[] salt, out byte[] nonce, out byte[] tag)
        {
            salt = Bin.RandomBytes(16);
            nonce = NewNonce();
            var key = Sha3Kdf.Pbkdf2(Bin.Utf8Bytes(password), salt, iterations, KeyLen);
            var r = Encrypt(key, nonce, plain, PrivateKeyAad);
            tag = r.Tag;
            return r.Cipher;
        }

        public static byte[] OpenWithPassword(byte[] cipher, string password, int iterations,
                                              byte[] salt, byte[] nonce, byte[] tag)
        {
            var key = Sha3Kdf.Pbkdf2(Bin.Utf8Bytes(password), salt, iterations, KeyLen);
            return Decrypt(key, nonce, cipher, tag, PrivateKeyAad);
        }
    }

    /// <summary>
    /// ECIES：ECDH（临时密钥 ↔ 接收方公钥）→ HKDF-SHA3-512 → AES-256-GCM。
    /// 信封：u8 ver | u8 curve | u8 pointLen | point | nonce(12) | tag(16) | cipher
    /// </summary>
    public static class Ecies
    {
        public const byte EnvelopeVersion = 1;
        private const string KdfInfo = "SymbolPrint-ECIES-v1";

        public static ECCurve CurveOf(EcCurveId id) => id switch
        {
            EcCurveId.NistP256 => ECCurve.NamedCurves.nistP256,
            EcCurveId.NistP384 => ECCurve.NamedCurves.nistP384,
            EcCurveId.NistP521 => ECCurve.NamedCurves.nistP521,
            _ => throw new ArgumentOutOfRangeException(nameof(id), "未知曲线。")
        };

        public static EcKeys GenerateKeys(EcCurveId curve = EcCurveId.NistP256)
        {
            using var ecdh = ECDiffieHellman.Create(CurveOf(curve));
            return new EcKeys
            {
                CurveId = curve,
                PrivatePkcs8 = ecdh.ExportPkcs8PrivateKey(),
                PublicSpki = ecdh.ExportSubjectPublicKeyInfo()
            };
        }

        private static byte[] PointOf(EcCurveId id, byte[] spki)
        {
            using var ecdh = ECDiffieHellman.Create();
            ecdh.ImportSubjectPublicKeyInfo(spki, out _);
            var p = ecdh.ExportParameters(false);
            var r = new byte[1 + p.Q.X.Length + p.Q.Y.Length];
            r[0] = 0x04;
            Buffer.BlockCopy(p.Q.X, 0, r, 1, p.Q.X.Length);
            Buffer.BlockCopy(p.Q.Y, 0, r, 1 + p.Q.X.Length, p.Q.Y.Length);
            return r;
        }

        private static byte[] DeriveAesKey(byte[] secret, byte[] ephPoint, byte[] peerPoint)
        {
            var salt = Bin.Concat(ephPoint, peerPoint);
            return Sha3Kdf.Hkdf(secret, salt, Bin.Utf8Bytes(KdfInfo), AesGcmUtil.KeyLen);
        }

        public static byte[] Encrypt(byte[] recipientSpki, byte[] plain, EcCurveId curve,
                                     byte[] ephPrivatePkcs8 = null, byte[] nonceOverride = null)
        {
            plain ??= Array.Empty<byte>();
            var recipPoint = PointOf(curve, recipientSpki);

            using var recip = ECDiffieHellman.Create();
            recip.ImportSubjectPublicKeyInfo(recipientSpki, out _);

            ECDiffieHellman eph;
            if (ephPrivatePkcs8 != null)
            {
                eph = ECDiffieHellman.Create();
                eph.ImportPkcs8PrivateKey(ephPrivatePkcs8, out _);
            }
            else
            {
                eph = ECDiffieHellman.Create(CurveOf(curve));
            }

            using (eph)
            {
                var ephPrm = eph.ExportParameters(true);
                var ephPoint = new byte[1 + ephPrm.Q.X.Length + ephPrm.Q.Y.Length];
                ephPoint[0] = 0x04;
                Buffer.BlockCopy(ephPrm.Q.X, 0, ephPoint, 1, ephPrm.Q.X.Length);
                Buffer.BlockCopy(ephPrm.Q.Y, 0, ephPoint, 1 + ephPrm.Q.X.Length, ephPrm.Q.Y.Length);

                var secret = eph.DeriveRawSecretAgreement(recip.PublicKey);
                var aesKey = DeriveAesKey(secret, ephPoint, recipPoint);
                var nonce = nonceOverride ?? AesGcmUtil.NewNonce();
                var (cipher, tag) = AesGcmUtil.Encrypt(aesKey, nonce, plain);

                var w = new ByteWriter();
                w.WriteU8(EnvelopeVersion);
                w.WriteU8((byte)curve);
                w.WriteU8((byte)ephPoint.Length);
                w.WriteBytes(ephPoint);
                w.WriteBytes(nonce);
                w.WriteBytes(tag);
                w.WriteBytes(cipher);
                return w.ToArray();
            }
        }

        public static byte[] Decrypt(byte[] privatePkcs8, byte[] envelope)
        {
            var r = new ByteReader(envelope);
            var ver = r.ReadU8();
            if (ver != EnvelopeVersion) throw new InvalidOperationException($"ECIES 信封版本 {ver} 不受支持。");
            var curve = (EcCurveId)r.ReadU8();
            int pointLen = r.ReadU8();
            var ephPoint = r.ReadBytes(pointLen);
            var nonce = r.ReadBytes(AesGcmUtil.NonceLen);
            var tag = r.ReadBytes(AesGcmUtil.TagLen);
            var cipher = r.ReadBytes(r.Remaining);

            using var me = ECDiffieHellman.Create();
            me.ImportPkcs8PrivateKey(privatePkcs8, out _);
            var mine = me.ExportParameters(false);
            var myPoint = new byte[1 + mine.Q.X.Length + mine.Q.Y.Length];
            myPoint[0] = 0x04;
            Buffer.BlockCopy(mine.Q.X, 0, myPoint, 1, mine.Q.X.Length);
            Buffer.BlockCopy(mine.Q.Y, 0, myPoint, 1 + mine.Q.X.Length, mine.Q.Y.Length);

            int half = (ephPoint.Length - 1) / 2;
            var x = new byte[half];
            var y = new byte[half];
            Buffer.BlockCopy(ephPoint, 1, x, 0, half);
            Buffer.BlockCopy(ephPoint, 1 + half, y, 0, half);
            var prm = new ECParameters { Curve = CurveOf(curve), Q = new ECPoint { X = x, Y = y } };
            using var other = ECDiffieHellman.Create(prm);

            var secret = me.DeriveRawSecretAgreement(other.PublicKey);
            var aesKey = DeriveAesKey(secret, ephPoint, myPoint);
            return AesGcmUtil.Decrypt(aesKey, nonce, cipher, tag);
        }
    }
}
