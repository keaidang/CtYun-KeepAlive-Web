using System;
using System.Threading.Channels;

namespace CtYun
{
    internal class Utility
    {
        public static Channel<string> LogChannel { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        public static void WriteLine(ConsoleColor consolecolor, object value)
        {
            Console.ForegroundColor = consolecolor;
            var timePrefix = "[" + DateTime.Now.ToString("HH:mm:ss.ff") + "] ";
            var text = value?.ToString() ?? "";
            var fullMsg = timePrefix + text;

            Console.WriteLine(fullMsg);
            LogChannel.Writer.TryWrite(fullMsg);
        }
    }
}
