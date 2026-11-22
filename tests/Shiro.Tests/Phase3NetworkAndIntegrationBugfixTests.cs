#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Http;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests;

[Collection("SerialGlobalState")]
public sealed class Phase3NetworkAndIntegrationBugfixTests : IDisposable
{
    private const string AdminPassword = "StrongAdminPass123";
    private readonly string? _originalWsPassword;
    private readonly string? _originalAllowAccountEditing;

    public Phase3NetworkAndIntegrationBugfixTests()
    {
        _originalWsPassword = AppSettings.WebSocketPassword;
        _originalAllowAccountEditing = AppSettings.WebServer.Get("AllowAccountEditing");
        RobloxAccountSessionClient.TestHttpClientFactory = null;
    }

    public void Dispose()
    {
        RobloxAccountSessionClient.TestHttpClientFactory = null;
        AppSettings.WebSocketPassword = _originalWsPassword ?? string.Empty;
        if (_originalAllowAccountEditing == null)
            AppSettings.WebServer.RemoveProperty("AllowAccountEditing");
        else
            AppSettings.WebServer.Set("AllowAccountEditing", _originalAllowAccountEditing);
        AccountManager.ResetAccountsForTests(Array.Empty<Account>());
    }

    [Fact]
    public void WebhookNotificationService_IsAllowedDiscordWebhookUrl_AcceptsVersionedDiscordPaths()
    {
        Assert.True(WebhookNotificationService.IsAllowedDiscordWebhookUrl("https://discord.com/api/v10/webhooks/123456/tok-en"));
        Assert.True(WebhookNotificationService.IsAllowedDiscordWebhookUrl("https://canary.discordapp.com/api/v9/webhooks/123/abc"));
        Assert.True(WebhookNotificationService.IsAllowedDiscordWebhookUrl("https://discord.com/api/webhooks/123/abc"));

        Assert.False(WebhookNotificationService.IsAllowedDiscordWebhookUrl("https://discord.com/api/v10/users/1"));
        Assert.False(WebhookNotificationService.IsAllowedDiscordWebhookUrl("http://discord.com/api/v10/webhooks/123/abc"));
        Assert.False(WebhookNotificationService.IsAllowedDiscordWebhookUrl("https://evil.com/api/v10/webhooks/123/abc"));
        Assert.False(WebhookNotificationService.IsAllowedDiscordWebhookUrl("https://discord.com/api/v10/webhooks/"));
    }

