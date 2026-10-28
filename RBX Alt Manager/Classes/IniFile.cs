#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Shiro.Services;

namespace Shiro
{
    /// <summary>
    /// Represents a configuration property key-value pair with pre-parsed primitive caches.
    /// </summary>
    public class IniProperty
    {
        private string _value = string.Empty;
        private bool? _cachedBool;
        private int? _cachedInt;
        private decimal? _cachedDecimal;

        public string Name { get; set; } = string.Empty;

        public string Value
        {
            get => _value;
            set
            {
                _value = value ?? string.Empty;
                if (bool.TryParse(_value, out bool b))
                    _cachedBool = b;
                else if (_value == "1")
                    _cachedBool = true;
                else if (_value == "0")
                    _cachedBool = false;
                else
                    _cachedBool = null;

                _cachedInt = int.TryParse(_value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : null;
                _cachedDecimal = decimal.TryParse(_value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) ? d : null;
            }
        }

        public string? Comment { get; set; }

        internal bool? CachedBool => _cachedBool;
        internal int? CachedInt => _cachedInt;
        internal decimal? CachedDecimal => _cachedDecimal;
    }

    /// <summary>
    /// Represents a strongly-typed section within the application configuration store.
    /// Thread-safe property accessor backed by pre-parsed primitive value caching.
    /// </summary>
    public class IniSection
    {
        private readonly IDictionary<string, IniProperty> _properties;
        private readonly System.Threading.Lock _sync = new();

        public string Name { get; set; }
        public string? Comment { get; set; }

        public IniProperty[] Properties
        {
            get
            {
                lock (_sync)
                {
                    return _properties.Values
                        .Select(p => new IniProperty { Name = p.Name, Value = p.Value, Comment = p.Comment })
                        .ToArray();
                }
            }
        }

        internal Dictionary<string, string> ToDictionary()
        {
            lock (_sync)
            {
                var dict = new Dictionary<string, string>(_properties.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var prop in _properties.Values)
                {
                    dict[prop.Name] = prop.Value;
                }
                return dict;
            }
        }

        public IniSection(string name)
        {
            Name = name ?? string.Empty;
            _properties = new Dictionary<string, IniProperty>(StringComparer.OrdinalIgnoreCase);
        }

        public string? Get(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            lock (_sync)
            {
                return _properties.TryGetValue(name, out IniProperty? property) ? property.Value : null;
            }
        }

        public bool Exists(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            lock (_sync)
                return _properties.ContainsKey(name);
        }

        public bool GetBool(string name, bool defaultValue = false)
        {
            if (string.IsNullOrEmpty(name)) return defaultValue;
            lock (_sync)
            {
                if (_properties.TryGetValue(name, out IniProperty? property) && property.CachedBool.HasValue)
                    return property.CachedBool.Value;
            }
            return defaultValue;
        }

        public int GetInt(string name, int defaultValue = 0)
        {
            if (string.IsNullOrEmpty(name)) return defaultValue;
            lock (_sync)
            {
                if (_properties.TryGetValue(name, out IniProperty? property) && property.CachedInt.HasValue)
                    return property.CachedInt.Value;
            }
            return defaultValue;
        }

        public decimal GetDecimal(string name, decimal defaultValue = 0m)
        {
            if (string.IsNullOrEmpty(name)) return defaultValue;
            lock (_sync)
            {
                if (_properties.TryGetValue(name, out IniProperty? property) && property.CachedDecimal.HasValue)
                    return property.CachedDecimal.Value;
            }
            return defaultValue;
        }

        public T? Get<T>(string name) => Get(name, default(T));

        public T? Get<T>(string name, T? defaultValue)
        {
            if (string.IsNullOrEmpty(name)) return defaultValue;

            if (typeof(T) == typeof(bool))
            {
                bool def = defaultValue is bool b && b;
                bool res = GetBool(name, def);
                return (T)(object)res;
            }
            if (typeof(T) == typeof(int))
            {
                int def = defaultValue is int i ? i : 0;
                int res = GetInt(name, def);
                return (T)(object)res;
            }
            if (typeof(T) == typeof(decimal))
            {
                decimal def = defaultValue is decimal d ? d : 0m;
                decimal res = GetDecimal(name, def);
                return (T)(object)res;
            }
            if (typeof(T) == typeof(string))
            {
                string? s = Get(name);
                return s != null ? (T)(object)s : defaultValue;
            }

            lock (_sync)
            {
                if (_properties.TryGetValue(name, out IniProperty? property))
                {
                    try
                    {
                        return (T?)Convert.ChangeType(property.Value, typeof(T), CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        return defaultValue;
                    }
                }
            }

            return defaultValue;
        }

        public void Set(string name, string? value, string? comment = null)
        {
            if (string.IsNullOrEmpty(name)) return;

            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    _properties.Remove(name);
                    return;
                }

                if (!_properties.TryGetValue(name, out IniProperty? property))
                {
                    _properties.Add(name, new IniProperty { Name = name, Value = value, Comment = comment });
                }
                else
                {
                    property.Value = value;
                    if (comment != null) property.Comment = comment;
                }
            }
        }

