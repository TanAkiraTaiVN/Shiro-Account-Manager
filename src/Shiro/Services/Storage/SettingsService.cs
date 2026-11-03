#nullable enable
using System;

namespace Shiro.Services
{
    internal sealed class SettingsService
    {
        private readonly System.Threading.Lock sync = new();
        private readonly IAppLogger _logger;
        public IniFile File { get; }

        public SettingsService(IniFile file, IAppLogger? logger = null)
        {
            File = file ?? throw new ArgumentNullException(nameof(file));
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

        public void Set(string section, string key, string value, string? comment = null)
        {
            lock (sync)
                File.Section(section)?.Set(key, value, comment);
        }

        public void Save(string fileName = "RAMSettings.json")
        {
            lock (sync)
                File.Save(fileName);
        }
    }
}
