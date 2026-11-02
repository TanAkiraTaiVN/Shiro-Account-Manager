#nullable enable

namespace Shiro.Services
{
    /// <summary>
    /// Abstraction for querying and mutating application configuration settings.
    /// Decouples services from static IniSection references and enables isolated unit testing.
    /// </summary>
    public interface ISettingsService
    {
        string? Get(string name);
        T? Get<T>(string name, T? defaultValue = default);
        bool GetBool(string name, bool defaultValue = false);
        int GetInt(string name, int defaultValue = 0);
        decimal GetDecimal(string name, decimal defaultValue = 0m);
        bool Exists(string name);
        void Set(string name, string? value, string? comment = null);
        void RemoveProperty(string name);
    }
}
