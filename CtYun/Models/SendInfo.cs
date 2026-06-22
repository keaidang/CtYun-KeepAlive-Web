using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CtYun.Models
{
   
    public class SendInfo
    {
        public int Type { get; set; }
        public byte[] Data { get; set; }

        // 原有的 Size 属性通常可以根据 Data.Length 动态获取
        public int Size => Data?.Length ?? 0;

        /// <summary>
        /// 将当前对象序列化为 byte[]
        /// 结构：Type(2字节) + Length(4字节) + Data(N字节)
        /// </summary>
        public byte[] ToBuffer(bool isBuildMsg)
        {
            var msgLength = 0;
            if (isBuildMsg)
                msgLength = 8;

            int dataLength = Data?.Length ?? 0;
            // 总长度 = Type(2) + Length(4) + Data(N)
            byte[] buffer = new byte[2 + 4+ msgLength + dataLength];

            // 1. 写入 Type (ushort, 2字节)
            buffer[0] = (byte)(Type & 0xFF);
            buffer[1] = (byte)((Type >> 8) & 0xFF);

            // 2. 写入 Size (int, 4字节)
            var size= msgLength+ dataLength;
            buffer[2] = (byte)(size & 0xFF);
            buffer[3] = (byte)((size >> 8) & 0xFF);
            buffer[4] = (byte)((size >> 16) & 0xFF);
            buffer[5] = (byte)((size >> 24) & 0xFF);

            if (isBuildMsg)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(6, 4), (uint)dataLength);
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(10, 4), 8);
            }

            // 3. 写入 Data
            if (dataLength > 0)
            {
                Buffer.BlockCopy(Data, 0, buffer, 6+ msgLength, dataLength);
            }

            return buffer;
        }



        /// <summary>
        /// 静态解析方法：从 byte[] 还原为 SendInfo 对象
        /// </summary>
        public static List<SendInfo> FromBuffer(byte[] buffer)
        {
            var results = new List<SendInfo>();
            if (buffer == null || buffer.Length == 0)
                return results;

            int offset = 0;
            // 循环条件：剩余字节至少能凑够一个报文头 (6字节)
            while (offset + 6 <= buffer.Length)
            {
                // 1. 尝试解析 Type 和 Size
                ushort type = BitConverter.ToUInt16(buffer, offset);
                int dataLength = BitConverter.ToInt32(buffer, offset + 2);

                // 2. 检查数据长度是否合法 (防止 dataLength 过大导致内存溢出)
                // 如果 dataLength 明显不合理，或者剩余长度不足以装下这个包
                if (dataLength < 0 || offset + 6 + dataLength > buffer.Length)
                {
                    // --- 5. 处理半包：将剩余未处理的所有字节放入最后一个 SendInfo ---
                    int remainingCount = buffer.Length - offset;
                    if (remainingCount > 0)
                    {
                        results.Add(new SendInfo
                        {
                            Type = type, // 或者定义一个特殊的标志位表示“半包/残缺数据”
                            Data = new byte[remainingCount]
                        });
                        Buffer.BlockCopy(buffer, offset, results[results.Count - 1].Data, 0, remainingCount);
                    }
                    break;
                }

                // 3. 提取完整的数据包
                var info = new SendInfo
                {
                    Type = type,
                    Data = new byte[dataLength]
                };
                if (dataLength > 0)
                {
                    Buffer.BlockCopy(buffer, offset + 6, info.Data, 0, dataLength);
                }

                results.Add(info);
                offset += 6 + dataLength;

                // 4. 特殊处理末尾填充 (针对你提供的全0数据)
                // 如果剩下的字节全是0，且长度不足以构成下一个包头，直接结束避免产生空包
                if (offset + 6 > buffer.Length && offset < buffer.Length)
                {
                    // 检查剩余字节是否全为 0 (可选，根据你的协议而定)
                    bool allZero = true;
                    for (int i = offset; i < buffer.Length; i++)
                    {
                        if (buffer[i] != 0) { allZero = false; break; }
                    }
                    if (allZero)
                    {
                        offset = buffer.Length; // 视为已处理完
                        break;
                    }
                }
            }

       

            return results;
        }
    }
}