        public void RemoveProperty(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName)) return;

            lock (_sync)
                _properties.Remove(propertyName);
        }
    }

    /// <summary>
    /// Pure JSON Configuration Engine for Shiro Account Manager.
    /// Backed entirely by System.Text.Json with zero third-party dependencies.
    /// Provides 100% backward compatibility for existing domain services and ViewModels.
    /// </summary>
    public class IniFile
    {
        private readonly System.Threading.Lock _saveLock = new();
        private readonly IDictionary<string, IniSection> _sections;

        public bool WriteSpacingBetweenNameAndValue { get; set; } = true;
        public char CommentChar { get; set; } = '#';

        public IniSection[] Sections
        {
            get
            {
                lock (_saveLock)
                    return _sections.Values.ToArray();
            }
        }

        public IniFile()
        {
            _sections = new Dictionary<string, IniSection>(StringComparer.OrdinalIgnoreCase);
        }

        public IniFile(string path) : this()
        {
            Load(path);
        }

        public IniFile(TextReader? reader) : this()
        {
            if (reader != null)
                LoadFromString(reader.ReadToEnd());
        }

        private void Load(string path)
        {
            string resolved = StoragePaths.ResolveUserFile(path);
            if (!File.Exists(resolved))
                return;

            string content = File.ReadAllText(resolved);
            LoadFromString(content);
        }

        public void LoadFromString(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return;

            string trimmed = content.Trim();

            // 1. Primary path: Pure JSON parsing via System.Text.Json
            if (trimmed.StartsWith("{"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    foreach (var sectionProp in doc.RootElement.EnumerateObject())
                    {
                        var section = Section(sectionProp.Name);
                        if (sectionProp.Value.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in sectionProp.Value.EnumerateObject())
                            {
                                section.Set(prop.Name, prop.Value.ToString());
                            }
                        }
                    }
                    return;
                }
                catch (Exception ex)
                {
                    Program.Logger?.Warn($"Failed to parse JSON configuration: {ex.Message}. Attempting legacy fallback.");
                }
            }

            // 2. Legacy fallback for old INI files during migration
            using var reader = new StringReader(content);
            IniSection? currentSection = null;
            string? rawLine;
            while ((rawLine = reader.ReadLine()) != null)
            {
                ReadOnlySpan<char> line = rawLine.AsSpan().Trim();
                if (line.IsEmpty || line[0] == ';' || line[0] == '#')
                    continue;

                if (line.Length >= 2 && line[0] == '[' && line[^1] == ']')
                {
                    ReadOnlySpan<char> sectionSpan = line.Slice(1, line.Length - 2);
                    string sectionName = sectionSpan.Equals("RBX Alt Manager".AsSpan(), StringComparison.Ordinal)
                        ? "Roblox Account Manager"
                        : sectionSpan.ToString();
                    currentSection = Section(sectionName);
                }
                else if (currentSection != null)
                {
                    int equalsIndex = line.IndexOf('=');
                    if (equalsIndex > 0)
                    {
                        string key = line[..equalsIndex].Trim().ToString();
                        string val = line[(equalsIndex + 1)..].Trim().ToString();
                        currentSection.Set(key, val);
                    }
                }
            }
        }

        public IniSection Section(string? sectionName)
        {
            if (string.IsNullOrEmpty(sectionName))
                sectionName = "General";

            lock (_saveLock)
            {
                if (!_sections.TryGetValue(sectionName, out IniSection? section) || section == null)
                {
                    section = new IniSection(sectionName);
                    _sections[sectionName] = section;
                }
                return section;
            }
        }

        public IniSection this[string sectionName] => Section(sectionName);

        public void RemoveSection(string sectionName)
        {
            if (string.IsNullOrEmpty(sectionName)) return;

            lock (_saveLock)
                _sections.Remove(sectionName);
        }

        public string ToJsonString()
        {
            var root = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (IniSection section in Sections)
            {
                root[section.Name] = section.ToDictionary();
            }

            return JsonSerializer.Serialize(root, Utilities.FastJson.IndentedOptions);
        }

        public void Save(string path)
        {
            string resolvedPath = StoragePaths.ResolveUserFile(path);
            string? directory = Path.GetDirectoryName(resolvedPath);
            if (string.IsNullOrEmpty(directory))
            {
                Program.Logger?.Error($"Cannot save settings, target directory does not exist: {path}");
                return;
            }

            Directory.CreateDirectory(directory);

            lock (_saveLock)
            {
                string text = ToJsonString();
                StoragePaths.AtomicWriteText(resolvedPath, text, durable: true);
            }
        }

        public void Save(TextWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.Write(ToJsonString());
        }

        public override string ToString() => ToJsonString();
    }
}
