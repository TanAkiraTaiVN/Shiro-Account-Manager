#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Shiro.Tests
{
    public partial class ArchitectureTests
    {
        [Fact]
        public void AsyncVoidHandlers_InViews_ShouldNotExceedBaseline()
        {
            const int Baseline = 18;
            var viewsDir = Path.Combine(RepoRoot, "src", "Shiro", "AvaloniaUI");
            var files = Directory.GetFiles(viewsDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/") && !f.EndsWith(".g.cs"));

            int actual = 0;
            foreach (var file in files)
            {
                actual += Regex.Matches(File.ReadAllText(file), @"\basync\s+void\b").Count;
            }

            Assert.True(actual <= Baseline, $"async void count in AvaloniaUI ({actual}) exceeds baseline ({Baseline}).");
        }

        [Fact]
        public void ProxyDeadKey_ShouldOnlyBeUsedInProxyCode()
        {
            var appDir = Path.Combine(RepoRoot, "src", "Shiro");
            var files = Directory.GetFiles(appDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/") && !f.EndsWith(".g.cs"));

            var offenders = files
                .Where(f => !Path.GetFileName(f).Contains("Proxy", StringComparison.OrdinalIgnoreCase) &&
                            File.ReadAllText(f).Contains("\"Proxy_Dead\"", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToList();

            Assert.Empty(offenders);
        }

        [Fact]
        public void NexusFolder_ShouldNotDependOn_Views()
        {
            const int Baseline = 8;
            var nexusDir = Path.Combine(RepoRoot, "src", "Shiro", "Nexus");
            var files = Directory.GetFiles(nexusDir, "*.cs", SearchOption.AllDirectories);

            int actual = 0;
            foreach (var file in files)
            {
                actual += Regex.Matches(File.ReadAllText(file), @"\bShiro\.AvaloniaUI\b").Count;
            }

            Assert.True(actual <= Baseline, $"Shiro.AvaloniaUI references in Nexus ({actual}) exceed baseline ({Baseline}).");
        }
    }
}
