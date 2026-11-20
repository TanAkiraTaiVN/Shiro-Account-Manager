#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NetArchTest.Rules;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    public partial class ArchitectureTests
    {
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
                .Or()
                .HaveNameEndingWith("Browser")
                .Or()
                .HaveNameEndingWith("Config")
                .Or()
                .HaveNameEndingWith("Login")
                .Or()
                .HaveNameEndingWith("Signup")
                .Or()
                .HaveName("Batch")
                .Or()
                .HaveNameEndingWith("Patcher")
                .Or()
                .HaveNameEndingWith("Presence")
                .Or()
                .HaveNameEndingWith("Envelope")
                .Or()
                .HaveNameEndingWith("Entry")
                .Or()
                .HaveName("Cryptography")
                .Or()
                .HaveNameEndingWith("Protector")
                .Or()
                .HaveNameEndingWith("Process")
                .Or()
                .HaveNameEndingWith("Watcher")
                .Or()
                .HaveNameEndingWith("Mapper")
                .Or()
                .HaveName("Utilities")
                .GetResult();

            var failingNames = result.FailingTypes?.Select(t => t.FullName) ?? Enumerable.Empty<string>();
            Assert.True(result.IsSuccessful, $"Services naming convention check failed. Violations: {string.Join(", ", failingNames)}");
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
            var domainDir = Path.Combine(repoRoot!, "src", "Shiro", "Domain");
            var avaloniaDir = Path.Combine(repoRoot!, "src", "Shiro", "AvaloniaUI");
            var files = Directory.GetFiles(servicesDir, "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(viewModelsDir, "*.cs", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(domainDir, "*.cs", SearchOption.AllDirectories))
                .Concat(Directory.GetFiles(avaloniaDir, "*.cs", SearchOption.AllDirectories))
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
        public void AmbientStaticAccess_ShouldNotExceedPerFolderBaseline()
        {
            var baseline = new Dictionary<string, (int Logger, int Settings)>
            {
                ["Services/Network"] = (0, 0),
                ["Services/Auth"] = (0, 0),
                ["Services/Storage"] = (0, 0),
                ["ViewModels"] = (0, 0),
                // locked at current baseline; may only go down:
                ["Domain"] = (0, 0),
                ["Services/Launch"] = (0, 0),
                ["Services/Windows"] = (0, 0),
                ["Services/Ui"] = (0, 0),
                ["AvaloniaUI/Views"] = (0, 0),
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
        public void Services_ShouldNotDependOn_ViewModels()
        {
            var allowFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AccountWorkflowService.cs",
                "ISettingsSideEffects.cs",
                "ToastService.cs"
            };
            var servicesDir = Path.Combine(RepoRoot, "src", "Shiro", "Services");
            var sourceOffenders = Directory.GetFiles(servicesDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !allowFiles.Contains(Path.GetFileName(f)))
                .Where(f => Regex.IsMatch(
                    SourceScan.StripCommentsAndStringLiterals(File.ReadAllText(f)),
                    @"\bShiro\.ViewModels\b"))
                .Select(Path.GetFileName)
                .ToList();

            Assert.Empty(sourceOffenders);

            var allowTypePrefixes = new HashSet<string>(StringComparer.Ordinal)
            {
                "Shiro.Services.AccountWorkflowService",
                "Shiro.Services.SettingsSideEffectsService",
                "Shiro.Services.ToastItem",
                "Shiro.Services.ToastService"
            };
            var archResult = Types.InAssembly(CoreAssembly)
                .That()
                .ResideInNamespace("Shiro.Services")
                .ShouldNot()
                .HaveDependencyOnAny("Shiro.ViewModels")
                .GetResult();
            var ilOffenders = (archResult.FailingTypes?.Select(t => t.FullName ?? t.Name) ?? Enumerable.Empty<string>())
                .Where(name => !allowTypePrefixes.Contains(name.Split('+', '/')[0]))
                .ToList();

            Assert.Empty(ilOffenders);
        }
    }
}
