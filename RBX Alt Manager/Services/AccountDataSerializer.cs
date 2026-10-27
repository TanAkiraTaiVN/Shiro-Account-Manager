#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RBX_Alt_Manager.Classes;

namespace RBX_Alt_Manager.Services
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

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

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
                return root.Deserialize<List<Account>>(JsonOptions) ?? [];
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
            return accountsElement.Deserialize<List<Account>>(JsonOptions) ?? [];
        }

        private readonly struct AccountEnvelope
        {
            public int schemaVersion { get; init; }
            public IReadOnlyCollection<Account> accounts { get; init; }
        }

        public static byte[] Serialize(IReadOnlyCollection<Account> accounts, bool includePasswords = true)
        {
            if (includePasswords)
            {
                return JsonSerializer.SerializeToUtf8Bytes(
                    new AccountEnvelope
                    {
                        schemaVersion = CurrentSchemaVersion,
                        accounts = accounts ?? Array.Empty<Account>()
                    },
                    JsonOptions);
            }

            JsonArray accountArray = JsonSerializer.SerializeToNode(accounts ?? [], JsonOptions) as JsonArray ?? [];
            // Do not mutate the live Account instances just to persist a
            // privacy preference. Removing the property from this JSON
            // snapshot also makes the option effective for existing
            // passwords on the very next save.
            foreach (JsonObject account in accountArray.OfType<JsonObject>())
            {
                account.Remove("Password");
            }

            JsonObject envelope = new JsonObject
            {
                ["schemaVersion"] = CurrentSchemaVersion,
                ["accounts"] = accountArray
            };

            return JsonSerializer.SerializeToUtf8Bytes(envelope);
        }
    }
}
