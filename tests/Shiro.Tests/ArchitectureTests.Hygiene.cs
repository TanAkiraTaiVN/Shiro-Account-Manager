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

        [Fact]
        public void LegacyClassesFolder_ShouldNotExist() =>
            Assert.False(Directory.Exists(Path.Combine(RepoRoot, "src", "Shiro", "Classes")));

        [Fact]
        public void NoTypes_ShouldLiveIn_ShiroClassesNamespace() =>
            Assert.Empty(CoreAssembly.GetTypes().Where(t => t.Namespace == "Shiro.Classes").Select(t => t.FullName));

        [Fact]
        public void CSharpComments_ShouldNotContainVietnameseDiacritics()
        {
            var vn = new Regex(@"[\u00C0-\u00FF\u0102\u0103\u0110\u0111\u0128\u0129\u0168\u0169\u01A0\u01A1\u01AF\u01B0\u1EA0-\u1EF9]");
            var comment = new Regex(@"//[^\r\n]*|/\*[\s\S]*?\*/");
            var offenders = SourceScan.AppSourceFiles(RepoRoot)
                .Where(f => comment.Matches(SourceScan.StripStringLiterals(File.ReadAllText(f))).Any(m => vn.IsMatch(m.Value)))
                .Select(Path.GetFileName).ToList();
            Assert.Empty(offenders);
        }
    }
}
