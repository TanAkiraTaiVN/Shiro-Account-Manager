#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class ClientSettingsAndUtilitiesBehaviorTests
    {
        private enum SampleColor
        {
            Red,
            Green,
            Blue
        }

        [Fact]
        public void Utilities_NextEnum_WrapsAroundCorrectly()
        {
            Assert.Equal(SampleColor.Green, SampleColor.Red.Next());
            Assert.Equal(SampleColor.Blue, SampleColor.Green.Next());
            Assert.Equal(SampleColor.Red, SampleColor.Blue.Next());
        }

        [Fact]
        public void Utilities_Clamp_ConstrainsValueWithinBounds()
        {
            Assert.Equal(10, 5.Clamp(10, 20));
            Assert.Equal(20, 25.Clamp(10, 20));
            Assert.Equal(15, 15.Clamp(10, 20));
        }

        [Fact]
        public void Utilities_FastJson_RoundTripsObject()
        {
            var source = new IniProperty { Name = "TestKey", Value = "TestVal" };
            string json = FastJson.Serialize(source, indented: false);
            Assert.Contains("TestKey", json);

            bool success = FastJson.TryDeserialize<IniProperty>(json, out var deserialized);
            Assert.True(success);
            Assert.Equal("TestKey", deserialized?.Name);
            Assert.Equal("TestVal", deserialized?.Value);
        }

        [Fact]
        public void Utilities_TryParseJson_WithValidAndInvalidInputs()
        {
            string valid = "{\"Name\":\"Key1\",\"Value\":\"Val1\"}";
            Assert.True(valid.TryParseJson<IniProperty>(out var p1));
            Assert.Equal("Key1", p1?.Name);

            string invalid = "not a json string";
            Assert.False(invalid.TryParseJson<IniProperty>(out var p2));
            Assert.Null(p2);
        }

        [Fact]
        public void Utilities_OpenUrl_WithNullOrWhitespace_ReturnsSafely()
        {
            var ex1 = Record.Exception(() => Utilities.OpenUrl(null));
            Assert.Null(ex1);
            var ex2 = Record.Exception(() => Utilities.OpenUrl(""));
            Assert.Null(ex2);
            var ex3 = Record.Exception(() => Utilities.OpenUrl("   "));
            Assert.Null(ex3);
        }

        [Fact]
        public void ClientSettingsPatcher_ApplyFpsLimitToDirectory_UnlocksAndRemovesFps()
        {
            string testFolder = Path.Combine(Path.GetTempPath(), "Shiro-Patcher-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testFolder);
            try
            {
                // 1. Unlock FPS (clamped to 360)
                ClientSettingsPatcher.ApplyFpsLimitToDirectory(testFolder, unlockFps: true, targetFps: 500);
                string jsonPath = Path.Combine(testFolder, "ClientSettings", "ClientAppSettings.json");
                Assert.True(File.Exists(jsonPath));

                string content = File.ReadAllText(jsonPath);
                using (var doc = JsonDocument.Parse(content))
                {
                    Assert.True(doc.RootElement.TryGetProperty(ClientSettingsPatcher.FpsFlagKey, out var prop));
                    Assert.Equal(360, prop.GetInt32());
                }

                // 2. Remove FPS unlock
                ClientSettingsPatcher.ApplyFpsLimitToDirectory(testFolder, unlockFps: false, targetFps: 144);
                Assert.False(File.Exists(jsonPath));
            }
            finally
            {
                if (Directory.Exists(testFolder))
                {
                    try { Directory.Delete(testFolder, true); }
                    catch (IOException) { }
                }
            }
        }

        [Fact]
        public void ClientSettingsPatcher_PurgeSettings_WhenDisabled_DoesNotThrow()
        {
            AppSettings.General.Set("PurgeClientSettings", "false");
            var ex = Record.Exception(() => ClientSettingsPatcher.PurgeSettings());
            Assert.Null(ex);
        }

        [Fact]
        public void IniFile_SectionsPropertiesAndTypeConversions_Work()
        {
            var ini = new IniFile();
            var sec = ini.Section("General");
            sec.Comment = "General settings";
            sec.Set("Port", "8080", "Listening port");
            sec.Set("Enabled", "true");
            sec.Set("Rate", "3.14");

            Assert.True(sec.Exists("Port"));
            Assert.Equal(8080, sec.GetInt("Port"));
            Assert.True(sec.GetBool("Enabled"));
            Assert.Equal(3.14m, sec.GetDecimal("Rate"));
            Assert.Equal("defaultVal", sec.Get("MissingKey", "defaultVal"));

            // Remove property
            sec.RemoveProperty("Port");
            Assert.False(sec.Exists("Port"));

            // Save to TextWriter writes JsonString
            using var sw = new StringWriter();
            ini.Save(sw);
            string written = sw.ToString();
            Assert.Contains("\"General\"", written);
            Assert.Contains("\"Enabled\": \"true\"", written);

            // Remove section
            ini.RemoveSection("General");
            Assert.Null(ini.Sections.FirstOrDefault(s => s.Name == "General"));
        }

        [Fact]
        public async Task RobloxEconomyService_FetchRobuxBalanceAsync_InvalidAccount_ReturnsDefaultBalance()
        {
            var service = RobloxEconomyService.Default;

            long balNull = await service.FetchRobuxBalanceAsync(null!);
            Assert.Equal(0, balNull);

            var accNoId = new Account { UserID = 0, SecurityToken = "tok", Valid = true };
            long balNoId = await service.FetchRobuxBalanceAsync(accNoId);
            Assert.Equal(-1, balNoId);

            var accNoTok = new Account { UserID = 12345, SecurityToken = "", Valid = true };
            long balNoTok = await service.FetchRobuxBalanceAsync(accNoTok);
            Assert.Equal(-1, balNoTok);

            var accInvalid = new Account { UserID = 12345, SecurityToken = "tok", Valid = false };
            long balInvalid = await service.FetchRobuxBalanceAsync(accInvalid);
            Assert.Equal(-1, balInvalid);
        }
    }
}
