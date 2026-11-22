using System;
using System.IO;
using System.Text;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class LogWatcherAndWebAuthTests
    {
        [Fact]
        public void ReadAppendedLines_FileStreamWithHalfWrittenLineWithoutNewline_WaitsUntilNewlineFlushed()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "ShiroLogTest_" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                using var writer = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                using var reader = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long pos = 0;

                byte[] part1 = Encoding.UTF8.GetBytes("2026-11-04T12:00:00.000Z [FLog::Network] Connection lost");
                writer.Write(part1, 0, part1.Length);
                writer.Flush();

                var lines1 = RobloxProcess.ReadAppendedLines(reader, ref pos);
                Assert.Empty(lines1);
                Assert.Equal(0, pos);

                byte[] part2 = Encoding.UTF8.GetBytes(" with reason 267\r\n");
                writer.Write(part2, 0, part2.Length);
                writer.Flush();

                var lines2 = RobloxProcess.ReadAppendedLines(reader, ref pos);
                Assert.Contains("2026-11-04T12:00:00.000Z [FLog::Network] Connection lost with reason 267", lines2[0]);
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }

        [Fact]
        public void WebAuthPolicy_WhenAllowExternalTrue_StillBlocksUntrustedBrowserOrigin()
        {
            var settings = new AuthSettings(AllowExternal: true, EveryRequestRequiresPassword: false, ConfiguredPassword: "StrongPassword123");
            var req = new AuthRequest(
                Scope: AuthScope.Api,
                IsLoopbackRemote: true,
                Host: "localhost",
                Origin: "https://evil.example.com",
                HeaderPassword: "StrongPassword123",
                QueryPassword: null);

            var decision = WebAuthPolicy.Evaluate(req, settings);
            Assert.Equal(AuthDecision.ForbiddenOrigin, decision);
        }
    }
}
