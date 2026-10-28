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
        public void NoSourceCodeFiles_ShouldExceed500Lines()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string? repoRoot = null;
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "RBX Alt Manager.sln")) ||
                    File.Exists(Path.Combine(dir.FullName, "Shiro.sln")))
                {
                    repoRoot = dir.FullName;
                    break;
                }
                dir = dir.Parent;
            }

            Assert.NotNull(repoRoot);
            var appDir = Path.Combine(repoRoot!, "src", "Shiro");
            if (!Directory.Exists(appDir))
                appDir = Path.Combine(repoRoot!, "RBX Alt Manager");

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
                .HaveNameEndingWith("Parser")
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
                .HaveName("AppServices")
                .GetResult();

            var failingNames = result.FailingTypes?.Select(t => t.FullName) ?? Enumerable.Empty<string>();
            Assert.True(result.IsSuccessful, $"Services naming convention check failed. Violations: {string.Join(", ", failingNames)}");
        }
    }
}
