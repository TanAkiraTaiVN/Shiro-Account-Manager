#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public partial class ArchitectureTests
    {
        internal static readonly Assembly CoreAssembly = typeof(AccountViewModel).Assembly;

        [GeneratedRegex(@"\{DynamicResource\s+([A-Za-z0-9_.]+)\}")]
        internal static partial Regex DynamicResourcePattern();

        [Fact]
        public void AccountManager_AccountsList_ShouldNotBePubliclyExposed()
        {
            var publicField = typeof(AccountManager).GetField("AccountsList", BindingFlags.Public | BindingFlags.Static);
            Assert.Null(publicField);
        }

        [Fact]
        public void SourceCode_ShouldEnforceModernNetworkingAndZeroPersonalDataLeaks()
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

            foreach (var file in files)
            {
                string content = File.ReadAllText(file);
                Assert.DoesNotContain("$RECYCLE.BIN", content, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("System.Net.WebClient", content, StringComparison.Ordinal);
                Assert.DoesNotContain("HttpWebRequest", content, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void AllAxamlDynamicResourceKeys_ShouldBeDefinedInLocalizationDictionaries()
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
            var axamlFiles = Directory.GetFiles(appDir, "*.axaml", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/"))
                .ToList();

            Shiro.Services.LocalizationService.EnsureCoreKeys();
            var regex = DynamicResourcePattern();

            foreach (var file in axamlFiles)
            {
                string content = File.ReadAllText(file);
                foreach (System.Text.RegularExpressions.Match match in regex.Matches(content))
                {
                    string key = match.Groups[1].Value;
                    if (key.EndsWith("Brush", StringComparison.Ordinal) ||
                        key.EndsWith("Color", StringComparison.Ordinal) ||
                        key.Contains("Theme", StringComparison.Ordinal) ||
                        key.Contains("Font", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string lookupKey = key.StartsWith("Loc_", StringComparison.Ordinal) ? key.Substring(4) : key;
                    string resolved = Shiro.Services.LocalizationService.Get(lookupKey, "__MISSING__");
                    Assert.NotEqual("__MISSING__", resolved);
                }
            }
        }

        [Fact]
        public void AllCommandPaletteBuiltinCommands_ShouldMapToValidLucideIcons()
        {
            var method = typeof(MainViewModel).GetMethod("GetBuiltinCommands", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var commands = (System.Collections.IEnumerable)method!.Invoke(null, null)!;
            int count = 0;

            foreach (var cmd in commands)
            {
                count++;
                var iconNameProp = cmd.GetType().GetProperty("IconName");
                Assert.NotNull(iconNameProp);
                string iconName = (string)iconNameProp!.GetValue(cmd)!;

                Assert.True(Enum.TryParse<LucideAvalonia.Enum.LucideIconNames>(iconName, ignoreCase: true, out var parsed),
                    $"BuiltinCommand has invalid LucideIconNames '{iconName}'");

                var toVmMethod = cmd.GetType().GetMethod("ToViewModel");
                var itemVm = (CommandPaletteItemViewModel)toVmMethod!.Invoke(cmd, new object[] { false })!;
                Assert.Equal(parsed, itemVm.IconKind);
                Assert.NotNull(itemVm.IconBrush);
            }

            Assert.Equal(29, count);
        }

        [Fact]
        public void SourceFiles_ShouldNotContain_MojibakeOrReplacementCharacters()
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
            var extensions = new[] { "*.cs", "*.axaml", "*.json" };
            var violations = new System.Collections.Generic.List<string>();

            foreach (var ext in extensions)
            {
                foreach (var file in Directory.GetFiles(appDir, ext, SearchOption.AllDirectories))
                {
                    string norm = file.Replace('\\', '/');
                    if (norm.Contains("/bin/") || norm.Contains("/obj/"))
                        continue;

                    string content = File.ReadAllText(file);
                    if (content.Contains('\uFFFD'))
                    {
                        violations.Add($"{Path.GetFileName(file)} contains Unicode replacement character (U+FFFD)");
                    }
                }
            }

            Assert.True(violations.Count == 0,
                $"Encoding/mojibake violations found:\n{string.Join("\n", violations)}");
        }

        [Fact]
        public void CSharpSourceFiles_ShouldNotContain_HardcodedVietnameseStringLiterals()
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
                .Where(f => !Path.GetFileName(f).StartsWith("LocalizationService", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var vnLiteralRegex = new System.Text.RegularExpressions.Regex(
                "\"[^\"\r\n]*[àáảãạăằắẳẵặâầấẩẫậèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵđÀÁẢÃẠĂẰẮẲẴẶÂẦẤẨẪẬÈÉẺẼẸÊỀẾỂỄỆÌÍỈĨỊÒÓỎÕỌÔỒỐỔỖỘƠỜỚỞỠỢÙÚỦŨỤƯỪỨỬỮỰỲÝỶỸỴĐ][^\"\r\n]*\"");

            var violations = new System.Collections.Generic.List<string>();
            foreach (var file in files)
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("/*", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal))
                        continue;

                    int commentIdx = line.IndexOf("//", StringComparison.Ordinal);
                    string codePart = commentIdx >= 0 ? line.Substring(0, commentIdx) : line;
                    var match = vnLiteralRegex.Match(codePart);
                    if (match.Success)
                    {
                        violations.Add($"{Path.GetFileName(file)}:{i + 1} -> {match.Value}");
                    }
                }
            }

            Assert.True(violations.Count == 0,
                $"Found {violations.Count} hardcoded Vietnamese string literals outside LocalizationService*:\n" +
                string.Join("\n", violations.Take(40)));
        }

        [Fact]
        public void TestProject_ShouldEnableStrictNullable_ReferenceNSubstitute_And_EnforceCoverageGates()
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
            string testCsproj = File.ReadAllText(Path.Combine(repoRoot!, "tests", "Shiro.Tests", "Shiro.Tests.csproj"));
            Assert.Contains("<Nullable>enable</Nullable>", testCsproj, StringComparison.Ordinal);
            Assert.Contains("Include=\"NSubstitute\"", testCsproj, StringComparison.Ordinal);

            string checkCov = File.ReadAllText(Path.Combine(repoRoot!, "scripts", "Check-Coverage.ps1"));
            Assert.Contains("[double]$MinOverall = 75.0", checkCov, StringComparison.Ordinal);
            Assert.Contains("[double]$MinBranch = 60.0", checkCov, StringComparison.Ordinal);
            Assert.Contains("[double]$MinCore = 92.0", checkCov, StringComparison.Ordinal);

            string buildYml = File.ReadAllText(Path.Combine(repoRoot!, ".github", "workflows", "build.yml"));
            Assert.Contains("-MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0", buildYml, StringComparison.Ordinal);

            string releaseYml = File.ReadAllText(Path.Combine(repoRoot!, ".github", "workflows", "release.yml"));
            Assert.Contains("-MinOverall 75.0 -MinBranch 60.0 -MinCore 92.0", releaseYml, StringComparison.Ordinal);
        }

        [Fact]
        public void GitHubWorkflows_ShouldPinActionsToFullCommitSha()
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
            var workflowsDir = Path.Combine(repoRoot!, ".github", "workflows");
            var ymlFiles = Directory.GetFiles(workflowsDir, "*.yml");
            var usesRegex = new System.Text.RegularExpressions.Regex(@"^\s*-?\s*uses:\s*([^\s#]+)", System.Text.RegularExpressions.RegexOptions.Multiline);
            var shaRegex = new System.Text.RegularExpressions.Regex(@"@[0-9a-f]{40}$");

            var violations = new System.Collections.Generic.List<string>();
            foreach (var file in ymlFiles)
            {
                string content = File.ReadAllText(file);
                foreach (System.Text.RegularExpressions.Match match in usesRegex.Matches(content))
                {
                    string actionRef = match.Groups[1].Value;
                    if (!shaRegex.IsMatch(actionRef))
                    {
                        violations.Add($"{Path.GetFileName(file)}: '{actionRef}' is not pinned to a 40-char commit SHA");
                    }
                }
            }

            Assert.True(violations.Count == 0,
                $"Unpinned GitHub Actions found:\n{string.Join("\n", violations)}");
        }

        private static IEnumerable<string> SourceFiles(string relativeDir, string pattern)
        {
            var appDir = Path.Combine(RepoRoot, relativeDir);
            return Directory.GetFiles(appDir, pattern, SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/") && !f.EndsWith(".g.cs"));
        }

        [Fact]
        public void CSharpSourceFiles_ShouldNotBranchOnIsEnglish()
        {
            var files = SourceFiles("src/Shiro", "*.cs")
                .Where(f => !Path.GetFileName(f).StartsWith("LocalizationService"))
                .ToList();

            var ternaryOffenders = files
                .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bIsEnglish\s*\?", RegexOptions.IgnoreCase))
                .Select(Path.GetFileName)
                .ToList();
            Assert.Empty(ternaryOffenders);

            var anyIsEnglishOffenders = files
                .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bIsEnglish\b"))
                .Select(Path.GetFileName)
                .ToList();
            Assert.Empty(anyIsEnglishOffenders);
        }

        internal static string RepoRoot
        {
            get
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, "Shiro.sln")))
                    {
                        return dir.FullName;
                    }
                    dir = dir.Parent;
                }
                throw new InvalidOperationException("Shiro.sln not found in parent directories.");
            }
        }

        public static readonly string[] ApprovedCoverageExclusions =
        {
            "**/*.g.cs", "**/*.Designer.cs", "**/App.axaml.cs", "**/AvaloniaUI/**", "**/tests/**"
        };

        [Fact]
        public void CoverageRunSettings_ExcludeByFile_ShouldMatchApprovedList()
        {
            string[] approved = ApprovedCoverageExclusions;
            var xml = System.Xml.Linq.XDocument.Load(Path.Combine(RepoRoot, "tests", "coverage.runsettings"));
            var actual = xml.Descendants("ExcludeByFile").Single().Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(approved.OrderBy(x => x), actual.OrderBy(x => x));
        }

        [Fact]
        public void VersionMetadata_ShouldBeSynchronizedAcrossProjectAndDocs()
        {
            const string ExpectedVersion = "5.0.0";

            string csproj = File.ReadAllText(Path.Combine(RepoRoot, "src", "Shiro", "Shiro.csproj"));
            Assert.Contains($"<Version>{ExpectedVersion}</Version>", csproj, StringComparison.Ordinal);
            Assert.Contains($"<AssemblyVersion>{ExpectedVersion}.0</AssemblyVersion>", csproj, StringComparison.Ordinal);
            Assert.Contains($"<FileVersion>{ExpectedVersion}.0</FileVersion>", csproj, StringComparison.Ordinal);

            Version? asmVersion = CoreAssembly.GetName().Version;
            Assert.Equal(new Version(5, 0, 0, 0), asmVersion);

            string aboutView = File.ReadAllText(Path.Combine(RepoRoot, "src", "Shiro", "AvaloniaUI", "Views", "AboutView.axaml"));
            Assert.Contains($"v{ExpectedVersion}", aboutView, StringComparison.Ordinal);

            string openApiJson = Shiro.Services.ApiDocumentationService.GetOpenApiJson();
            Assert.Contains($"\"version\":\"{ExpectedVersion}\"", openApiJson.Replace(" ", ""), StringComparison.Ordinal);

            string docsHtml = Shiro.Services.ApiDocumentationService.GetDocsHtml();
            Assert.Contains($"Gateway v{ExpectedVersion}", docsHtml, StringComparison.Ordinal);

            string enJson = File.ReadAllText(Path.Combine(RepoRoot, "src", "Shiro", "Resources", "Strings", "en.json"));
            string viJson = File.ReadAllText(Path.Combine(RepoRoot, "src", "Shiro", "Resources", "Strings", "vi.json"));
            Assert.Contains($"Version {ExpectedVersion}", enJson, StringComparison.Ordinal);
            Assert.Contains($"Phiên bản {ExpectedVersion}", viJson, StringComparison.Ordinal);

            string securityMd = File.ReadAllText(Path.Combine(RepoRoot, "SECURITY.md"));
            Assert.Contains("5.0.x", securityMd, StringComparison.Ordinal);

            string noticeMd = File.ReadAllText(Path.Combine(RepoRoot, "NOTICE.md"));
            Assert.Contains($"v{ExpectedVersion}", noticeMd, StringComparison.Ordinal);

            string contributingMd = File.ReadAllText(Path.Combine(RepoRoot, "CONTRIBUTING.md"));
            Assert.Contains("-MinOverall 75.0", contributingMd, StringComparison.Ordinal);
            Assert.Contains("-MinCore 92.0", contributingMd, StringComparison.Ordinal);

            string threatModelMd = File.ReadAllText(Path.Combine(RepoRoot, "docs", "threat-model.md"));
            Assert.DoesNotContain("AccountData.dat", threatModelMd, StringComparison.Ordinal);
            Assert.Contains("AccountData.json", threatModelMd, StringComparison.Ordinal);
        }
    }
}
