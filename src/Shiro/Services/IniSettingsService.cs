#nullable enable
using System;

namespace Shiro.Services
{
    /// <summary>
    /// Thread-safe adapter wrapping an IniSection as an ISettingsService.
    /// </summary>
    public sealed class IniSettingsService : ISettingsService
    {
        private readonly Func<IniSection?>? _sectionProvider;
        private readonly IniSection? _fixedSection;

        public IniSettingsService(IniSection fixedSection)
        {
            _fixedSection = fixedSection ?? throw new ArgumentNullException(nameof(fixedSection));
        }

        public IniSettingsService(Func<IniSection?> sectionProvider)
        {
            _sectionProvider = sectionProvider ?? throw new ArgumentNullException(nameof(sectionProvider));
        }

        private IniSection? Section => _fixedSection ?? _sectionProvider?.Invoke();

        public string? Get(string name) => Section?.Get(name);

        public T? Get<T>(string name, T? defaultValue = default) =>
            Section != null ? Section.Get(name, defaultValue) : defaultValue;

        public bool GetBool(string name, bool defaultValue = false) =>
            Section != null ? Section.GetBool(name, defaultValue) : defaultValue;

        public int GetInt(string name, int defaultValue = 0) =>
            Section != null ? Section.GetInt(name, defaultValue) : defaultValue;

        public decimal GetDecimal(string name, decimal defaultValue = 0m) =>
            Section != null ? Section.GetDecimal(name, defaultValue) : defaultValue;

        public bool Exists(string name) =>
            Section?.Exists(name) ?? false;

        public void Set(string name, string? value, string? comment = null) =>
            Section?.Set(name, value, comment);
    }
}
