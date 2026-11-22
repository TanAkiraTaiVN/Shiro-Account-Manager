#nullable enable
using System;

namespace Shiro.Services
{
    internal sealed class SettingsService
    {
        private readonly System.Threading.Lock sync = new();
        private readonly IAppLogger _logger;
        public SettingsDocument Document { get; }
        public IniFile File => Document.BackingIni;

        public SettingsService(IniFile file, IAppLogger? logger = null)
            : this(new SettingsDocument(file ?? throw new ArgumentNullException(nameof(file)), "RAMSettings.json"), logger)
        {
        }

        public SettingsService(SettingsDocument document, IAppLogger? logger = null)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            _logger = logger ?? AppServices.Logger;
        }

        public T? Get<T>(string section, string key, T? fallback = default(T))
        {
            lock (sync)
            {
                IniSection? target = File.Section(section);
                if (target == null || !target.Exists(key))
                    return fallback;

                try { return target.Get<T>(key); }
                catch (Exception ex)
                {
                    _logger.Debug($"SettingsService parse '{key}' in '{section}' failed: {ex.Message}");
                    return fallback;
                }
            }
        }

        public void Set(string section, string key, string value)
        {
            lock (sync)
                Document.Set(section, key, value);
        }

        public void Save(string fileName = "RAMSettings.json")
        {
            lock (sync)
            {
                if (string.Equals(fileName, Document.FilePath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, "RAMSettings.json", StringComparison.OrdinalIgnoreCase))
                {
                    Document.Save();
                }
                else
                {
                    File.Save(fileName);
                }
            }
        }
    }
}
