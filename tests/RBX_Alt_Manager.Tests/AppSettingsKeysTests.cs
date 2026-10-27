using System;
using System.Collections.Generic;
using System.Reflection;
using RBX_Alt_Manager.Services;
using Xunit;

namespace RBX_Alt_Manager.Tests
{
    public class AppSettingsKeysTests
    {
        [Fact]
        public void Sections_AreDefinedAndNonEmpty()
        {
            Assert.Equal("General", AppSettingsKeys.Sections.General);
            Assert.Equal("Developer", AppSettingsKeys.Sections.Developer);
            Assert.Equal("WebServer", AppSettingsKeys.Sections.WebServer);
            Assert.Equal("AccountControl", AppSettingsKeys.Sections.AccountControl);
            Assert.Equal("Watcher", AppSettingsKeys.Sections.Watcher);
            Assert.Equal("Prompts", AppSettingsKeys.Sections.Prompts);
        }

        [Fact]
        public void GeneralKeys_AreUniqueAndValid()
        {
            var fields = typeof(AppSettingsKeys.General).GetFields(BindingFlags.Public | BindingFlags.Static);
            var set = new HashSet<string>();

            foreach (var field in fields)
            {
                string value = field.GetValue(null) as string;
                Assert.False(string.IsNullOrWhiteSpace(value), $"Field {field.Name} has empty value");
                Assert.True(set.Add(value), $"Duplicate key detected: {value}");
            }
        }

        [Fact]
        public void FastFlagKeys_FollowStandardNamingPrefix()
        {
            Assert.StartsWith("FastFlag_", AppSettingsKeys.General.FastFlag_DisableShadows);
            Assert.StartsWith("FastFlag_", AppSettingsKeys.General.FastFlag_DisableParticles);
            Assert.StartsWith("FastFlag_", AppSettingsKeys.General.FastFlag_DisablePostFx);
            Assert.StartsWith("FastFlag_", AppSettingsKeys.General.FastFlag_DisableGrass);
            Assert.StartsWith("FastFlag_", AppSettingsKeys.General.FastFlag_LowResTextures);
            Assert.StartsWith("FastFlag_", AppSettingsKeys.General.FastFlag_DisableLightGrid);
        }

        [Fact]
        public void WatcherKeys_AreUniqueAndValid()
        {
            var fields = typeof(AppSettingsKeys.Watcher).GetFields(BindingFlags.Public | BindingFlags.Static);
            var set = new HashSet<string>();

            foreach (var field in fields)
            {
                string value = field.GetValue(null) as string;
                Assert.False(string.IsNullOrWhiteSpace(value));
                Assert.True(set.Add(value), $"Duplicate watcher key: {value}");
            }
        }
    }
}
