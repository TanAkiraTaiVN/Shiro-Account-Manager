#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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

        internal bool? CachedBool => _cachedBool;
        internal int? CachedInt => _cachedInt;
        internal decimal? CachedDecimal => _cachedDecimal;
    }

    /// <summary>
    /// Represents a strongly-typed section within the application configuration store.
    /// Thread-safe property accessor backed by pre-parsed primitive value caching.
    /// </summary>
    public class IniSection : ISettingsService
    {
        private readonly IDictionary<string, IniProperty> _properties;
        private readonly System.Threading.Lock _sync = new();

        public string Name { get; set; }
        internal Action? OnModified { get; set; }

        public IniProperty[] Properties
        {
            get
            {
                lock (_sync)
                {
                    return _properties.Values
                        .Select(p => new IniProperty { Name = p.Name, Value = p.Value })
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

        internal void WritePropertiesTo(Utf8JsonWriter writer)
        {
            lock (_sync)
            {
                foreach (var prop in _properties.Values)
                {
                    writer.WriteString(prop.Name, prop.Value);
                }
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
                    catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
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

            bool changed = false;
            lock (_sync)
            {
                if (value is null)
                {
                    changed = _properties.Remove(name);
                }
                else if (!_properties.TryGetValue(name, out IniProperty? property))
                {
                    _properties.Add(name, new IniProperty { Name = name, Value = value });
                    changed = true;
                }
                else
                {
                    changed = !string.Equals(property.Value, value, StringComparison.Ordinal);
                    property.Value = value;
                }
            }
            if (changed)
                OnModified?.Invoke();
        }

        public void RemoveProperty(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName)) return;

            bool removed;
            lock (_sync)
                removed = _properties.Remove(propertyName);
            if (removed)
                OnModified?.Invoke();
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

        internal Action? OnAtomicWrite { get; set; }
        private Action? _onModified;
        internal Action? OnModified
        {
            get => _onModified;
            set
            {
                _onModified = value;
                lock (_saveLock)
                {
                    foreach (IniSection sec in _sections.Values)
                        sec.OnModified = NotifySectionModified;
                }
            }
        }

        private void NotifySectionModified() => OnModified?.Invoke();

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
            if (trimmed.StartsWith("{", StringComparison.Ordinal) ||
                trimmed.StartsWith("//", StringComparison.Ordinal) ||
                trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                try
                {
                    var jsonOptions = new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    };
                    using var doc = JsonDocument.Parse(trimmed, jsonOptions);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
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
                }
                catch (Exception ex)
                {
                    AppServices.Logger.Warn($"Failed to parse JSON configuration: {ex.Message}. Attempting legacy fallback.");
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
                    section = new IniSection(sectionName) { OnModified = NotifySectionModified };
                    _sections[sectionName] = section;
                }
                else if (section.OnModified == null)
                {
                    section.OnModified = NotifySectionModified;
                }
                return section;
            }
        }

        public IniSection this[string sectionName] => Section(sectionName);

        public void RemoveSection(string sectionName)
        {
            if (string.IsNullOrEmpty(sectionName)) return;

            bool removed;
            lock (_saveLock)
                removed = _sections.Remove(sectionName);
            if (removed)
                OnModified?.Invoke();
        }

        public byte[] ToUtf8JsonBytes()
        {
            var buffer = new ArrayBufferWriter<byte>(512);
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                lock (_saveLock)
                {
                    foreach (IniSection section in _sections.Values)
                    {
                        writer.WritePropertyName(section.Name);
                        writer.WriteStartObject();
                        section.WritePropertiesTo(writer);
                        writer.WriteEndObject();
                    }
                }
                writer.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }

        public string ToJsonString() => Encoding.UTF8.GetString(ToUtf8JsonBytes());

        public void Save(string path)
        {
            string resolvedPath = StoragePaths.ResolveUserFile(path);
            string? directory = Path.GetDirectoryName(resolvedPath);
            if (string.IsNullOrEmpty(directory))
            {
                AppServices.Logger.Error($"Cannot save settings, target directory does not exist: {path}");
                return;
            }

            Directory.CreateDirectory(directory);

            lock (_saveLock)
            {
                byte[] bytes = ToUtf8JsonBytes();
                StoragePaths.AtomicWriteBytes(resolvedPath, bytes, durable: true);
                OnAtomicWrite?.Invoke();
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
