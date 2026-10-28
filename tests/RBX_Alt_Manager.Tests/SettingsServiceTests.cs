using System;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class SettingsServiceTests
    {
        [Fact]
        public void SettingKeys_HaveExpectedKeyNames()
        {
            Assert.Equal("General", SettingKeys.GeneralSection);
            Assert.Equal("UnlockFPS", SettingKeys.UnlockFPS);
            Assert.Equal("MaxFPSValue", SettingKeys.MaxFPSValue);
            Assert.Equal("EnablePotatoMode", SettingKeys.EnablePotatoMode);
            Assert.Equal("HideUsernames", SettingKeys.HideUsernames);
            Assert.Equal("AccountJoinDelay", SettingKeys.AccountJoinDelay);
            Assert.Equal("AutoCleanZombiesOnLaunch", SettingKeys.AutoCleanZombiesOnLaunch);
            Assert.Equal("AutoCleanZombiesOnLaunch", AppSettingsKeys.General.AutoCleanZombiesOnLaunch);
        }

        [Fact]
        public void IniSection_TypedGetWithDefault_WorksCorrectly()
        {
            var ini = new IniFile();
            var section = ini["General"];

            // Missing keys return provided default
            Assert.True(section.Get<bool>("NonExistentBool", true));
            Assert.False(section.Get<bool>("NonExistentBool2", false));
            Assert.Equal(123, section.Get<int>("NonExistentInt", 123));
            Assert.Equal(4.56, section.Get<double>("NonExistentDouble", 4.56));
            Assert.Equal("Fallback", section.Get<string>("NonExistentStr", "Fallback"));

            // Existing keys parse correctly
            section.Set("TestBoolTrue", "True");
            section.Set("TestBoolFalse", "false");
            section.Set("TestInt", "789");
            section.Set("TestDouble", "3.1415");
            section.Set("TestString", "HelloRAM");

            Assert.True(section.Get<bool>("TestBoolTrue", false));
            Assert.False(section.Get<bool>("TestBoolFalse", true));
            Assert.Equal(789, section.Get<int>("TestInt", 0));
            Assert.Equal(3.1415, section.Get<double>("TestDouble", 0.0), 3);
            Assert.Equal("HelloRAM", section.Get<string>("TestString", ""));
        }

        [Fact]
        public void SettingsService_GetWithMissingKey_ReturnsFallback()
        {
            var ini = new IniFile();
            var service = new SettingsService(ini);

            bool boolVal = service.Get<bool>("General", "NonExistentKey123", true);
            int intVal = service.Get<int>("General", "NonExistentIntKey", 42);
            string strVal = service.Get<string>("General", "NonExistentStrKey", "DefaultString");

            Assert.True(boolVal);
            Assert.Equal(42, intVal);
            Assert.Equal("DefaultString", strVal);
        }

        [Fact]
        public void SettingsService_SetAndGet_PersistsValues()
        {
            var ini = new IniFile();
            var service = new SettingsService(ini);

            service.Set("General", "CustomTestKey", "999");
            int val = service.Get<int>("General", "CustomTestKey", 0);

            Assert.Equal(999, val);
        }

        [Fact]
        public void IniFile_ToJsonString_SerializesFormattedJson()
        {
            var ini = new IniFile();
            ini.Section("General").Set("UnlockFPS", "true");
            ini.Section("General").Set("MaxFPSValue", "240");
            ini.Section("Developer").Set("Debug", "false");

            string json = ini.ToJsonString();

            Assert.Contains("\"General\"", json);
            Assert.Contains("\"UnlockFPS\": \"true\"", json);
            Assert.Contains("\"MaxFPSValue\": \"240\"", json);
            Assert.Contains("\"Developer\"", json);
        }

        [Fact]
        public void IniFile_LoadFromString_ParsesJsonCorrectly()
        {
            string json = """
            {
              "General": {
                "UnlockFPS": "true",
                "MaxFPSValue": "165"
              },
              "WebServer": {
                "Port": "8080"
              }
            }
            """;

            var ini = new IniFile();
            ini.LoadFromString(json);

            Assert.True(ini.Section("General").Get<bool>("UnlockFPS", false));
            Assert.Equal(165, ini.Section("General").Get<int>("MaxFPSValue", 0));
            Assert.Equal(8080, ini.Section("WebServer").Get<int>("Port", 0));
        }
    }
}
