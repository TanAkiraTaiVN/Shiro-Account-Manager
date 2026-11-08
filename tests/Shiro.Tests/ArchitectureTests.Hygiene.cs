#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Shiro.Tests
{
    public partial class ArchitectureTests
    {
        private static readonly HashSet<string> UntypedCatchAllowList = new(StringComparer.OrdinalIgnoreCase) { "AppLogger.cs" };

        [Fact]
        public void UntypedCatchBlocks_ShouldNotExceedBaseline()
        {
            const int Baseline = 0;
            var offenders = SourceScan.AppSourceFiles(RepoRoot)
                .Where(f => !UntypedCatchAllowList.Contains(Path.GetFileName(f)))
                .SelectMany(f => Regex.Matches(SourceScan.StripStringLiterals(File.ReadAllText(f)),
                         @"\bcatch\s*\{|\bcatch\s*\(\s*(System\.)?Exception\s*\)")
                    .Select(_ => Path.GetFileName(f)))
                .ToList();
            Assert.True(offenders.Count <= Baseline,
                $"Untyped catch count {offenders.Count} > {Baseline}: {string.Join(", ", offenders.GroupBy(x => x).Select(g => $"{g.Key}×{g.Count()}"))}");
        }

        [Fact]
        public void SourceScan_StripsEmbeddedJavaScript()
        {
            const string code = "var js = \"\"\"\n try { x(); } catch (_) { }\n\"\"\"; try { } catch { }";
            Assert.Single(Regex.Matches(SourceScan.StripStringLiterals(code), @"\bcatch\s*\{"));
        }
    }
}
