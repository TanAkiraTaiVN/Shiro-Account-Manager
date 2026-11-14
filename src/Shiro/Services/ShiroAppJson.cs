#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    [JsonSerializable(typeof(RobloxCurrencyResponse))]
    [JsonSerializable(typeof(RobloxAuthenticatedUser))]
    [JsonSerializable(typeof(RobloxFriendsCountResponse))]
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
    [JsonSerializable(typeof(BroadcastEnvelope<StatusBroadcastPayload>))]
    [JsonSerializable(typeof(BroadcastEnvelope<LaunchProgressBroadcastPayload>))]
    [JsonSerializable(typeof(BroadcastEnvelope<PulseBroadcastPayload>))]
    [JsonSerializable(typeof(BroadcastEnvelope<string?>))]
    [JsonSerializable(typeof(LaunchProgress))]
    [JsonSerializable(typeof(Dictionary<string, JsonElement>))]
    [JsonSerializable(typeof(JsonElement))]
    [JsonSerializable(typeof(DiscordWebhookPayload))]
    [JsonSerializable(typeof(DiscordWebhookEmbed))]
    [JsonSerializable(typeof(DiscordWebhookField))]
    [JsonSerializable(typeof(TelegramSendMessagePayload))]
    [JsonSerializable(typeof(ApiAccountJsonItem))]
    [JsonSerializable(typeof(List<ApiAccountJsonItem>))]
    [JsonSerializable(typeof(DatacenterLocationItem))]
    [JsonSerializable(typeof(DatacenterEntryItem))]
    [JsonSerializable(typeof(List<DatacenterEntryItem>))]
    [JsonSerializable(typeof(DiscordHandshakePayload))]
    [JsonSerializable(typeof(DiscordSetActivityCommand))]
    [JsonSerializable(typeof(AccountExportJsonItem))]
    [JsonSerializable(typeof(List<AccountExportJsonItem>))]
    [JsonSerializable(typeof(WebServerV2Response))]
    [JsonSerializable(typeof(WebSocketStatsPayload))]
    [JsonSerializable(typeof(AntiAfkStatusPayload))]
    [JsonSerializable(typeof(AntiAfkTogglePayload))]
    [JsonSerializable(typeof(MemoryTrimPayload))]
    [JsonSerializable(typeof(MultiRobloxStatusPayload))]
    [JsonSerializable(typeof(MultiRobloxTogglePayload))]
    [JsonSerializable(typeof(HideWindowsPayload))]
    [JsonSerializable(typeof(ShowWindowsPayload))]
    [JsonSerializable(typeof(ToggleWindowsPayload))]
    [JsonSerializable(typeof(PotatoModeTogglePayload))]
    [JsonSerializable(typeof(AutoReconnectStatusPayload))]
    [JsonSerializable(typeof(AutoReconnectTogglePayload))]
    [JsonSerializable(typeof(Batch.BatchRequestPayload))]
    [JsonSerializable(typeof(Batch.BatchRequestPayload[]))]
    [JsonSerializable(typeof(EmptyJsonObjectPayload))]
    [JsonSerializable(typeof(UsernamesLookupPayload))]
    [JsonSerializable(typeof(BloxGenGenerateRequest))]
    [JsonSerializable(typeof(PrivateServerInviteSettingPayload))]
    [JsonSerializable(typeof(PurchaseProductPayload))]
    [JsonSerializable(typeof(JoinGameInstancePayload))]
    [JsonSerializable(typeof(FollowPrivacySettingPayload))]
    [JsonSerializable(typeof(SetDisplayNamePayload))]
    [JsonSerializable(typeof(SetPlayerAvatarTypePayload))]
    [JsonSerializable(typeof(QuickLoginCodePayload))]
    internal partial class ShiroAppJson : JsonSerializerContext
    {
    }

    internal readonly record struct EmptyJsonObjectPayload();
    internal readonly record struct UsernamesLookupPayload(string[] usernames);
    internal readonly record struct BloxGenGenerateRequest(
        [property: JsonPropertyName("apiKey")] string apiKey,
        [property: JsonPropertyName("type")] string type,
        [property: JsonPropertyName("continent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? continent = null,
        [property: JsonPropertyName("region"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? region = null);
    internal readonly record struct PrivateServerInviteSettingPayload(string whoCanInviteMeToPrivateServers);
    internal readonly record struct PurchaseProductPayload(int expectedCurrency, long expectedPrice, long expectedSellerId);
    internal readonly record struct JoinGameInstancePayload(string gameId, long placeId);
    internal readonly record struct FollowPrivacySettingPayload(string whoCanJoinMeInExperiences);
    internal readonly record struct SetDisplayNamePayload(string newDisplayName);
    internal readonly record struct SetPlayerAvatarTypePayload(string playerAvatarType);
    internal readonly record struct QuickLoginCodePayload(string code);

    internal readonly record struct DiscordHandshakePayload(
        [property: JsonPropertyName("v")] int v,
        [property: JsonPropertyName("client_id")] string client_id);

    internal readonly record struct DiscordTimestampsPayload(
        [property: JsonPropertyName("start")] long? start);

    internal readonly record struct DiscordAssetsPayload(
        [property: JsonPropertyName("large_image")] string? large_image,
        [property: JsonPropertyName("large_text")] string? large_text,
        [property: JsonPropertyName("small_image")] string? small_image,
        [property: JsonPropertyName("small_text")] string? small_text);

    internal readonly record struct DiscordButtonPayload(
        [property: JsonPropertyName("label")] string label,
        [property: JsonPropertyName("url")] string url);

    internal readonly record struct DiscordActivityPayload
    {
        [JsonPropertyName("details")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? details { get; init; }

        [JsonPropertyName("state")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? state { get; init; }

        [JsonPropertyName("timestamps")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DiscordTimestampsPayload? timestamps { get; init; }

        [JsonPropertyName("assets")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DiscordAssetsPayload? assets { get; init; }

        [JsonPropertyName("buttons")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<DiscordButtonPayload>? buttons { get; init; }
    }

    internal readonly record struct DiscordSetActivityArgs(
        [property: JsonPropertyName("pid")] int pid,
        [property: JsonPropertyName("activity")] DiscordActivityPayload? activity);

    internal readonly record struct DiscordSetActivityCommand(
        [property: JsonPropertyName("cmd")] string cmd,
        [property: JsonPropertyName("args")] DiscordSetActivityArgs args,
        [property: JsonPropertyName("nonce")] string nonce);

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
    internal readonly record struct BroadcastEnvelope<T>(
        [property: JsonPropertyName("event")] string EventName,
        long timestamp,
        T data);
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

    public sealed record ApiAccountJsonItem(
        string Username,
        long UserID,
        string Alias,
        string Description,
        string Group,
        string? CSRFToken,
        double LastUsed,
        string? Cookie,
        Dictionary<string, string> Fields);

    internal readonly record struct AccountExportJsonItem(
        string? Username,
        long UserID,
        string? Alias,
        string? Group,
        string? Password,
        long RobuxBalance,
        string? Proxy,
        DateTime LastUse,
        string? SecurityToken);

    internal readonly record struct WebServerV2Response(bool Success, string Message);
    internal readonly record struct WebSocketStatsPayload(int ActiveClients, string Server, string[] Endpoints);
    internal readonly record struct AntiAfkStatusPayload(bool Enabled, int TapsSent, string Status);
    internal readonly record struct AntiAfkTogglePayload(bool Enabled, string Status);
    internal readonly record struct MemoryTrimPayload(long BytesSaved, int ClientsCount);
    internal readonly record struct MultiRobloxStatusPayload(bool Enabled, string Status, int TotalClosed, bool MutexHeld);
    internal readonly record struct MultiRobloxTogglePayload(bool Enabled, string Status);
    internal readonly record struct HideWindowsPayload(int HiddenCount, int TotalClients, bool AreHidden);
    internal readonly record struct ShowWindowsPayload(int ShownCount, int TotalClients, bool AreHidden);
    internal readonly record struct ToggleWindowsPayload(bool AreHidden, int AffectedCount);
    internal readonly record struct PotatoModeTogglePayload(bool PotatoMode);
    internal readonly record struct AutoReconnectStatusPayload(bool Enabled, int ActiveSessions, int TotalReconnections);
    internal readonly record struct AutoReconnectTogglePayload(bool Enabled, int ActiveSessions);
}
