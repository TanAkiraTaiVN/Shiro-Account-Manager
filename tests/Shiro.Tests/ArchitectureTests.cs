#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NetArchTest.Rules;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    public partial class ArchitectureTests
    {
        private static readonly Assembly CoreAssembly = typeof(AccountViewModel).Assembly;

        [Fact]
        public void ViewModels_ShouldNotDependOn_Views()
        {
            var result = Types.InAssembly(CoreAssembly)
                .That()
                .ResideInNamespace("Shiro.ViewModels")
                .ShouldNot()
                .HaveDependencyOnAny("Shiro.AvaloniaUI.Views")
                .GetResult();

            var failingNames = result.FailingTypes?.Select(t => t.FullName) ?? Enumerable.Empty<string>();
            Assert.True(result.IsSuccessful, $"ViewModels should not depend on Views. Violations: {string.Join(", ", failingNames)}");
        }

        [Fact]
        public void Services_And_Classes_ShouldNotDependOn_Views()
        {
            var coveredTypes = Types.InAssembly(CoreAssembly)
                .That()
                .DoNotResideInNamespace("Shiro.AvaloniaUI")
                .And()
                .DoNotHaveName("AvaloniaDialogService")
                .GetTypes()
                .Select(t => t.FullName)
                .ToList();

            Assert.Contains(typeof(AccountManager).FullName, coveredTypes);
            Assert.Contains(typeof(Account).FullName, coveredTypes);

            var result = Types.InAssembly(CoreAssembly)
                .That()
                .DoNotResideInNamespace("Shiro.AvaloniaUI")
                .And()
                .DoNotHaveName("AvaloniaDialogService")
                .ShouldNot()
                .HaveDependencyOnAny("Shiro.AvaloniaUI.Views")
                .GetResult();

            var failingNames = (result.FailingTypes?.Select(t => t.FullName ?? t.Name) ?? Enumerable.Empty<string>())
                .Where(name => !name.StartsWith("Shiro.Services.AvaloniaDialogService", StringComparison.Ordinal))
                .ToList();
            Assert.True(failingNames.Count == 0, $"Non-UI types (except AvaloniaDialogService) should not depend on Views. Violations: {string.Join(", ", failingNames)}");
        }

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
        public void Services_ShouldFollowNamingConvention()
        {
            var result = Types.InAssembly(CoreAssembly)
                .That()
                .ResideInNamespace("Shiro.Services")
                .And()
                .AreClasses()
                .And()
                .AreNotNested()
                .Should()
                .HaveNameEndingWith("Service")
                .Or()
                .HaveNameEndingWith("Helper")
                .Or()
                .HaveNameEndingWith("Client")
                .Or()
                .HaveNameEndingWith("Coordinator")
                .Or()
                .HaveNameEndingWith("Paths")
                .Or()
                .HaveNameEndingWith("Keys")
                .Or()
                .HaveNameEndingWith("Settings")
                .Or()
                .HaveNameEndingWith("Logger")
                .Or()
                .HaveNameEndingWith("Redactor")
                .Or()
                .HaveNameEndingWith("Store")
                .Or()
                .HaveNameEndingWith("Optimizer")
                .Or()
                .HaveNameEndingWith("Dispatcher")
                .Or()
                .HaveNameEndingWith("Loc")
                .Or()
                .HaveNameEndingWith("Serializer")
                .Or()
                .HaveNameEndingWith("Repository")
                .Or()
                .HaveNameEndingWith("Proxy")
                .Or()
                .HaveNameEndingWith("Result")
                .Or()
                .HaveNameEndingWith("Converter")
                .Or()
                .HaveNameEndingWith("Json")
                .Or()
                .HaveNameEndingWith("Extensions")
                .Or()
                .HaveNameEndingWith("WebServer")
                .Or()
                .HaveNameEndingWith("Request")
                .Or()
                .HaveNameEndingWith("Context")
                .Or()
                .HaveNameEndingWith("Progress")
                .Or()
                .HaveNameStartingWith("LruCache")
                .Or()
                .HaveNameEndingWith("Task")
                .Or()
                .HaveNameEndingWith("Item")
                .Or()
                .HaveNameEndingWith("Response")
                .Or()
                .HaveNameEndingWith("Launcher")
                .Or()
                .HaveNameEndingWith("User")
                .Or()
                .HaveNameEndingWith("Alert")
                .Or()
                .HaveNameEndingWith("Policy")
                .Or()
                .HaveNameEndingWith("Decoder")
                .Or()
                .HaveNameEndingWith("Parser")
                .Or()
                .HaveNameEndingWith("Installer")
                .Or()
                .HaveNameEndingWith("Info")
                .Or()
                .HaveNameEndingWith("Scripts")
                .Or()
                .HaveName("AppServices")
                .GetResult();

            var failingNames = result.FailingTypes?.Select(t => t.FullName) ?? Enumerable.Empty<string>();
            Assert.True(result.IsSuccessful, $"Services naming convention check failed. Violations: {string.Join(", ", failingNames)}");
        }

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
            var regex = new System.Text.RegularExpressions.Regex(@"\{DynamicResource\s+([A-Za-z0-9_]+)\}");

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

            Assert.Equal(30, count);
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
                ["Account"] = 2400,
                ["MainViewModel"] = 3300,
                ["AccountManager"] = 2671,
                ["ServerListViewModel"] = 1650
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
        public void Services_ShouldUse_AppSettings_And_AccountRepository_InsteadOf_AccountManagerStatic()
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
            var servicesDir = Path.Combine(repoRoot!, "src", "Shiro", "Services");
            var viewModelsDir = Path.Combine(repoRoot!, "src", "Shiro", "ViewModels");
            var classesDir = Path.Combine(repoRoot!, "src", "Shiro", "Classes");
            var avaloniaDir = Path.Combine(repoRoot!, "src", "Shiro", "AvaloniaUI");
            var nexusDir = Path.Combine(repoRoot!, "src", "Shiro", "Nexus");
            var files = Directory.GetFiles(servicesDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("AccountRepository.cs", StringComparison.OrdinalIgnoreCase))
                .Concat(Directory.GetFiles(viewModelsDir, "*.cs", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(classesDir, "*.cs", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(avaloniaDir, "*.cs", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(nexusDir, "*.cs", SearchOption.AllDirectories))
                .ToList();

            var violations = new System.Collections.Generic.List<string>();
            foreach (var file in files)
            {
                string text = File.ReadAllText(file);
                if (Regex.IsMatch(text, @"(?<!"")\bAccountManager\."))
                {
                    violations.Add($"{Path.GetFileName(file)} references AccountManager.* directly (use AppServices / AppSettings)");
                }
            }

            Assert.True(violations.Count == 0,
                $"Service/ViewModel decoupling violations found:\n{string.Join("\n", violations)}");
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
        public void Services_Directory_ShouldRemainModularized()
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
            var servicesDir = Path.Combine(repoRoot!, "src", "Shiro", "Services");
            var rootFiles = Directory.GetFiles(servicesDir, "*.cs", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.Equals(name, "AppServices.cs", StringComparison.OrdinalIgnoreCase) &&
                               !string.Equals(name, "ShiroAppJson.cs", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(rootFiles.Count == 0,
                $"New service files must be placed inside a domain subfolder (Auth, Launch, Storage, Windows, Network, Ui). Found at root: {string.Join(", ", rootFiles)}");
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
            "**/*.g.cs", "**/*.Designer.cs", "**/App.axaml.cs", "**/AvaloniaUI/**", "**/tests/**",
            "**/RobloxBrowserLogin*.cs", "**/RobloxBrowserSignup*.cs", "**/AccountBrowser.cs",
            "**/ChromiumHomeBrowser.cs", "**/RobloxProcess.cs", "**/RobloxWatcher.cs"
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

        [Fact]
        public void AmbientStaticAccess_ShouldNotExceedPerFolderBaseline()
        {
            var baseline = new Dictionary<string, (int Logger, int Settings)>
            {
                ["Services/Network"] = (0, 0),
                ["Services/Auth"] = (0, 0),
                ["Services/Storage"] = (0, 0),
                ["ViewModels"] = (0, 0),
                // locked at current baseline; may only go down:
                ["Classes"] = (175, 52),
                ["Services/Launch"] = (137, 50),
                ["Services/Windows"] = (97, 14),
                ["Services/Ui"] = (17, 11),
                ["AvaloniaUI/Views"] = (60, 49),
            };

            var violations = new List<string>();

            foreach (var (folder, (maxLogger, maxSettings)) in baseline)
            {
                var dir = Path.Combine(RepoRoot, "src", "Shiro", folder.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(dir))
                {
                    violations.Add($"Directory does not exist: {folder}");
                    continue;
                }

                var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/") && !f.EndsWith(".g.cs"));

                if (folder == "Services/Storage")
                {
                    files = files.Where(f => !Path.GetFileName(f).Equals("AppSettings.cs", StringComparison.OrdinalIgnoreCase) &&
                                             !Path.GetFileName(f).Equals("AppLogger.cs", StringComparison.OrdinalIgnoreCase));
                }

                int actualLogger = 0;
                int actualSettings = 0;

                foreach (var file in files)
                {
                    string text = File.ReadAllText(file);
                    actualLogger += Regex.Matches(text, @"\bProgram\.Logger\b").Count;
                    actualSettings += Regex.Matches(text, @"\bAppSettings\.").Count;
                }

                if (actualLogger > maxLogger)
                {
                    violations.Add($"{folder}: Program.Logger count {actualLogger} exceeds baseline {maxLogger}");
                }
                if (actualSettings > maxSettings)
                {
                    violations.Add($"{folder}: AppSettings. count {actualSettings} exceeds baseline {maxSettings}");
                }
            }

            Assert.True(violations.Count == 0,
                $"Ambient static access exceeded baseline:\n{string.Join("\n", violations)}");
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
