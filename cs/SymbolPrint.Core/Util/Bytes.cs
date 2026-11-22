using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SymbolPrint.Core.Util
{
    /// <summary>字节工具（与 VB 版逐字节等价：全部小端）。</summary>
    public static class Bin
    {
        public static byte[] Utf8Bytes(string s) => s == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(s);
        public static string Utf8(byte[] b) => b == null ? "" : Encoding.UTF8.GetString(b);
        public static string Hex(byte[] b) => b == null ? "" : Convert.ToHexString(b);
        public static byte[] FromHex(string s)
        {
            if (string.IsNullOrEmpty(s)) return Array.Empty<byte>();
            return Convert.FromHexString(s.Replace(" ", "").Replace("-", ""));
        }
        public static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null) return ReferenceEquals(a, b);
            if (a.Length != b.Length) return false;
            return CryptographicOperations.FixedTimeEquals(a, b);
        }
        public static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            RandomNumberGenerator.Fill(b);
            return b;
        }
        public static byte[] Concat(params byte[][] parts)
        {
            int total = 0;
            foreach (var p in parts) if (p != null) total += p.Length;
            var r = new byte[total];
            int off = 0;
            foreach (var p in parts)
            {
                if (p == null) continue;
                Buffer.BlockCopy(p, 0, r, off, p.Length);
                off += p.Length;
            }
            return r;
        }
    }

    public sealed class ByteWriter
    {
        private readonly MemoryStream _ms = new MemoryStream();
        public void WriteU8(byte v) => _ms.WriteByte(v);
        public void WriteU16(ushort v)
        {
            _ms.WriteByte((byte)(v & 0xFF));
            _ms.WriteByte((byte)((v >> 8) & 0xFF));
        }
        public void WriteU32(uint v)
        {
            for (int i = 0; i < 4; i++) _ms.WriteByte((byte)((v >> (8 * i)) & 0xFF));
        }
        public void WriteU64(ulong v)
        {
            for (int i = 0; i < 8; i++) _ms.WriteByte((byte)((v >> (8 * i)) & 0xFF));
        }
        public void WriteBytes(byte[] b)
        {
            if (b != null && b.Length > 0) _ms.Write(b, 0, b.Length);
        }
        public long Length => _ms.Length;
        public byte[] ToArray() => _ms.ToArray();
    }

    public sealed class ByteReader
    {
        private readonly byte[] _buf;
        private int _pos;
        private readonly int _end;

        public ByteReader(byte[] buf) : this(buf, 0, buf?.Length ?? 0) { }

        public ByteReader(byte[] buf, int offset, int count)
        {
            if (buf == null) throw new ArgumentNullException(nameof(buf));
            if (offset < 0 || count < 0 || offset + count > buf.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            _buf = buf;
            _pos = offset;
            _end = offset + count;
        }

        public int Position => _pos;
        public int Remaining => _end - _pos;

        private void Need(int n)
        {
            if (_pos + n > _end) throw new EndOfStreamException($"数据不足：位于 {_pos} 处还需要 {n} 字节，但只剩 {_end - _pos} 字节。");
        }

        public byte[] ReadBytes(int n)
        {
            if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
            Need(n);
            var r = new byte[n];
            if (n > 0) Buffer.BlockCopy(_buf, _pos, r, 0, n);
            _pos += n;
            return r;
        }

        public byte ReadU8() { Need(1); return _buf[_pos++]; }

        public ushort ReadU16()
        {
            Need(2);
            int v = _buf[_pos] | (_buf[_pos + 1] << 8);
            _pos += 2;
            return (ushort)v;
        }

        public uint ReadU32()
        {
            Need(4);
            uint v = 0;
            for (int i = 0; i < 4; i++) v |= (uint)_buf[_pos + i] << (8 * i);
            _pos += 4;
            return v;
        }

        public ulong ReadU64()
        {
            Need(8);
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)_buf[_pos + i] << (8 * i);
            _pos += 8;
            return v;
        }
    }
}
