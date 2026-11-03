#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Shiro.Classes;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public class LocalizationIntegrityTests
    {
        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Shiro.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("Shiro.sln not found");
        }

        [Fact]
        public void EveryViKey_HasNonEmptyEnglishValue()
        {
            var repoRoot = FindRepoRoot();
            var stringsDir = Path.Combine(repoRoot, "src", "Shiro", "Resources", "Strings");
            var viJson = File.ReadAllText(Path.Combine(stringsDir, "vi.json"));
            var enJson = File.ReadAllText(Path.Combine(stringsDir, "en.json"));
            var viDict = JsonSerializer.Deserialize<Dictionary<string, string>>(viJson)!;
            var enDict = JsonSerializer.Deserialize<Dictionary<string, string>>(enJson)!;

            var missingOrEmpty = viDict.Keys
                .Where(k => !enDict.TryGetValue(k, out var val) || string.IsNullOrWhiteSpace(val))
                .ToList();

            Assert.Empty(missingOrEmpty);
        }

        [Fact]
        public void AccountViewModel_ViewBanButtonText_FollowsLanguageAndRaisesChange()
        {
            try
            {
                var vm = new AccountViewModel(new Account { Username = "u" });
                var raised = new List<string?>();
                vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                vm.NotifyLanguageChanged();
                Assert.Equal("View Ban", vm.ViewBanButtonText);
                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                vm.NotifyLanguageChanged();
                Assert.Equal("Xem lệnh cấm", vm.ViewBanButtonText);
                Assert.Contains(nameof(AccountViewModel.ViewBanButtonText), raised);
            }
            finally
            {
                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
            }
        }

        [Fact]
        public void Format_WithMismatchedPlaceholdersOrBraces_DoesNotThrow()
        {
            try
            {
                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                // no args -> template returned unformatted
                Assert.Equal("Player '{0}' was not found in current public servers.", LocalizationService.Format("ServerList.PlayerNotFound"));
                // extra args are ignored by string.Format
                Assert.Equal("Player 'a' was not found in current public servers.", LocalizationService.Format("ServerList.PlayerNotFound", "a", "b"));
                // missing key -> key returned (Get falls back to key)
                Assert.Equal("__missing_key__", LocalizationService.Format("__missing_key__", "x"));
                // template with literal brace and an arg -> SafeFormat must not throw
                var ex = Record.Exception(() => LocalizationService.Format("__missing_key__", "{bad", 1));
                Assert.Null(ex);
            }
            finally
            {
                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
            }
        }

        [Fact]
        public void LocalizationService_Format_HandlesMissingKeysAndMismatchedPlaceholdersSafely()
        {
            LocalizationService.EnsureCoreKeys();
            string missing = LocalizationService.Format("NonExistent_Key_999", "fallback {0}", "A", "B");
            Assert.Contains("A", missing);

            string malformed = LocalizationService.Format("NonExistent_Key_999", "bad {0} {5}", "onlyOne");
            Assert.False(string.IsNullOrWhiteSpace(malformed));
        }
    }
}