    [Fact]
    public void WebhookNotificationService_FormatTelegramMessage_TruncatesTo4096WithoutTrailingOddBackslash()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 625; i++)
            sb.Append("a_b*c[d]"); // 8 chars * 625 = 5000 chars with many MarkdownV2 specials

        var alert = new WebhookAlert
        {
            AccountUsername = "User",
            EventType = WebhookEventType.RateLimited,
            Description = sb.ToString(),
            Timestamp = new DateTimeOffset(2026, 11, 21, 15, 30, 0, TimeSpan.Zero)
        };

        string formatted = WebhookNotificationService.FormatTelegramMessage(alert);

        Assert.InRange(formatted.Length, 1, 4096);
        Assert.Contains("*Event:*", formatted, StringComparison.Ordinal);
        Assert.EndsWith(@"\.\.\.", formatted, StringComparison.Ordinal);

        string beforeEllipsis = formatted[..^6];
        int trailingBackslashes = 0;
        for (int i = beforeEllipsis.Length - 1; i >= 0 && beforeEllipsis[i] == '\\'; i--)
            trailingBackslashes++;
        Assert.Equal(0, trailingBackslashes % 2);
    }

    [Fact]
    public async Task AssetCommerceService_PurchaseAsync_CheapestReseller_SyncsExpectedPriceAndSellerType()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """
                [
                  {
                    "collectibleItemId": "col-9",
                    "collectibleProductId": "orig-cp-1",
                    "hasResellers": true,
                    "lowestAvailableResaleProductId": "resale-cp-9",
                    "lowestAvailableResaleSellerId": 999,
                    "lowestAvailableResaleSellerType": "Group",
                    "lowestPrice": 450
                  }
                ]
                """)
            .Enqueue(HttpStatusCode.OK, """{"purchased":true,"purchaseResult":"Success"}""");

        using var apiScope = AppServices.Override<IRobloxApiClient>(new RobloxApiClient(handler));
        RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);

        var account = new Account
        {
            Username = "ResaleBuyer",
            UserID = 7777,
            SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H.COOKIE.RESALE",
            CSRFToken = "csrf-ok",
            TokenSet = DateTime.Now
        };

        var (ok, error) = await AssetCommerceService.Default.PurchaseAsync(
            account,
            expectedPrice: 100,
            sellerId: 111,
            collectibleItemId: "col-9",
            collectibleProductId: null,
            sellerType: "User");

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(2, handler.Requests.Count);

        string? purchaseBody = handler.RequestBodies[1];
        Assert.NotNull(purchaseBody);
        using var doc = JsonDocument.Parse(purchaseBody!);
        Assert.Equal(450L, doc.RootElement.GetProperty("expectedPrice").GetInt64());
        Assert.Equal(999L, doc.RootElement.GetProperty("expectedSellerId").GetInt64());
        Assert.Equal("Group", doc.RootElement.GetProperty("expectedSellerType").GetString());
        Assert.Equal("resale-cp-9", doc.RootElement.GetProperty("collectibleProductId").GetString());
    }

    [Fact]
    public async Task AccountApiDispatcher_SetAvatar_DoesNotOpenMissingAssetsDialog()
    {
        var fakeDialogs = Substitute.For<IDialogService>();
        using var dialogScope = AppServices.Override<IDialogService>(fakeDialogs);

        var handler = new FakeHttpHandler(req =>
        {
            string url = req.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("auth.roblox.com", StringComparison.OrdinalIgnoreCase))
            {
                var csrfResp = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{}")
                };
                csrfResp.Headers.TryAddWithoutValidation("x-csrf-token", "csrf-avatar-1");
                return csrfResp;
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"invalidAssetIds":[98765],"errors":[{"message":"Invalid asset"}]}""")
            };
        });

        using var apiScope = AppServices.Override<IRobloxApiClient>(new RobloxApiClient(handler));
        RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);

        var account = new Account
        {
            Username = "HeadlessAvatarUser",
            UserID = 881122,
            SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H.AVATAR123"
        };

        AccountManager.ResetAccountsForTests(Array.Empty<Account>());
        AppServices.AccountRepository.AddThreadSafe(account);
        AppSettings.WebSocketPassword = AdminPassword;
        AppSettings.WebServer.Set("AllowAccountEditing", "true");

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Account"] = "HeadlessAvatarUser",
            ["Password"] = AdminPassword
        };

        var ctx = new AccountManager.ApiRequestContext
        {
            AbsolutePath = "/SetAvatar",
            IsV2 = true,
            IsLocal = true,
            Host = "localhost",
            Origin = "",
            Body = """{"playerAvatarType":"R15","assets":[{"id":98765}]}""",
            GetQuery = k => query.TryGetValue(k, out var val) ? val : null,
            GetHeader = _ => null,
            SetStatusCode = _ => { }
        };

        string response = await AccountApiDispatcher.Instance.ProcessApiRequestAsync(ctx);
        Assert.Contains("Failed to set avatar", response, StringComparison.Ordinal);
        fakeDialogs.DidNotReceiveWithAnyArgs().OpenMissingAssetsDialog(default!, default!);

        bool directResult = await RobloxSettingsApiService.Instance.SetAvatarAsync(
            account,
            """{"playerAvatarType":"R15","assets":[{"id":98765}]}""",
            suppressMissingAssetsDialog: false);
        Assert.False(directResult);
        fakeDialogs.Received(1).OpenMissingAssetsDialog(account, Arg.Is<long[]>(ids => ids.SequenceEqual(new[] { 98765L })));
    }

    [Fact]
    public void RamAccountLua_BracketsBareIpv6HostInHttpAndWebSocketUrls()
    {
        string luaPath = Path.Combine(ArchitectureTests.RepoRoot, "RAMAccount.lua");
        string lua = File.ReadAllText(luaPath);

        Assert.Contains("FormatHostForUrl", lua, StringComparison.Ordinal);
        Assert.Contains("'[' ..", lua, StringComparison.Ordinal);
        Assert.Contains(".. ']'", lua, StringComparison.Ordinal);
        Assert.Contains("string.find(", lua, StringComparison.Ordinal);
        Assert.Contains("'::1'", lua, StringComparison.Ordinal);
        Assert.Contains("'[::1]'", lua, StringComparison.Ordinal);
        Assert.Contains("^127%.", lua, StringComparison.Ordinal);
        Assert.Contains("^%s*(.-)%s*$", lua, StringComparison.Ordinal);
        Assert.Contains("'http://' .. FormatHostForUrl(WebserverSettings.Host)", lua, StringComparison.Ordinal);
        Assert.Contains("'ws://' .. FormatHostForUrl(WebserverSettings.Host)", lua, StringComparison.Ordinal);
        Assert.DoesNotContain("'http://' .. WebserverSettings.Host", lua, StringComparison.Ordinal);
        Assert.DoesNotContain("'ws://' .. WebserverSettings.Host", lua, StringComparison.Ordinal);
    }

    [Fact]
    public void WebhookNotificationService_TruncateTelegramMarkdownSafe_HandlesOddAndEvenTrailingBackslashes()
    {
        // 4089 'a' + "\_" + 'z'*20 -> at limit=4090 (4096 - 6), 1 trailing '\' (odd) -> trims to 4089 + "\.\.\." (length 4095)
        string oddAtBoundary = new string('a', 4089) + @"\_" + new string('z', 20);
        string truncatedOdd = WebhookNotificationService.TruncateTelegramMarkdownSafe(oddAtBoundary, 4096);
        Assert.Equal(4095, truncatedOdd.Length);
        Assert.Equal(new string('a', 4089) + @"\.\.\.", truncatedOdd);

        // 4088 'a' + "\\" + 'z'*20 -> at limit=4090 (4096 - 6), 2 trailing '\' (even, escaped backslash) -> keeps both + "\.\.\." (length 4096)
        string evenAtBoundary = new string('a', 4088) + @"\\" + new string('z', 20);
        string truncatedEven = WebhookNotificationService.TruncateTelegramMarkdownSafe(evenAtBoundary, 4096);
        Assert.Equal(4096, truncatedEven.Length);
        Assert.Equal(new string('a', 4088) + @"\\" + @"\.\.\.", truncatedEven);

        // Edge cases for small maxLength <= suffix.Length (6) and UTF-16 surrogate pair
        Assert.Equal(string.Empty, WebhookNotificationService.TruncateTelegramMarkdownSafe("abcdefgh", 0));
        Assert.Equal(string.Empty, WebhookNotificationService.TruncateTelegramMarkdownSafe("abcdefgh", 1));
        Assert.Equal(@"\.\.", WebhookNotificationService.TruncateTelegramMarkdownSafe("abcdefgh", 5));
        Assert.Equal(@"\.\.\.", WebhookNotificationService.TruncateTelegramMarkdownSafe("abcdefgh", 6));

        string surrogateAtBoundary = new string('a', 3) + "\U0001F600" + "zzzzzzz"; // length 12 > 10; emoji spans indices 3..4; maxLength=10 -> limit=4 splits pair
        string truncatedSurrogate = WebhookNotificationService.TruncateTelegramMarkdownSafe(surrogateAtBoundary, 10);
        Assert.Equal(@"aaa\.\.\.", truncatedSurrogate);
        Assert.InRange(truncatedSurrogate.Length, 1, 10);
    }

    [Fact]
    public async Task AssetCommerceService_PurchaseAsync_NonResale_ResolvesCreatorSellerTypeWhenSellerIdUnset()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(HttpStatusCode.OK, """
                [
                  {
                    "collectibleItemId": "col-group-1",
                    "collectibleProductId": "cp-group-1",
                    "hasResellers": false,
                    "creatorId": 555444,
                    "creatorType": "Group"
                  }
                ]
                """)
            .Enqueue(HttpStatusCode.OK, """{"purchased":true,"purchaseResult":"Success"}""");

        using var apiScope = AppServices.Override<IRobloxApiClient>(new RobloxApiClient(handler));
        RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);

        var account = new Account
        {
            Username = "GroupBuyer",
            UserID = 3333,
            SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H.COOKIE.GRP",
            CSRFToken = "csrf-ok",
            TokenSet = DateTime.Now
        };

        var (ok, error) = await AssetCommerceService.Default.PurchaseAsync(
            account,
            expectedPrice: 75,
            sellerId: 0,
            collectibleItemId: "col-group-1",
            collectibleProductId: null,
            sellerType: "User");

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(2, handler.Requests.Count);
        using var doc = JsonDocument.Parse(handler.RequestBodies[1]!);
        Assert.Equal(555444L, doc.RootElement.GetProperty("expectedSellerId").GetInt64());
        Assert.Equal("Group", doc.RootElement.GetProperty("expectedSellerType").GetString());
        Assert.Equal(75L, doc.RootElement.GetProperty("expectedPrice").GetInt64());
    }
}
