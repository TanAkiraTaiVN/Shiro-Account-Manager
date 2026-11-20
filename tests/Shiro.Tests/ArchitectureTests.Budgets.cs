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
        [Fact]
        public void NoSourceCodeFiles_ShouldExceed500Lines()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string? repoRoot = null;
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Shiro.sln")))
                {
                    repoRoot = dir.FullName;
                    break;
                }
                dir = dir.Parent;
            }

            Assert.NotNull(repoRoot);
            var appDir = Path.Combine(repoRoot!, "src", "Shiro");
            Assert.True(Directory.Exists(appDir));

            var files = Directory.GetFiles(appDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/") && !f.EndsWith(".g.cs"))
                .ToList();

            var oversizedFiles = files
                .Select(f => new { Path = f, Lines = File.ReadAllLines(f).Length })
                .Where(x => x.Lines > 485)
                .ToList();

            Assert.True(oversizedFiles.Count == 0,
                $"Expected 0 files exceeding 485 lines, but found {oversizedFiles.Count}:\n" +
                string.Join("\n", oversizedFiles.Select(x => $"{Path.GetFileName(x.Path)}: {x.Lines} lines")));
        }

        [Fact]
        public void NoClass_ShouldExceedLineBudget_AcrossPartials()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string? repoRoot = null;
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Shiro.sln")))
                {
                    repoRoot = dir.FullName;
                    break;
                }
                dir = dir.Parent;
            }

            Assert.NotNull(repoRoot);
            var appDir = Path.Combine(repoRoot!, "src", "Shiro");
            var files = Directory.GetFiles(appDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/") && !f.EndsWith(".g.cs"))
                .ToList();

            const int DefaultBudget = 1150;
            var baselines = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["Account"] = 2300
            };

            var classLines = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
            var classRegex = new System.Text.RegularExpressions.Regex(
                @"(?m)^\s*(?:public|internal|private|protected)?\s*(?:static|sealed|abstract)?\s*partial\s+class\s+(\w+)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

            foreach (var file in files)
            {
                string text = File.ReadAllText(file);
                int lines = File.ReadAllLines(file).Length;
                var matches = classRegex.Matches(text);
                var seenInFile = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    string className = match.Groups[1].Value;
                    if (seenInFile.Add(className))
                    {
                        classLines[className] = classLines.GetValueOrDefault(className, 0) + lines;
                    }
                }
            }

            var violations = new System.Collections.Generic.List<string>();
            foreach (var kvp in classLines)
            {
                int maxAllowed = baselines.TryGetValue(kvp.Key, out int b) ? b : DefaultBudget;
                if (kvp.Value > maxAllowed)
                {
                    violations.Add($"{kvp.Key}: {kvp.Value} lines (budget: {maxAllowed})");
                }
            }

            Assert.True(violations.Count == 0,
                $"Classes exceeded partial line budget:\n{string.Join("\n", violations)}");
        }

        [Fact]
        public void EmptyCatchBlocks_ShouldNotExceedBaseline()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string? repoRoot = null;
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Shiro.sln")))
                {
                    repoRoot = dir.FullName;
                    break;
                }
                dir = dir.Parent;
            }

            Assert.NotNull(repoRoot);
            var appDir = Path.Combine(repoRoot!, "src", "Shiro");
            var files = Directory.GetFiles(appDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/") && !f.EndsWith(".g.cs"))
                .ToList();

            const int Baseline = 0;
            int totalEmptyCatches = 0;
            var emptyCatchRegex = new System.Text.RegularExpressions.Regex(
                @"catch\s*(\([^\)]*\))?\s*\{\s*\}",
                System.Text.RegularExpressions.RegexOptions.Compiled);

            foreach (var file in files)
            {
                string text = File.ReadAllText(file);
                totalEmptyCatches += emptyCatchRegex.Matches(text).Count;
            }

            Assert.True(totalEmptyCatches <= Baseline,
                $"Empty catch count ({totalEmptyCatches}) exceeded baseline ({Baseline}). New code must log or handle exceptions.");
        }

        [Fact]
        public void NoAxamlFiles_ShouldExceed400Lines()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string? repoRoot = null;
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Shiro.sln")))
                {
                    repoRoot = dir.FullName;
                    break;
                }
                dir = dir.Parent;
            }

            Assert.NotNull(repoRoot);
            var appDir = Path.Combine(repoRoot!, "src", "Shiro");
            var files = Directory.GetFiles(appDir, "*.axaml", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/"))
                .ToList();

            var violations = new System.Collections.Generic.List<string>();
            foreach (var file in files)
            {
                string[] lines = File.ReadAllLines(file);
                int lineCount = lines.Length;
                if (lineCount > 400)
                {
                    violations.Add($"{Path.GetFileName(file)}: {lineCount} lines (max 400)");
                }
                int maxLineChars = lineCount == 0 ? 0 : lines.Max(l => l.Length);
                if (maxLineChars > 200)
                {
                    violations.Add($"{Path.GetFileName(file)}: longest line is {maxLineChars} chars (max 200 - do not pack multi-line XAML onto single lines)");
                }
            }

            string[] requiredPanels =
            {
                "DashboardInspectorPanel.axaml",
                "DashboardFooterBar.axaml",
                "ServerListResultsPanel.axaml",
                "MainWindowOverlaysPanel.axaml",
                "SettingsHeaderBanner.axaml"
            };
            var panelsDir = Path.Combine(appDir, "AvaloniaUI", "Views", "Panels");
            foreach (var panel in requiredPanels)
            {
                if (!File.Exists(Path.Combine(panelsDir, panel)))
                {
                    violations.Add($"Missing extracted UserControl panel: {panel}");
                }
            }

            Assert.True(violations.Count == 0,
                $"AXAML modularization or line-length violations:\n{string.Join("\n", violations)}");
        }

        [Fact]
        public void SourceFiles_ShouldNotUse_SyncOverAsyncBlocking()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string? repoRoot = null;
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Shiro.sln")))
                {
                    repoRoot = dir.FullName;
                    break;
                }
                dir = dir.Parent;
            }

            Assert.NotNull(repoRoot);
            var appDir = Path.Combine(repoRoot!, "src", "Shiro");
            var files = Directory.GetFiles(appDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/") && !f.EndsWith(".g.cs"))
                .ToList();

            var violations = new System.Collections.Generic.List<string>();
            foreach (var file in files)
            {
                string content = File.ReadAllText(file);
                if (content.Contains(".GetAwaiter().GetResult()", StringComparison.Ordinal) || content.Contains("httpClient.Send(", StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetFileName(file)} uses synchronous blocking (.GetAwaiter().GetResult() or httpClient.Send)");
                }
            }

            Assert.True(violations.Count == 0,
                $"Sync-over-async violations found:\n{string.Join("\n", violations)}");
        }

        [Fact]
        public void TestMethods_ShouldAssertOnValues_NotOnlyNullness()
        {
            var testDir = Path.Combine(RepoRoot, "tests", "Shiro.Tests");
            var csFiles = Directory.GetFiles(testDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/") && !f.EndsWith(".g.cs"))
                .ToList();

            var methodRegex = new Regex(
                @"\[(?:Fact|Theory)\b[^\]]*\](?:\s*\[[^\]]+\])*\s*(?:public|internal|private|protected)?\s*(?:static\s+)?(?:async\s+)?(?:void|Task(?:<[^>]+>)?)\s+([A-Za-z0-9_]+)\s*\([^)]*\)\s*\{",
                RegexOptions.Singleline | RegexOptions.Compiled);

            var assertRegex = new Regex(@"Assert\.([A-Za-z0-9_]+)\b", RegexOptions.Compiled);
            var weakTrueRegex = new Regex(@"Assert\.True\s*\([^;)]*(?:>|>=)\s*0|Assert\.True\s*\(\s*(\w+)\s*\|\|\s*!\1\s*\)", RegexOptions.Compiled | RegexOptions.Singleline);

            var offenders = new List<string>();

            foreach (var file in csFiles)
            {
                string text = File.ReadAllText(file);
                var matches = methodRegex.Matches(text);

                foreach (Match m in matches)
                {
                    string methodName = m.Groups[1].Value;
                    int startIdx = m.Index + m.Length - 1;
                    int braceCount = 1;
                    int i = startIdx + 1;
                    while (i < text.Length && braceCount > 0)
                    {
                        if (text[i] == '{') braceCount++;
                        else if (text[i] == '}') braceCount--;
                        i++;
                    }

                    string body = text.Substring(startIdx, i - startIdx);
                    var assertMatches = assertRegex.Matches(body);
                    bool hasThrows = body.Contains("Assert.Throws", StringComparison.Ordinal) ||
                                     body.Contains("Record.Exception", StringComparison.Ordinal);

                    if (assertMatches.Count == 0)
                    {
                        if (!hasThrows)
                        {
                            offenders.Add($"{Path.GetFileName(file)}::{methodName} (0 asserts)");
                        }
                    }
                    else
                    {
                        bool allWeak = true;
                        foreach (Match a in assertMatches)
                        {
                            string assertName = a.Groups[1].Value;
                            if (assertName == "NotNull" || assertName == "NotEmpty")
                            {
                                continue;
                            }

                            if (assertName == "True")
                            {
                                int callIdx = a.Index;
                                int semiIdx = body.IndexOf(';', callIdx);
                                string statement = semiIdx >= 0 ? body.Substring(callIdx, semiIdx - callIdx) : body.Substring(callIdx);
                                if (weakTrueRegex.IsMatch(statement))
                                {
                                    continue;
                                }
                            }

                            allWeak = false;
                            break;
                        }

                        if (allWeak)
                        {
                            offenders.Add($"{Path.GetFileName(file)}::{methodName} (all asserts are weak)");
                        }
                    }
                }
            }

            Assert.Empty(offenders);
        }
    }
}
