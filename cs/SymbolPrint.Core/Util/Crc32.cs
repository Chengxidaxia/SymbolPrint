using System;

namespace SymbolPrint.Core.Util
{
    /// <summary>
    /// CRC-32（IEEE 802.3，多项式 0xEDB88320），用于章文件的内部完整性快检。
    /// 与 VB 版逐值一致：同表、同初值 0xFFFFFFFF、同末尾取反。
    /// </summary>
    public static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
                }
                t[i] = c;
            }
            return t;
        }

        public static uint Compute(byte[] data, int offset = 0, int count = -1)
        {
            if (data == null) return 0u;
            if (count < 0) count = data.Length - offset;
            uint c = 0xFFFFFFFFu;
            for (int i = offset; i < offset + count; i++)
            {
                c = Table[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            }
            return c ^ 0xFFFFFFFFu;
        }
    }
}
