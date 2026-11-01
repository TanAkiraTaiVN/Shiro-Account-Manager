#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shiro.Classes;
using Shiro.Nexus;

namespace Shiro.Services
{
    [JsonSourceGenerationOptions(
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        IncludeFields = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
        Converters = [typeof(FlexibleStringConverter)],
        GenerationMode = JsonSourceGenerationMode.Default)]
    [JsonSerializable(typeof(Account))]
    [JsonSerializable(typeof(List<Account>))]
    [JsonSerializable(typeof(IReadOnlyCollection<Account>))]
    [JsonSerializable(typeof(Account[]))]
    [JsonSerializable(typeof(AccountJson))]
    [JsonSerializable(typeof(ControlledAccount))]
    [JsonSerializable(typeof(List<ControlledAccount>))]
    [JsonSerializable(typeof(Command))]
    [JsonSerializable(typeof(RobloxCurrencyResponse))]
    [JsonSerializable(typeof(RobloxAuthenticatedUser))]
    [JsonSerializable(typeof(RobloxFriendsCountResponse))]
    [JsonSerializable(typeof(RobloxPinInfo))]
    [JsonSerializable(typeof(BrowserConfig))]
    [JsonSerializable(typeof(ThumbnailBatchResponse))]
    [JsonSerializable(typeof(ThumbnailBatchItem))]
    [JsonSerializable(typeof(GameDetails))]
    [JsonSerializable(typeof(GameDetails[]))]
    [JsonSerializable(typeof(List<GameDetails>))]
    [JsonSerializable(typeof(Game))]
    [JsonSerializable(typeof(List<Game>))]
    [JsonSerializable(typeof(FavoriteGame))]
    [JsonSerializable(typeof(List<FavoriteGame>))]
    [JsonSerializable(typeof(RobloxPresenceUsersRequest))]
    [JsonSerializable(typeof(RobloxPresenceEnvelope))]
    [JsonSerializable(typeof(RobloxGamesEnvelope))]
    [JsonSerializable(typeof(RobloxGameDataEntry))]
    [JsonSerializable(typeof(UserPresence))]
    [JsonSerializable(typeof(ServersInfo))]
    [JsonSerializable(typeof(ServerData))]
    [JsonSerializable(typeof(AvatarRoot))]
    [JsonSerializable(typeof(Avatar))]
    [JsonSerializable(typeof(TokenAvatarRoot))]
    [JsonSerializable(typeof(TokenAvatar))]
    [JsonSerializable(typeof(long[]))]
    [JsonSerializable(typeof(string[]))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(ProxyItem))]
    [JsonSerializable(typeof(List<ProxyItem>))]
    [JsonSerializable(typeof(RobloxServerListResult))]
    [JsonSerializable(typeof(ServerRegionPingResult))]
    [JsonSerializable(typeof(AccountDataSerializer.AccountEnvelope))]
    [JsonSerializable(typeof(AccountRuntimeStateStore.RuntimeStateEntry))]
    [JsonSerializable(typeof(AccountRuntimeStateStore.RuntimeStateDocument))]
    [JsonSerializable(typeof(AuthenticationTicketPayload))]
    [JsonSerializable(typeof(RobloxCookiesDatEnvelope))]
    [JsonSerializable(typeof(ApiErrorResponse))]
    [JsonSerializable(typeof(GatewayWelcomeMessage))]
    [JsonSerializable(typeof(GatewayPongMessage))]
    [JsonSerializable(typeof(StatusBroadcastPayload))]
    [JsonSerializable(typeof(LaunchProgressBroadcastPayload))]
    [JsonSerializable(typeof(PulseBroadcastPayload))]
    [JsonSerializable(typeof(LaunchProgress))]
    [JsonSerializable(typeof(Dictionary<string, JsonElement>))]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(DiscordWebhookPayload))]
    [JsonSerializable(typeof(DiscordWebhookEmbed))]
    [JsonSerializable(typeof(DiscordWebhookField))]
    [JsonSerializable(typeof(TelegramSendMessagePayload))]
    internal partial class ShiroAppJson : JsonSerializerContext
    {
    }

    internal readonly record struct AuthenticationTicketPayload(string authenticationTicket);
    internal readonly record struct RobloxCookiesDatEnvelope(string CookiesVersion, string CookiesData);
    internal readonly record struct ApiErrorResponse(bool success, string error);
    internal readonly record struct GatewayWelcomeData(string server, string version, int activeClients);
    internal readonly record struct GatewayWelcomeMessage(
        [property: JsonPropertyName("event")] string EventName,
        long timestamp,
        GatewayWelcomeData data);
    internal readonly record struct GatewayPongMessage(
        [property: JsonPropertyName("event")] string EventName,
        long timestamp);
    internal readonly record struct StatusBroadcastPayload(bool enabled, string status);
    internal readonly record struct PulseBroadcastPayload(long timestamp);
    internal readonly record struct LaunchProgressBroadcastPayload(
        int index,
        int total,
        string? username,
        long? userId,
        string status,
        string? result,
        string? message,
        bool completed);

    public readonly record struct DiscordWebhookPayload(
        [property: JsonPropertyName("embeds")] List<DiscordWebhookEmbed> embeds);

    public readonly record struct DiscordWebhookEmbed(
        [property: JsonPropertyName("title")] string? title,
        [property: JsonPropertyName("description")] string? description,
        [property: JsonPropertyName("color")] int color,
        [property: JsonPropertyName("fields")] List<DiscordWebhookField> fields,
        [property: JsonPropertyName("timestamp")] string? timestamp);

    public readonly record struct DiscordWebhookField(
        [property: JsonPropertyName("name")] string? name,
        [property: JsonPropertyName("value")] string? value,
        [property: JsonPropertyName("inline")] bool inline);

    public readonly record struct TelegramSendMessagePayload(
        [property: JsonPropertyName("chat_id")] string chat_id,
        [property: JsonPropertyName("text")] string text,
        [property: JsonPropertyName("parse_mode")] string parse_mode);
}
