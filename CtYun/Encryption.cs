using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace CtYun
{
    public class Encryption
    {
        // 1. 使用 Memory 存储，但计算时优先用 Span
        private readonly List<Memory<byte>> _buffers = new();
        public uint AuthMechanism { get; set; } = 1;

        public byte[] Execute(byte[] key)
        {
            // 第一步：处理数据
            ResolveInboundData(key);

            // 第二步：提取公钥 (n 和 e)
            var (n, eValue) = GetPublicKey();

            // 第三步：RSA-OAEP 填充与加密
            byte[] encrypted = L(128, "", n, eValue);

            // 第四步：封装报文
            return ToBuffer(encrypted);
        }
        private void ResolveInboundData(byte[] data)
        {
            // 现代写法：直接切片，不产生拷贝
            _buffers.Add(data.AsMemory(16));
        }
        private (BigInteger N, int E) GetPublicKey()
        {
            // 假设 buffers[0] 的 32 字节开始是公钥 N
            ReadOnlySpan<byte> nSource = _buffers[0].Span.Slice(32, 129);

            // BigInteger 构造函数：isUnsigned 确保不被当成负数，isBigEndian 符合 JS 原逻辑
            var n = new BigInteger(nSource, isUnsigned: true, isBigEndian: true);

            // 提取 24 位指数 E
            ReadOnlySpan<byte> eSource = _buffers[0].Span.Slice(163, 3);
            int e = (eSource[0] << 16) | (eSource[1] << 8) | eSource[2];

            return (n, e);
        }

        private byte[] L(int keyLen, string label, BigInteger n, int e)
        {
            // 1. 生成 20 字节随机 Seed (类似 OAEP 结构)
            byte[] seed = new byte[20];
#if DEBUG
            seed = new byte[] { 90, 64, 187, 211, 235, 2, 14, 254, 104, 220, 29, 151, 185, 105, 121, 211, 98, 253, 44, 232 };

#else
            RandomNumberGenerator.Fill(seed);
#endif

            int hLen = 20; // SHA1 长度
            int dbLen = keyLen - hLen - 1;
            byte[] db = new byte[dbLen];

            // 2. 填充 DB: Hash(L) || PS || 01 || M
            // 这里的逻辑对应原代码中的 Sha1JsEquivalent 和 a 数组的处理
            byte[] lHash = SHA1.HashData(Encoding.UTF8.GetBytes(label));
            lHash.CopyTo(db.AsSpan());
            db[db.Length - 1 - label.Length - 1] = 1;
            // 注意：原代码的 a[l]=1 逻辑比较奇特，此处建议严格对齐业务逻辑，如果 M 是空字符串，则 1 是分隔符

            // 3. MGF1 掩码处理 (原代码中的 P 函数)
            byte[] dbMask = MGF1(seed, dbLen);
            for (int k = 0; k < dbLen; k++) db[k] ^= dbMask[k];

            byte[] seedMask = MGF1(db, hLen);
            for (int k = 0; k < hLen; k++) seed[k] ^= seedMask[k];

            // 4. 拼接最终要加密的数值: 00 || MaskedSeed || MaskedDB
            byte[] em = new byte[keyLen];
            seed.CopyTo(em.AsSpan(1, hLen));
            db.CopyTo(em.AsSpan(1 + hLen));

            // 5. RSA 加密: c = m^e mod n
            var m = new BigInteger(em, isUnsigned: true, isBigEndian: true);
            var resultInt = BigInteger.ModPow(m, e, n);

            // 转回字节数组并填充到 keyLen 长度
            byte[] resultBytes = resultInt.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (resultBytes.Length == keyLen) return resultBytes;

            byte[] final = new byte[keyLen];
            resultBytes.CopyTo(final.AsSpan(keyLen - resultBytes.Length));
            return final;
        }

        private byte[] MGF1(ReadOnlySpan<byte> seed, int maskLen)
        {
            byte[] mask = new byte[maskLen];
            byte[] counter = new byte[4];
            int offset = 0;
            uint n = 0;

            while (offset < maskLen)
            {
                BinaryPrimitives.WriteUInt32BigEndian(counter, n);

                // 拼接 seed + counter
                byte[] block = new byte[seed.Length + 4];
                seed.CopyTo(block);
                counter.CopyTo(block.AsSpan(seed.Length));

                byte[] hash = SHA1.HashData(block);
                int copyLen = Math.Min(hash.Length, maskLen - offset);
                hash.AsSpan(0, copyLen).CopyTo(mask.AsSpan(offset));

                offset += hash.Length;
                n++;
            }
            return mask;
        }

        private byte[] ToBuffer(byte[] buffer)
        {
            byte[] result = new byte[4 + buffer.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(result, AuthMechanism);
            buffer.CopyTo(result.AsSpan(4));
            return result;
        }

        public byte[] RecoverSeed(byte[] decryptedBlock)
        {
            // 1. 分割数据
            byte[] maskedSeed = decryptedBlock.AsSpan(1, 20).ToArray();
            byte[] maskedDB = decryptedBlock.AsSpan(21).ToArray();

            // 2. 用 MaskedDB 生成 Seed 的掩码
            // 注意：这里的 MGF1 必须和你加密时使用的逻辑完全一致（通常是基于 SHA-1）
            byte[] seedMask = MGF1(maskedDB, 20);

            // 3. 异或还原 Seed
            byte[] originalSeed = new byte[20];
            for (int i = 0; i < 20; i++)
            {
                originalSeed[i] = (byte)(maskedSeed[i] ^ seedMask[i]);
            }

            return originalSeed;
        }
    }
}