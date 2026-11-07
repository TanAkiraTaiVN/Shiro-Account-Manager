#nullable enable
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;

namespace Shiro.Services
{
    public static partial class LocalizationService
    {
        private static bool _coreKeysLoaded;
        private static FrozenDictionary<string, string> _frozenEnStrings = null!;
        private static FrozenDictionary<string, string> _frozenViStrings = null!;
        private static ResourceDictionary _enResourceDict = null!;
        private static ResourceDictionary _viResourceDict = null!;
        private static ResourceDictionary? _activeResourceDict;

        static LocalizationService()
        {
            EnsureCoreKeys();
        }

        public static void EnsureCoreKeys()
        {
            if (_coreKeysLoaded) return;
            _coreKeysLoaded = true;

            _frozenEnStrings = LoadStringsFromResource("en.json");
            _frozenViStrings = LoadStringsFromResource("vi.json");

            var enRes = new ResourceDictionary();
            foreach (var kvp in _frozenEnStrings)
            {
                enRes[kvp.Key] = kvp.Value;
                enRes["Loc_" + kvp.Key] = kvp.Value;
            }
            _enResourceDict = enRes;

            var viRes = new ResourceDictionary();
            foreach (var kvp in _frozenViStrings)
            {
                viRes[kvp.Key] = kvp.Value;
                viRes["Loc_" + kvp.Key] = kvp.Value;
            }
            _viResourceDict = viRes;
        }

        private static FrozenDictionary<string, string> LoadStringsFromResource(string resourceSuffix)
        {
            try
            {
                var assembly = typeof(LocalizationService).Assembly;
                string? resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));

                if (resourceName != null)
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        var dict = JsonSerializer.Deserialize(stream, ShiroAppJson.Default.DictionaryStringString);
                        if (dict != null)
                            return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Logger?.Error($"Failed to load embedded localization resource ending with {resourceSuffix}: {ex.Message}");
            }

            return FrozenDictionary<string, string>.Empty;
        }
    }
}
