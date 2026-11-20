#nullable enable
using System;
using System.Buffers;
using System.IO;
using System.Text;

namespace Shiro.Services
{
    internal partial class RobloxProcess
    {
        internal static string[] ReadAppendedLines(Stream stream, ref long lastPosition)
        {
            if (stream == null || !stream.CanRead) return Array.Empty<string>();
            long currentLength = stream.Length;
            if (currentLength <= lastPosition) return Array.Empty<string>();
            long delta = currentLength - lastPosition;
            if (delta <= 0 || delta > int.MaxValue) return Array.Empty<string>();

            int length = (int)delta;
            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                stream.Seek(lastPosition, SeekOrigin.Begin);
                stream.ReadExactly(rented, 0, length);

                // If the stream is a live FileStream and the last byte isn't a newline,
                // only consume up to the last complete newline so partial lines being written by Roblox are never split mid-line.
                int consumeLength = length;
                if (stream is FileStream && rented[length - 1] != (byte)'\n')
                {
                    int lastNewline = Array.LastIndexOf(rented, (byte)'\n', length - 1, length);
                    if (lastNewline < 0) return Array.Empty<string>();
                    consumeLength = lastNewline + 1;
                }
                lastPosition += consumeLength;
                return SplitCompletedLogLines(Encoding.UTF8.GetString(rented.AsSpan(0, consumeLength)));
            }
            finally { ArrayPool<byte>.Shared.Return(rented); }
        }

        internal static string[] SplitCompletedLogLines(string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
            var lines = text.Split('\n');
            int count = lines.Length;
            while (count > 0 && lines[count - 1].Length == 0)
                count--;
            if (count == 0) return Array.Empty<string>();
            var trimmed = new string[count];
            for (int i = 0; i < count; i++)
                trimmed[i] = lines[i].TrimEnd('\r');
            return trimmed;
        }

        internal static void RaiseLogFileReadSafely(EventHandler<EventArgs>? handlers, object? sender, EventArgs args)
        {
            if (handlers == null) return;
            foreach (EventHandler<EventArgs> sub in handlers.GetInvocationList())
            {
                try { sub(sender, args); }
                catch (Exception ex) { AppServices.Logger?.Debug($"LogFileRead subscriber failed: {ex.Message}"); }
            }
        }

        internal static bool ShouldAllowKill(bool ignoreExistingProcesses, bool startedBeforeWatcher, long lastPosition)
            => !ignoreExistingProcesses || !startedBeforeWatcher;
    }
}
