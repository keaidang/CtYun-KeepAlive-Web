using CtYun.Models;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace CtYun
{
    public  class ConnectInfo
    {
        [JsonPropertyName("desktopInfo")]
        public DesktopInfo DesktopInfo { get; set; }
    }

    public class DesktopInfo
    {
        [JsonPropertyName("desktopId")]
        public int DesktopId { get; set; }

        [JsonPropertyName("host")]
        public string Host { get; set; }

        [JsonPropertyName("port")]
        public string Port { get; set; }

        [JsonPropertyName("clinkLvsOutHost")]
        public string ClinkLvsOutHost { get; set; }

        [JsonPropertyName("caCert")]
        public string CaCert { get; set; }

        [JsonPropertyName("clientCert")]
        public string ClientCert { get; set; }

        [JsonPropertyName("clientKey")]
        public string ClientKey { get; set; }

        [JsonPropertyName("token")]
        public string Token { get; set; }

        [JsonPropertyName("tenantMemberAccount")]
        public string TenantMemberAccount { get; set; }

        [JsonPropertyName("osType")]
        public string OsType { get; set; }
        public byte[] ToBuffer(string deviceCode)
        {
            var deviceType = "60";

            // 1. 预计算总长度 (也可以使用 MemoryStream 动态处理)
            // 头部固定长度：4(desktopId) + 4*2(session) + 4*2(type) + 4*2(code) + 4*2(account) = 36 字节
            int headerSize = 36;
            int totalSize = headerSize +
                            (Token.Length + 1) +
                            (deviceType.Length + 1) +
                            (deviceCode.Length + 1) +
                            (TenantMemberAccount.Length + 1);

            byte[] buffer = new byte[totalSize];

            using (MemoryStream ms = new MemoryStream(buffer))
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                uint currentOffset = (uint)headerSize;

                // --- 写入头部信息 ---

                // 写入 desktopId (uint32, 小端序)
                writer.Write(DesktopId);

                // 写入 SessionId 的 长度 和 偏移
                writer.Write((uint)Token.Length + 1);
                writer.Write(currentOffset);
                currentOffset += (uint)Token.Length + 1;

                // 写入 DeviceType 的 长度 和 偏移
                writer.Write((uint)deviceType.Length + 1);
                writer.Write(currentOffset);
                currentOffset += (uint)deviceType.Length + 1;

                // 写入 DeviceCode 的 长度 和 偏移
                writer.Write((uint)deviceCode.Length + 1);
                writer.Write(currentOffset);
                currentOffset += (uint)deviceCode.Length + 1;

                // 写入 UserAccount 的 长度 和 偏移
                writer.Write((uint)TenantMemberAccount.Length + 1);
                writer.Write(currentOffset);
                // currentOffset 不再需要累加，因为后面没数据了

                // --- 写入实际字符串内容 (Body) ---
                // 注意：JS 循环 charCodeAt 是按字节写入，C# 需指定编码
                WriteFixedString(writer, Token);
                WriteFixedString(writer, deviceType);
                WriteFixedString(writer, deviceCode);
                WriteFixedString(writer, TenantMemberAccount);
            }

            return buffer;
        }

        // 辅助方法：写入字符串并补一个 \0 字节
        private void WriteFixedString(BinaryWriter writer, string str)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(str);
            writer.Write(bytes);
            writer.Write((byte)0); // 对应 JS 的 length + 1 和跳位逻辑
        }
    }
    public class ConnecMessage
    {
        public int type { get; set; }
        public int ssl { get; set; }
        public string host { get; set; }
        public string port { get; set; }
        public string ca { get; set; }
        public string cert { get; set; }
        public string key { get; set; }
        public string servername { get; set; }

        public int oqs { get; set; }
    }



}
