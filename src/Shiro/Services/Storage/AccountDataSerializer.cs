#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Shiro.Classes;

namespace Shiro.Services
{
    /// <summary>
    /// Reads and writes both legacy unversioned account arrays and modern versioned
    /// account envelopes using pure .NET System.Text.Json high-performance engine.
    /// The encryption and DPAPI layer remains outside this class;
    /// this class only controls the JSON schema inside that layer.
    /// </summary>
    internal static class AccountDataSerializer
    {
        public const int CurrentSchemaVersion = 1;

        public static List<Account> Deserialize(byte[] data, out bool requiresMigration)
        {
            requiresMigration = false;
            if (data is null || data.Length == 0)
                return [];

            using JsonDocument doc = JsonDocument.Parse(data);
            JsonElement root = doc.RootElement;
            if (root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return [];

            if (root.ValueKind == JsonValueKind.Array)
            {
                // Version zero is the format used by every release before
                // the schema envelope was introduced.
                requiresMigration = true;
                using (Account.BeginSuppressDeferredSaveScope())
                {
                    var legacyList = root.Deserialize(ShiroAppJson.Default.ListAccount) ?? [];
                    for (int i = 0; i < legacyList.Count; i++)
                    {
                        if (legacyList[i] != null)
                            legacyList[i].IsDirty = false;
                    }
                    return legacyList;
                }
            }

            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Account data must be a JSON array or versioned object.");

            if (!root.TryGetProperty("accounts", out JsonElement accountsElement) || accountsElement.ValueKind == JsonValueKind.Null)
                throw new InvalidDataException("Versioned account data is missing its accounts field.");

            int schemaVersion = 0;
            if (root.TryGetProperty("schemaVersion", out JsonElement schemaElement) && schemaElement.ValueKind == JsonValueKind.Number)
            {
                schemaElement.TryGetInt32(out schemaVersion);
            }

            if (schemaVersion > CurrentSchemaVersion)
                throw new InvalidDataException($"Account data schema {schemaVersion} is newer than this application supports.");

            requiresMigration = schemaVersion < CurrentSchemaVersion;
            using (Account.BeginSuppressDeferredSaveScope())
            {
                var list = accountsElement.Deserialize(ShiroAppJson.Default.ListAccount) ?? [];
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] != null)
                        list[i].IsDirty = false;
                }
                return list;
            }
        }

        internal readonly struct AccountEnvelope
        {
            public int schemaVersion { get; init; }
            public IReadOnlyCollection<Account> accounts { get; init; }
        }

        internal static readonly JsonTypeInfo<AccountEnvelope> PasswordEnvelopeTypeInfo = CreateEnvelopeTypeInfo(includePasswords: true);
        internal static readonly JsonTypeInfo<AccountEnvelope> NoPasswordEnvelopeTypeInfo = CreateEnvelopeTypeInfo(includePasswords: false);

        private static JsonTypeInfo<AccountEnvelope> CreateEnvelopeTypeInfo(bool includePasswords)
        {
            var options = new JsonSerializerOptions(ShiroAppJson.Default.Options)
            {
                TypeInfoResolver = ShiroAppJson.Default.WithAddedModifier(typeInfo =>
                {
                    if (typeInfo.Type != typeof(Account))
                        return;

                    for (int i = 0; i < typeInfo.Properties.Count; i++)
                    {
                        JsonPropertyInfo prop = typeInfo.Properties[i];
                        if (!includePasswords && string.Equals(prop.Name, nameof(Account.Password), StringComparison.OrdinalIgnoreCase))
                        {
                            prop.ShouldSerialize = static (_, _) => false;
                        }
                        else if (string.Equals(prop.Name, nameof(Account.Fields), StringComparison.OrdinalIgnoreCase))
                        {
                            prop.Get = static obj => ((Account)obj).GetFieldsSnapshot();
                        }
                    }
                })
            };

            return (JsonTypeInfo<AccountEnvelope>)options.GetTypeInfo(typeof(AccountEnvelope));
        }

        public static byte[] Serialize(IReadOnlyCollection<Account> accounts, bool includePasswords = true)
        {
            var envelope = new AccountEnvelope
            {
                schemaVersion = CurrentSchemaVersion,
                accounts = accounts ?? Array.Empty<Account>()
            };

            return JsonSerializer.SerializeToUtf8Bytes(
                envelope,
                includePasswords ? PasswordEnvelopeTypeInfo : NoPasswordEnvelopeTypeInfo);
        }
    }
}
