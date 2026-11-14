#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Shiro.Services
{
    /// <summary>
    /// Maps outfit JSON data to the unified Roblox Avatar v4 wire format (PATCH https://avatar.roblox.com/v4/avatar).
    /// Replaces legacy v1-v3 mutation endpoints deprecated before the December 11, 2026 cutoff.
    /// Wire schema verified against OpenAPI /v4/avatar and live GET /v1/avatar-rules palette.
    /// </summary>
    internal static class AvatarV4PayloadMapper
    {
        // Verified 75-entry BrickColor palette mapping numeric brickColorId to hex string #RRGGBB.
        private static readonly FrozenDictionary<int, string> BodyColorsPalette = new Dictionary<int, string>
        {
            [361] = "#564236", [192] = "#694028", [217] = "#7C5C46", [153] = "#957977", [359] = "#AF9483", [352] = "#C7AC78", [5] = "#D7C59A", [101] = "#DA867A", [1007] = "#A34B4B", [1014] = "#AA5500",
            [38] = "#A05F35", [18] = "#CC8E69", [125] = "#EAB892", [1030] = "#FFCC99", [133] = "#D5733D", [106] = "#DA8541", [105] = "#E29B40", [1017] = "#FFAF00", [24] = "#F5CD30", [334] = "#F8D96D",
            [226] = "#FDEA8D", [141] = "#27462D", [1021] = "#3A7D15", [28] = "#287F47", [37] = "#4B974B", [310] = "#5B9A4C", [317] = "#7C9C6B", [119] = "#A4BD47", [1011] = "#002060", [1012] = "#2154B9",
            [1010] = "#0000FF", [23] = "#0D69AC", [305] = "#527CAE", [102] = "#6E99CA", [45] = "#B4D2E4", [107] = "#008F9C", [1018] = "#12EED4", [1027] = "#9FF3E9", [1019] = "#00FFFF", [1013] = "#04AFEC",
            [11] = "#80BBDC", [1024] = "#AFDDFF", [104] = "#6B327C", [1023] = "#8C5B9F", [321] = "#A75E9B", [1015] = "#AA00AA", [1031] = "#6225D1", [1006] = "#B480FF", [1026] = "#B1A7FF", [21] = "#C4281C",
            [1004] = "#FF0000", [1032] = "#FF00BF", [1016] = "#FF66CC", [330] = "#FF98DC", [9] = "#E8BAC8", [1025] = "#FFC9C9", [364] = "#5A4C42", [351] = "#BC9B5D", [1008] = "#C1BE42", [29] = "#A1C48C",
            [1022] = "#7F8E64", [151] = "#789082", [135] = "#74869D", [1020] = "#00FF00", [1028] = "#CCFFCC", [1009] = "#FFFF00", [1029] = "#FFFFCC", [1003] = "#111111", [26] = "#1B2A35", [199] = "#635F62",
            [194] = "#A3A2A5", [1002] = "#CDCDCD", [208] = "#E5E4DF", [1] = "#F2F3F3", [1001] = "#F8F8F8"
        }.ToFrozenDictionary();

        public static bool TryMapPayload(string? avatarJsonData, out AvatarV4PatchRequest? request, out string? errorMessage)
        {
            request = null;
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(avatarJsonData)) return false;

            try
            {
                using var doc = JsonDocument.Parse(avatarJsonData);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;

                var updateTypes = new List<string>(4);
                string? playerAvatarType = null;
                AvatarV4Scales? scales = null;
                AvatarV4BodyColors? bodyColors = null;
                AvatarV4Asset[]? assets = null;

                if (root.TryGetProperty("playerAvatarType", out var patElem) || root.TryGetProperty("avatarType", out patElem))
                {
                    playerAvatarType = NormalizeAvatarType(patElem);
                    if (!string.IsNullOrEmpty(playerAvatarType)) updateTypes.Add("UpdateAvatarType");
                }

                if ((root.TryGetProperty("scales", out var scaleElem) || root.TryGetProperty("scale", out scaleElem)) && scaleElem.ValueKind == JsonValueKind.Object)
                {
                    scales = new AvatarV4Scales(
                        ReadDouble(scaleElem, "height"), ReadDouble(scaleElem, "width"),
                        ReadDouble(scaleElem, "head"), ReadDouble(scaleElem, "depth"),
                        ReadDouble(scaleElem, "proportion"), ReadDouble(scaleElem, "bodyType"));
                    updateTypes.Add("UpdateScales");
                }

                if ((root.TryGetProperty("bodyColors", out var bcElem) || root.TryGetProperty("bodyColor3s", out bcElem)) && bcElem.ValueKind == JsonValueKind.Object)
                {
                    bodyColors = new AvatarV4BodyColors(
                        ReadColor(bcElem, "headColor", "headColor3", "headColorId"),
                        ReadColor(bcElem, "torsoColor", "torsoColor3", "torsoColorId"),
                        ReadColor(bcElem, "rightArmColor", "rightArmColor3", "rightArmColorId"),
                        ReadColor(bcElem, "leftArmColor", "leftArmColor3", "leftArmColorId"),
                        ReadColor(bcElem, "rightLegColor", "rightLegColor3", "rightLegColorId"),
                        ReadColor(bcElem, "leftLegColor", "leftLegColor3", "leftLegColorId"));
                    updateTypes.Add("UpdateBodyColors");
                }

                if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
                {
                    assets = ParseAssets(assetsElem);
                    updateTypes.Add("UpdateAssets");
                }

                if (updateTypes.Count == 0) return false;

                var model = new AvatarV4UpdateModel(scales, playerAvatarType, bodyColors, assets);
                request = new AvatarV4PatchRequest(updateTypes.ToArray(), new AvatarV4Definition(model));
                return true;
            }
            catch (JsonException ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static long[] ExtractInvalidAssetIds(string? jsonResponse)
        {
            if (string.IsNullOrWhiteSpace(jsonResponse)) return Array.Empty<long>();
            try
            {
                using var doc = JsonDocument.Parse(jsonResponse);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return Array.Empty<long>();

                var invalidIds = new HashSet<long>();
                void CollectIds(JsonElement arrayElem)
                {
                    if (arrayElem.ValueKind != JsonValueKind.Array) return;
                    foreach (var item in arrayElem.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out long numId) && numId > 0)
                            invalidIds.Add(numId);
                        else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idProp))
                        {
                            if (idProp.ValueKind == JsonValueKind.Number && idProp.TryGetInt64(out long objId) && objId > 0)
                                invalidIds.Add(objId);
                            else if (idProp.ValueKind == JsonValueKind.String && long.TryParse(idProp.GetString(), out long parsedId) && parsedId > 0)
                                invalidIds.Add(parsedId);
                        }
                    }
                }

                if (root.TryGetProperty("validation", out var valElem) && valElem.ValueKind == JsonValueKind.Object &&
                    valElem.TryGetProperty("invalidAssets", out var valAssets)) CollectIds(valAssets);
                if (root.TryGetProperty("invalidAssets", out var topAssets)) CollectIds(topAssets);
                if (root.TryGetProperty("invalidAssetIds", out var legacyIds)) CollectIds(legacyIds);

                return invalidIds.OrderBy(id => id).ToArray();
            }
            catch (JsonException)
            {
                return Array.Empty<long>();
            }
        }

        private static string? NormalizeAvatarType(JsonElement elem)
        {
            if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out int intVal))
                return intVal switch { 1 => "R6", 3 => "R15", _ => null };

            if (elem.ValueKind == JsonValueKind.String)
            {
                string? raw = elem.GetString()?.Trim();
                if (string.IsNullOrEmpty(raw)) return null;
                if (string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) || string.Equals(raw, "R6", StringComparison.OrdinalIgnoreCase)) return "R6";
                if (string.Equals(raw, "3", StringComparison.OrdinalIgnoreCase) || string.Equals(raw, "R15", StringComparison.OrdinalIgnoreCase)) return "R15";
                return raw;
            }
            return null;
        }

        private static double? ReadDouble(JsonElement obj, string propertyName)
        {
            if (obj.TryGetProperty(propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double val)) return val;
                if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) return parsed;
            }
            return null;
        }

        private static string? ReadColor(JsonElement obj, string v4Key, string v2Key, string v1Key) =>
            obj.TryGetProperty(v4Key, out var elem) || obj.TryGetProperty(v2Key, out elem) || obj.TryGetProperty(v1Key, out elem) ? NormalizeColor(elem) : null;

        private static string? NormalizeColor(JsonElement elem)
        {
            if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out int brickId))
                return BodyColorsPalette.TryGetValue(brickId, out string? hex) ? hex : null;

            if (elem.ValueKind == JsonValueKind.String)
            {
                string? text = elem.GetString()?.Trim();
                if (string.IsNullOrEmpty(text)) return null;
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedBrickId) &&
                    BodyColorsPalette.TryGetValue(parsedBrickId, out string? paletteHex)) return paletteHex;

                string trimmed = text.StartsWith('#') ? text.Substring(1) : text;
                if (trimmed.Length == 6 && int.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
                    return $"#{trimmed.ToUpperInvariant()}";
            }
            return null;
        }

        private static AvatarV4Asset[] ParseAssets(JsonElement assetsElem)
        {
            var list = new List<AvatarV4Asset>();
            foreach (var item in assetsElem.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    long id = 0;
                    if (item.TryGetProperty("id", out var idProp))
                    {
                        if (idProp.ValueKind == JsonValueKind.Number) id = idProp.GetInt64();
                        else if (idProp.ValueKind == JsonValueKind.String && long.TryParse(idProp.GetString(), out long parsedId)) id = parsedId;
                    }

                    if (id > 0)
                    {
                        JsonElement? meta = item.TryGetProperty("meta", out var metaProp) &&
                            metaProp.ValueKind != JsonValueKind.Null && metaProp.ValueKind != JsonValueKind.Undefined ? metaProp.Clone() : null;
                        list.Add(new AvatarV4Asset(id, meta));
                    }
                }
                else if (item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out long numId) && numId > 0)
                {
                    list.Add(new AvatarV4Asset(numId, null));
                }
            }
            return list.ToArray();
        }
    }
}
