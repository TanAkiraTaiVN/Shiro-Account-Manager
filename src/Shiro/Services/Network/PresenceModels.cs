#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Shiro.Services
{
    public sealed record RobloxPresenceUsersRequest(long[] userIds);

    public sealed class RobloxPresenceEnvelope
    {
        [JsonPropertyName("userPresences")]
        public List<UserPresence> userPresences { get; set; } = new List<UserPresence>();
    }

    public sealed class RobloxGamesEnvelope
    {
        [JsonPropertyName("data")]
        public List<RobloxGameDataEntry> data { get; set; } = new List<RobloxGameDataEntry>();
    }

    public sealed class RobloxGameDataEntry
    {
        [JsonPropertyName("id")]
        public long id { get; set; }

        [JsonPropertyName("name")]
        public string name { get; set; } = string.Empty;
    }

    public class UserPresence
    {
        [JsonPropertyName("userPresenceType")]
        public UserPresenceType userPresenceType { get; set; }

        [JsonPropertyName("lastLocation")]
        public string? lastLocation { get; set; }

        [JsonPropertyName("placeId")]
        public long? placeId { get; set; }

        [JsonPropertyName("rootPlaceId")]
        public long? rootPlaceId { get; set; }

        [JsonPropertyName("gameId")]
        public string? gameId { get; set; }

        [JsonPropertyName("universeId")]
        public long? universeId { get; set; }

        [JsonPropertyName("userId")]
        public long userId { get; set; }

        [JsonPropertyName("lastOnline")]
        public DateTime lastOnline { get; set; }

        [JsonIgnore]
        public string? gameName { get; set; }
    }

    public enum UserPresenceType
    {
        Offline,
        Online,
        InGame,
        InStudio
    }
}
