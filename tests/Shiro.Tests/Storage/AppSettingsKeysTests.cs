using System;
using System.Collections.Generic;
using System.Reflection;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public class AppSettingsKeysTests
    {
        [Fact]
        public void Sections_AreDefinedAndNonEmpty()
        {
            Assert.Equal("General", AppSettingsKeys.Sections.General);
            Assert.Equal("Developer", AppSettingsKeys.Sections.Developer);
            Assert.Equal("WebServer", AppSettingsKeys.Sections.WebServer);
            Assert.Equal("Watcher", AppSettingsKeys.Sections.Watcher);
            Assert.Equal("Prompts", AppSettingsKeys.Sections.Prompts);
            Assert.Null(typeof(AppSettingsKeys.Sections).GetField("AccountControl", BindingFlags.Public | BindingFlags.Static));
            Assert.Null(typeof(AppSettingsKeys).GetNestedType("AccountControl"));
        }

        [Fact]
        public void GeneralKeys_AreUniqueAndValid()
        {
            var fields = typeof(AppSettingsKeys.General).GetFields(BindingFlags.Public | BindingFlags.Static);
            var set = new HashSet<string>();

            foreach (var field in fields)
            {
                string? value = field.GetValue(null) as string;
                Assert.False(string.IsNullOrWhiteSpace(value), $"Field {field.Name} has empty value");
                Assert.True(set.Add(value), $"Duplicate key detected: {value}");
            }
        }

        [Fact]
        public void RemovedFastFlagKeys_AreAbsent()
        {
            foreach (string key in new[]
            {
                "FastFlag_DisableShadows",
                "FastFlag_DisableParticles",
                "FastFlag_DisablePostFx",
                "FastFlag_DisableGrass",
                "FastFlag_LowResTextures",
                "FastFlag_DisableLightGrid"
            })
            {
                Assert.Null(typeof(AppSettingsKeys.General).GetField(key, BindingFlags.Public | BindingFlags.Static));
            }
        }

        [Fact]
        public void WatcherKeys_AreUniqueAndValid()
        {
            var fields = typeof(AppSettingsKeys.Watcher).GetFields(BindingFlags.Public | BindingFlags.Static);
            var set = new HashSet<string>();

            foreach (var field in fields)
            {
                string? value = field.GetValue(null) as string;
                Assert.False(string.IsNullOrWhiteSpace(value));
                Assert.True(set.Add(value), $"Duplicate watcher key: {value}");
            }
        }
    }
}
