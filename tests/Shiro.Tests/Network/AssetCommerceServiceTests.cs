#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shiro;
using Shiro.Http;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests.Network
{
    [Collection("StaticSeams")]
    public sealed class AssetCommerceServiceTests : IDisposable
    {
        private readonly IRobloxApiClient _originalClient;

        public AssetCommerceServiceTests()
        {
            _originalClient = AppServices.RobloxApi;
            RobloxAccountSessionClient.TestHttpClientFactory = null;
        }

        public void Dispose()
        {
            RobloxAccountSessionClient.TestHttpClientFactory = null;
            AppServices.Override<IRobloxApiClient>(_originalClient);
        }

        private static Account CreateTestAccount(string name = "BuyerUser") => new()
        {
            Username = name,
            UserID = 4242,
            SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_H.COOKIE.S",
            CSRFToken = "initial-csrf",
            TokenSet = DateTime.Now
        };

        [Fact]
        public async Task GetAssetDetailsAsync_PostsToCatalogRobloxCom_WithJsonBody()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, """{"data":[{"id":1001,"name":"Cool Hat"}]}""");

            AppServices.Override<IRobloxApiClient>(new RobloxApiClient(handler));
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
            var account = CreateTestAccount();

            var (ok, content) = await AssetCommerceService.Default.GetAssetDetailsAsync(account, 1001, CancellationToken.None);

            Assert.True(ok);
            Assert.Contains("Cool Hat", content, StringComparison.Ordinal);
            Assert.Single(handler.Requests);
            var req = handler.Requests[0];
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("https://catalog.roblox.com/v1/catalog/items/details", req.RequestUri?.ToString());
            Assert.NotNull(handler.RequestBodies[0]);
            Assert.Contains("\"id\":1001", handler.RequestBodies[0], StringComparison.Ordinal);
        }

        [Fact]
        public async Task GetAssetDetailsAsync_RetriesOnceOn403Csrf()
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.Forbidden, "{}", ("x-csrf-token", "fresh-csrf-token"))
                .Enqueue(HttpStatusCode.OK, """{"data":[{"id":1002,"name":"Awesome Cap"}]}""");

            AppServices.Override<IRobloxApiClient>(new RobloxApiClient(handler));
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
            var account = CreateTestAccount();

            var (ok, content) = await AssetCommerceService.Default.GetAssetDetailsAsync(account, 1002, CancellationToken.None);

            Assert.True(ok);
            Assert.Contains("Awesome Cap", content, StringComparison.Ordinal);
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("fresh-csrf-token", account.CSRFToken);

            var retryReq = handler.Requests[1];
            Assert.True(retryReq.Headers.TryGetValues("X-CSRF-TOKEN", out var csrfValues));
            Assert.Equal("fresh-csrf-token", csrfValues.Single());
        }

        [Fact]
        public async Task PurchaseAsync_WithoutCollectibleItemId_ReturnsUnsupported()
        {
            var account = CreateTestAccount();
            var (ok, error) = await AssetCommerceService.Default.PurchaseAsync(
                account,
                expectedPrice: 100,
                sellerId: 701,
                collectibleItemId: null,
                collectibleProductId: null);

            Assert.False(ok);
            Assert.Equal(LocalizationService.Get("MissingAssets.LegacyPurchaseUnsupported"), error);
        }

        [Fact]
        public async Task PurchaseAsync_CollectibleItem_ResolvesProductIdIfMissing_AndPostsMarketplacePurchase()
        {
            var handler = new FakeHttpHandler()
                // Step 1: POST apis.roblox.com/marketplace-items/v1/items/details
                .Enqueue(HttpStatusCode.OK, """[{"collectibleItemId":"col-item-1","collectibleProductId":"resolved-cp-1","creatorId":888,"creatorType":"User"}]""")
                // Step 2: POST apis.roblox.com/marketplace-sales/v1/item/col-item-1/purchase-item
                .Enqueue(HttpStatusCode.OK, """{"purchased":true,"purchaseResult":"Success"}""");

            AppServices.Override<IRobloxApiClient>(new RobloxApiClient(handler));
            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(handler);
            var account = CreateTestAccount();

            var (ok, error) = await AssetCommerceService.Default.PurchaseAsync(
                account,
                expectedPrice: 25,
                sellerId: 888,
                collectibleItemId: "col-item-1",
                collectibleProductId: null,
                sellerType: "User");

            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal(2, handler.Requests.Count);

            var resolveReq = handler.Requests[0];
            Assert.Equal("https://apis.roblox.com/marketplace-items/v1/items/details", resolveReq.RequestUri?.ToString());
            Assert.Contains("col-item-1", handler.RequestBodies[0], StringComparison.Ordinal);

            var buyReq = handler.Requests[1];
            Assert.Equal("https://apis.roblox.com/marketplace-sales/v1/item/col-item-1/purchase-item", buyReq.RequestUri?.ToString());
            Assert.Contains("resolved-cp-1", handler.RequestBodies[1], StringComparison.Ordinal);
            Assert.Contains("4242", handler.RequestBodies[1], StringComparison.Ordinal);
        }
    }
}
