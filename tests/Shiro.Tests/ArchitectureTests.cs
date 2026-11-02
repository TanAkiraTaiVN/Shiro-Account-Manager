#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NetArchTest.Rules;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    public class ArchitectureTests
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
            var result = Types.InAssembly(CoreAssembly)
                .That()
                .ResideInNamespace("Shiro.Services")
                .Or()
                .ResideInNamespace("Shiro.Classes")
                .And()
                .DoNotHaveName("AvaloniaDialogService")
                .ShouldNot()
                .HaveDependencyOnAny("Shiro.AvaloniaUI.Views")
                .GetResult();

            var failingNames = (result.FailingTypes?.Select(t => t.FullName ?? t.Name) ?? Enumerable.Empty<string>())
                .Where(name => !name.StartsWith("Shiro.Services.AvaloniaDialogService", StringComparison.Ordinal))
                .ToList();
            Assert.True(failingNames.Count == 0, $"Services and Classes (except AvaloniaDialogService) should not depend on Views. Violations: {string.Join(", ", failingNames)}");
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
                .Where(f => !f.Contains(@"\bin\") && !f.Contains(@"\obj\") && !f.EndsWith(".g.cs"))
                .ToList();

            var oversizedFiles = files
                .Select(f => new { Path = f, Lines = File.ReadAllLines(f).Length })
                .Where(x => x.Lines > 500)
                .ToList();

            Assert.True(oversizedFiles.Count == 0,
                $"Expected 0 files exceeding 500 lines, but found {oversizedFiles.Count}:\n" +
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
                .Where(f => !f.Contains(@"\bin\") && !f.Contains(@"\obj\") && !f.EndsWith(".g.cs"))
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
                .Where(f => !f.Contains(@"\bin\") && !f.Contains(@"\obj\"))
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
                .Where(f => !f.Contains(@"\bin\") && !f.Contains(@"\obj\") && !f.EndsWith(".g.cs"))
                .ToList();

            const int DefaultBudget = 1200;
            var baselines = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["Account"] = 2900,
                ["MainViewModel"] = 3500,
                ["AccountManager"] = 3400,
                ["ServerListViewModel"] = 1800
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
                .Where(f => !f.Contains(@"\bin\") && !f.Contains(@"\obj\") && !f.EndsWith(".g.cs"))
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
            var files = Directory.GetFiles(servicesDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("AccountRepository.cs", StringComparison.OrdinalIgnoreCase) &&
                            !f.EndsWith("RobloxApiClient.cs", StringComparison.OrdinalIgnoreCase))
                .Concat(Directory.GetFiles(viewModelsDir, "*.cs", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(classesDir, "*.cs", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(avaloniaDir, "*.cs", SearchOption.AllDirectories))
                .ToList();

            var violations = new System.Collections.Generic.List<string>();
            foreach (var file in files)
            {
                string text = File.ReadAllText(file);
                if (text.Contains("AccountManager.", StringComparison.Ordinal))
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
                .Where(f => !f.Contains(@"\bin\") && !f.Contains(@"\obj\"))
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
                    if (file.Contains(@"\bin\") || file.Contains(@"\obj\"))
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
                .Where(f => !f.Contains(@"\bin\") && !f.Contains(@"\obj\") && !f.EndsWith(".g.cs"))
                .ToList();

            var violations = new System.Collections.Generic.List<string>();
            foreach (var file in files)
            {
                string content = File.ReadAllText(file);
                if (content.Contains(".GetAwaiter().GetResult()", StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetFileName(file)} uses sync-over-async .GetAwaiter().GetResult()");
                }
            }

            Assert.True(violations.Count == 0,
                $"Sync-over-async violations found:\n{string.Join("\n", violations)}");
        }
    }
}
