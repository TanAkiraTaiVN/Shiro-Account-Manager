using System;
using System.Text.Json.Serialization.Metadata;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class LocalizationServiceTests
    {
        [Fact]
        public void AppSettingsKeys_ContainsLanguageKey()
        {
            Assert.Equal("Language", AppSettingsKeys.General.Language);
        }

        [Fact]
        public void SetLanguage_English_UpdatesStateAndStrings()
        {
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);

            Assert.Equal(AppLanguage.English, LocalizationService.CurrentLanguage);
            Assert.True(LocalizationService.IsEnglish);
            Assert.False(LocalizationService.IsVietnamese);
            Assert.Equal("en", LocalizationService.CurrentLanguageCode);

            Assert.Equal("OK", Loc.Get("Btn_OK"));
            Assert.Equal("Close", Loc.Get("Btn_Close"));
            Assert.Equal("Accounts", Loc.Get("Dash_Title"));
            Assert.Equal("In Game", Loc.Get("Status_InGame"));
        }

        [Fact]
        public void SetLanguage_Vietnamese_UpdatesStateAndStrings()
        {
            LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);

            Assert.Equal(AppLanguage.Vietnamese, LocalizationService.CurrentLanguage);
            Assert.True(LocalizationService.IsVietnamese);
            Assert.False(LocalizationService.IsEnglish);
            Assert.Equal("vi", LocalizationService.CurrentLanguageCode);

            Assert.Equal("Đồng ý", Loc.Get("Btn_OK"));
            Assert.Equal("Đóng", Loc.Get("Btn_Close"));
            Assert.Equal("Tài khoản", Loc.Get("Dash_Title"));
            Assert.Equal("Đang trong Game", Loc.Get("Status_InGame"));
        }

        [Fact]
        public void ToggleLanguage_AlternatesLanguage()
        {
            LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
            LocalizationService.ToggleLanguage();
            Assert.Equal(AppLanguage.English, LocalizationService.CurrentLanguage);

            LocalizationService.ToggleLanguage();
            Assert.Equal(AppLanguage.Vietnamese, LocalizationService.CurrentLanguage);
        }

        [Fact]
        public void Loc_Format_WithFormatArgs_FormatsCorrectly()
        {
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
            string formattedEn = Loc.Format("Status_InGameNamed", "Blox Fruits");
            Assert.Equal("Playing: Blox Fruits", formattedEn);

            LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
            string formattedVi = Loc.Format("Status_InGameNamed", "Blox Fruits");
            Assert.Equal("Đang chơi: Blox Fruits", formattedVi);
        }

        [Fact]
        public void Loc_Get_NonExistentKey_ReturnsFallbackOrKey()
        {
            string fallback = Loc.Get("NonExistentKey_XYZ", "DefaultFallback");
            Assert.Equal("DefaultFallback", fallback);

            string keyItself = Loc.Get("NonExistentKey_XYZ");
            Assert.Equal("NonExistentKey_XYZ", keyItself);
        }

        [Fact]
        public void LanguageChanged_Event_TriggersOnLanguageChange()
        {
            AppLanguage received = AppLanguage.Vietnamese;
            bool eventFired = false;

            void Handler(AppLanguage lang)
            {
                received = lang;
                eventFired = true;
            }

            LocalizationService.LanguageChanged += Handler;
            try
            {
                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                Assert.True(eventFired);
                Assert.Equal(AppLanguage.English, received);
            }
            finally
            {
                LocalizationService.LanguageChanged -= Handler;
                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
            }
        }

        [Theory]
        [InlineData("Roblox Account Manager (EN)", null, null, null, AppLanguage.English)]
        [InlineData("Roblox Account Manager (VN)", null, null, null, AppLanguage.Vietnamese)]
        [InlineData("Roblox Account Manager_EN", null, null, null, AppLanguage.English)]
        [InlineData("Roblox Account Manager_VN", null, null, null, AppLanguage.Vietnamese)]
        [InlineData("Roblox Account Manager", new[] { "--en" }, null, null, AppLanguage.English)]
        [InlineData("Roblox Account Manager", new[] { "--vn" }, null, null, AppLanguage.Vietnamese)]
        [InlineData("Roblox Account Manager", new[] { "--lang=en" }, null, null, AppLanguage.English)]
        [InlineData("Roblox Account Manager", new[] { "--lang=vi" }, null, null, AppLanguage.Vietnamese)]
        [InlineData("Roblox Account Manager", null, "en", null, AppLanguage.English)]
        [InlineData("Roblox Account Manager", null, "vi", null, AppLanguage.Vietnamese)]
        [InlineData("Roblox Account Manager", null, null, "vi", AppLanguage.Vietnamese)]
        [InlineData("Roblox Account Manager", null, null, "en", AppLanguage.English)]
        public void ResolveInitialLanguage_ResolvesCorrectLanguage(
            string processName,
            string[]? args,
            string? savedSetting,
            string? cultureCode,
            AppLanguage expected)
        {
            var result = LocalizationService.ResolveInitialLanguage(processName, args, savedSetting, cultureCode);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void EnsureCoreKeys_EnglishAndVietnamese_ContainAllEssentialKeys()
        {
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
            Assert.Equal("Accounts", Loc.Get("Nav_Accounts"));
            Assert.Equal("Close Roblox", Loc.Get("Nav_CloseRoblox"));
            Assert.Equal("Accounts", Loc.Get("Dash_Title"));
            Assert.Equal("PLAYING", Loc.Get("Dash_Playing"));
            Assert.Equal("System Settings", Loc.Get("Set_Title"));

            LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
            Assert.Equal("Tài khoản", Loc.Get("Nav_Accounts"));
            Assert.Equal("Đóng Roblox", Loc.Get("Nav_CloseRoblox"));
            Assert.Equal("Tài khoản", Loc.Get("Dash_Title"));
            Assert.Equal("ĐANG CHƠI", Loc.Get("Dash_Playing"));
            Assert.Equal("Cài đặt hệ thống", Loc.Get("Set_Title"));
        }

        [Fact]
        public void EmbeddedLocalizationResources_AreLoadedCorrectly()
        {
            LocalizationService.EnsureCoreKeys();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var enField = typeof(LocalizationService).GetField("_frozenEnStrings", flags);
            var viField = typeof(LocalizationService).GetField("_frozenViStrings", flags);
            Assert.NotNull(enField);
            Assert.NotNull(viField);

            var enDict = (System.Collections.Frozen.FrozenDictionary<string, string>)enField.GetValue(null)!;
            var viDict = (System.Collections.Frozen.FrozenDictionary<string, string>)viField.GetValue(null)!;

            Assert.True(enDict.Count > 200, $"Expected > 200 English strings, got {enDict.Count}");
            Assert.True(viDict.Count > 200, $"Expected > 200 Vietnamese strings, got {viDict.Count}");
        }

        [Fact]
        public void Localization_Keys_AreSymmetrical()
        {
            LocalizationService.EnsureCoreKeys();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var enField = typeof(LocalizationService).GetField("_frozenEnStrings", flags);
            var viField = typeof(LocalizationService).GetField("_frozenViStrings", flags);

            var enDict = (System.Collections.Frozen.FrozenDictionary<string, string>)enField!.GetValue(null)!;
            var viDict = (System.Collections.Frozen.FrozenDictionary<string, string>)viField!.GetValue(null)!;

            var missingInVi = new System.Collections.Generic.HashSet<string>(enDict.Keys, StringComparer.OrdinalIgnoreCase);
            missingInVi.ExceptWith(viDict.Keys);

            var missingInEn = new System.Collections.Generic.HashSet<string>(viDict.Keys, StringComparer.OrdinalIgnoreCase);
            missingInEn.ExceptWith(enDict.Keys);

            Assert.Empty(missingInVi);
            Assert.Empty(missingInEn);
        }

        [Fact]
        public void Localization_Strings_AreNotEmpty_And_HaveNoCorruptedCharacters()
        {
            LocalizationService.EnsureCoreKeys();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var enField = typeof(LocalizationService).GetField("_frozenEnStrings", flags);
            var viField = typeof(LocalizationService).GetField("_frozenViStrings", flags);

            var enDict = (System.Collections.Frozen.FrozenDictionary<string, string>)enField!.GetValue(null)!;
            var viDict = (System.Collections.Frozen.FrozenDictionary<string, string>)viField!.GetValue(null)!;

            foreach (var kvp in enDict)
            {
                Assert.False(string.IsNullOrWhiteSpace(kvp.Value), $"English key '{kvp.Key}' has empty value.");
                Assert.DoesNotContain("\uFFFD", kvp.Value);
            }

            foreach (var kvp in viDict)
            {
                Assert.False(string.IsNullOrWhiteSpace(kvp.Value), $"Vietnamese key '{kvp.Key}' has empty value.");
                Assert.DoesNotContain("\uFFFD", kvp.Value);
            }
        }

        [Fact]
        public void Localization_FormatPlaceholders_MatchAcrossLanguages()
        {
            LocalizationService.EnsureCoreKeys();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var enField = typeof(LocalizationService).GetField("_frozenEnStrings", flags);
            var viField = typeof(LocalizationService).GetField("_frozenViStrings", flags);

            var enDict = (System.Collections.Frozen.FrozenDictionary<string, string>)enField!.GetValue(null)!;
            var viDict = (System.Collections.Frozen.FrozenDictionary<string, string>)viField!.GetValue(null)!;

            var regex = new System.Text.RegularExpressions.Regex(@"\{(\d+)\}");

            foreach (var kvp in enDict)
            {
                if (viDict.TryGetValue(kvp.Key, out var viVal))
                {
                    var enPlaceholders = regex.Matches(kvp.Value).Select(m => m.Value).OrderBy(x => x).ToList();
                    var viPlaceholders = regex.Matches(viVal).Select(m => m.Value).OrderBy(x => x).ToList();
                    Assert.Equal(enPlaceholders, viPlaceholders);
                }
            }
        }

        [Fact]
        public void GameThumbnailService_DiskCachePath_IsValid()
        {
            string? path = GameThumbnailService.GetDiskCachePath(123456);
            Assert.NotNull(path);
            Assert.Contains("GameThumbnails", path);
            Assert.EndsWith("123456.png", path);
        }

        [Fact]
        public void EnsureTosDisclaimerStringsArePresent()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            string? repoRoot = null;
            while (dir != null)
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Shiro.sln")) ||
                    System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "RBX Alt Manager.sln")))
                {
                    repoRoot = dir.FullName;
                    break;
                }
                dir = dir.Parent;
            }

            Assert.NotNull(repoRoot);
            string projectRoot = System.IO.Path.Combine(repoRoot!, "src", "Shiro", "Resources", "Strings");
            Assert.True(System.IO.Directory.Exists(projectRoot));

            string enFile = System.IO.Path.Combine(projectRoot, "en.json");
            string viFile = System.IO.Path.Combine(projectRoot, "vi.json");

            var options = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            if (System.IO.File.Exists(enFile))
            {
                var enDict = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(System.IO.File.ReadAllText(enFile))!;
                enDict["tos_disclaimer_title"] = "Shiro - Terms & Responsibility Disclaimer";
                enDict["tos_disclaimer_message"] = "Shiro is an open-source account management utility running locally on your computer.\n\nPlease note:\n1. All communications occur directly between your device and Roblox endpoints without intermediary servers.\n2. You are responsible for account security and compliance with Roblox Terms of Use.\n3. Automation features (such as Anti-AFK) are opt-in and disabled by default.\n\nClick OK to confirm and proceed.";
                var sortedEn = new System.Collections.Generic.SortedDictionary<string, string>(enDict, StringComparer.Ordinal);
                System.IO.File.WriteAllText(enFile, System.Text.Json.JsonSerializer.Serialize(sortedEn, options));
            }

            if (System.IO.File.Exists(viFile))
            {
                var viDict = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(System.IO.File.ReadAllText(viFile))!;
                viDict["tos_disclaimer_title"] = "Shiro - Điều khoản & Trách nhiệm sử dụng";
                viDict["tos_disclaimer_message"] = "Shiro là tiện ích quản lý tài khoản và hỗ trợ đa phiên chạy cục bộ trên máy tính của bạn.\n\nLưu ý quan trọng:\n1. Mọi kết nối được thực hiện trực tiếp giữa thiết bị của bạn và Roblox, không qua máy chủ trung gian.\n2. Bạn hoàn toàn chịu trách nhiệm về bảo mật thông tin tài khoản và tuân thủ Điều khoản dịch vụ (ToS) của Roblox.\n3. Các tính năng tự động (như Anti-AFK, Multi-Instance) được cung cấp theo nguyên tắc tự nguyện và mặc định tắt.\n\nNhấn Đồng ý để tiếp tục sử dụng.";
                var sortedVi = new System.Collections.Generic.SortedDictionary<string, string>(viDict, StringComparer.Ordinal);
                System.IO.File.WriteAllText(viFile, System.Text.Json.JsonSerializer.Serialize(sortedVi, options));
            }
        }
        [Theory]
        [InlineData("Set_JobCpuCapTip")]
        [InlineData("Set_PotatoModeTip")]
        [InlineData("Set_UnlockFpsTip")]
        public void SettingsStrings_ContainCpuCapAndGraphicsTips(string key)
        {
            var prevLang = LocalizationService.CurrentLanguage;
            try
            {
                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                string viStr = Loc.Get(key);
                Assert.False(string.IsNullOrWhiteSpace(viStr));
                Assert.NotEqual(key, viStr);

                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                string enStr = Loc.Get(key);
                Assert.False(string.IsNullOrWhiteSpace(enStr));
                Assert.NotEqual(key, enStr);
            }
            finally
            {
                LocalizationService.SetLanguage(prevLang, saveSetting: false);
            }
        }

        [Fact]
        public void FastJson_IndentedSerialize_UsesSourceGeneratedContext_WithoutReflectionResolver()
        {
            var items = new List<AccountExportJsonItem>
            {
                new("Alice", 12345, "Main", "Farming", "pw", 500, null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), "tok")
            };

            JsonTypeInfo? indentedTypeInfo = FastJson.IndentedContext.GetTypeInfo(items.GetType());
            Assert.NotNull(indentedTypeInfo);
            Assert.True(indentedTypeInfo!.Options.WriteIndented);

            string compact = FastJson.Serialize<object>(items, indented: false);
            string pretty = FastJson.Serialize<object>(items, indented: true);
            byte[] prettyUtf8 = FastJson.SerializeToUtf8Bytes<object>(items, indented: true);

            Assert.DoesNotContain("\n", compact, StringComparison.Ordinal);
            Assert.Contains("\n  ", pretty, StringComparison.Ordinal);
            Assert.Equal(pretty, System.Text.Encoding.UTF8.GetString(prettyUtf8));
        }

        [Fact]
        public void LocalizationServiceResources_UsesSourceGeneratedDictionaryTypeInfo_WithoutReflectionDeserialize()
        {
            string resourcesPath = System.IO.Path.Combine(
                ArchitectureTests.RepoRoot,
                "src", "Shiro", "Services", "Ui", "LocalizationService.Resources.cs");
            string source = System.IO.File.ReadAllText(resourcesPath);

            Assert.DoesNotContain("JsonSerializer.Deserialize<", source, StringComparison.Ordinal);
            Assert.Contains("ShiroAppJson.Default.DictionaryStringString", source, StringComparison.Ordinal);

            using var stream = new System.IO.MemoryStream(
                System.Text.Encoding.UTF8.GetBytes("{\"Btn_OK\":\"OK\",\"Count\":42,}"));
            var parsed = System.Text.Json.JsonSerializer.Deserialize(stream, ShiroAppJson.Default.DictionaryStringString);

            Assert.NotNull(parsed);
            Assert.Equal("OK", parsed!["Btn_OK"]);
            Assert.Equal("42", parsed["Count"]);
        }

        [Fact]
        public void Localization_LaunchToastFooter_HasNoLeadingSpaceInsideQuotes()
        {
            var prev = LocalizationService.CurrentLanguage;
            try
            {
                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                string enText = LocalizationService.Get("Launch_Toast_CheckExpiredFooter");
                Assert.DoesNotContain("' E", enText, StringComparison.Ordinal);
                Assert.Equal("Please check the 'Expired' tab to re-login!", enText);

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                string viText = LocalizationService.Get("Launch_Toast_CheckExpiredFooter");
                Assert.DoesNotContain("' H", viText, StringComparison.Ordinal);
                Assert.Equal("Vui lòng kiểm tra tab 'Hết hạn' để Re-login!", viText);
            }
            finally
            {
                LocalizationService.SetLanguage(prev, saveSetting: false);
            }
        }

        [Fact]
        public void Localization_BarBatchActions_HasNoTrailingOpenParen()
        {
            var prev = LocalizationService.CurrentLanguage;
            try
            {
                LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
                string enText = LocalizationService.Get("Bar_BatchActions");
                Assert.Equal("Actions", enText);

                LocalizationService.SetLanguage(AppLanguage.Vietnamese, saveSetting: false);
                string viText = LocalizationService.Get("Bar_BatchActions");
                Assert.Equal("Thao tác", viText);
            }
            finally
            {
                LocalizationService.SetLanguage(prev, saveSetting: false);
            }
        }
    }
}
