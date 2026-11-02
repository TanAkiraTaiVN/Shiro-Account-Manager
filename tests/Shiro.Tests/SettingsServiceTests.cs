using System;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class SettingsServiceTests
    {
        [Fact]
        public void AppSettingsKeys_HaveExpectedKeyNames()
        {
            Assert.Equal("General", AppSettingsKeys.Sections.General);
            Assert.Equal("UnlockFPS", AppSettingsKeys.General.UnlockFPS);
            Assert.Equal("MaxFPSValue", AppSettingsKeys.General.MaxFPSValue);
            Assert.Equal("EnablePotatoMode", AppSettingsKeys.General.EnablePotatoMode);
            Assert.Equal("HideUsernames", AppSettingsKeys.General.HideUsernames);
            Assert.Equal("AccountJoinDelay", AppSettingsKeys.General.AccountJoinDelay);
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

        private sealed class FakeSettingsService : ISettingsService
        {
            private readonly System.Collections.Generic.Dictionary<string, string> _store =
                new(StringComparer.OrdinalIgnoreCase);

            public string? Get(string name) => _store.TryGetValue(name, out var val) ? val : null;

            public T? Get<T>(string name, T? defaultValue = default)
            {
                if (!_store.TryGetValue(name, out var val)) return defaultValue;
                try
                {
                    if (typeof(T) == typeof(bool)) return (T)(object)bool.Parse(val);
                    if (typeof(T) == typeof(int)) return (T)(object)int.Parse(val);
                    if (typeof(T) == typeof(string)) return (T)(object)val;
                    return (T?)Convert.ChangeType(val, typeof(T));
                }
                catch
                {
                    return defaultValue;
                }
            }

            public bool GetBool(string name, bool defaultValue = false) =>
                _store.TryGetValue(name, out var val) && bool.TryParse(val, out var b) ? b : defaultValue;

            public int GetInt(string name, int defaultValue = 0) =>
                _store.TryGetValue(name, out var val) && int.TryParse(val, out var i) ? i : defaultValue;

            public decimal GetDecimal(string name, decimal defaultValue = 0m) =>
                _store.TryGetValue(name, out var val) && decimal.TryParse(val, out var d) ? d : defaultValue;

            public bool Exists(string name) => _store.ContainsKey(name);

            public void Set(string name, string? value, string? comment = null)
            {
                if (value == null) _store.Remove(name);
                else _store[name] = value;
            }

            public void RemoveProperty(string name) => _store.Remove(name);
        }

        [Fact]
        public void AppSettings_GeneralOverride_IsolatesTestConfiguration()
        {
            var fake = new FakeSettingsService();
            fake.Set("TestKey", "CustomValue");
            fake.Set("TestFlag", "true");

            try
            {
                AppSettings.General = fake;
                Assert.Equal("CustomValue", AppSettings.General.Get("TestKey"));
                Assert.True(AppSettings.General.GetBool("TestFlag"));
                Assert.False(AppSettings.General.GetBool("NonExistent", false));
            }
            finally
            {
                AppSettings.ResetOverrides();
            }
        }
    }
}
