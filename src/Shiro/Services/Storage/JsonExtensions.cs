#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Shiro.Services
{
    public sealed class FlexibleStringConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();
                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out long l))
                        return l.ToString();
                    if (reader.TryGetDouble(out double d))
                        return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return reader.GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture);
                case JsonTokenType.True:
                    return "true";
                case JsonTokenType.False:
                    return "false";
                case JsonTokenType.Null:
                    return null;
                default:
                    using (var doc = JsonDocument.ParseValue(ref reader))
                    {
                        return doc.RootElement.GetRawText();
                    }
            }
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value == null)
                writer.WriteNullValue();
            else
                writer.WriteStringValue(value);
        }
        public override string? ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.GetString();
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            writer.WritePropertyName(value ?? string.Empty);
        }
    }

    public static class FastJson
    {
        private static JsonSerializerOptions CreateOptions(bool indented)
        {
            IJsonTypeInfoResolver resolver = JsonSerializer.IsReflectionEnabledByDefault
                ? JsonTypeInfoResolver.Combine(ShiroAppJson.Default, new DefaultJsonTypeInfoResolver())
                : ShiroAppJson.Default;
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                IncludeFields = true,
                WriteIndented = indented,
                TypeInfoResolver = resolver
            };
            options.Converters.Add(new FlexibleStringConverter());
            return options;
        }

        public static readonly JsonSerializerOptions DefaultOptions = CreateOptions(false);
        public static readonly JsonSerializerOptions IndentedOptions = CreateOptions(true);

        public static bool TryDeserialize<T>(string? json, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                if (ShiroAppJson.Default.GetTypeInfo(typeof(T)) is System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
                {
                    result = JsonSerializer.Deserialize(json, typeInfo);
                    return result != null;
                }
                result = JsonSerializer.Deserialize<T>(json, DefaultOptions);
                return result != null;
            }
            catch
            {
                result = default;
                return false;
            }
        }

        public static T? Deserialize<T>(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return default;
            try
            {
                if (ShiroAppJson.Default.GetTypeInfo(typeof(T)) is System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
                {
                    return JsonSerializer.Deserialize(json, typeInfo);
                }
                return JsonSerializer.Deserialize<T>(json, DefaultOptions);
            }
            catch
            {
                return default;
            }
        }

        public static string Serialize<T>(T value, bool indented = false)
        {
            if (value != null && !indented && ShiroAppJson.Default.GetTypeInfo(value.GetType()) is JsonTypeInfo typeInfo)
            {
                return JsonSerializer.Serialize(value, typeInfo);
            }
            return JsonSerializer.Serialize(value, indented ? IndentedOptions : DefaultOptions);
        }

        public static byte[] SerializeToUtf8Bytes<T>(T value, bool indented = false)
        {
            if (value != null && !indented && ShiroAppJson.Default.GetTypeInfo(value.GetType()) is JsonTypeInfo typeInfo)
            {
                return JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
            }
            return JsonSerializer.SerializeToUtf8Bytes(value, indented ? IndentedOptions : DefaultOptions);
        }
    }

    public static class JsonExtensions
    {
        public static bool TryParseJson<T>(this string? json, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? result)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                result = default;
                return false;
            }

            return FastJson.TryDeserialize(json, out result);
        }

        public static bool TryParseFast<T>(this string? json, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? result) => FastJson.TryDeserialize(json, out result);
    }
}
