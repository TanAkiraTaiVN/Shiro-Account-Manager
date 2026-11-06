#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Classes;
using Shiro.Http;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class BatchBehaviorTests : IDisposable
    {
        private readonly Account? _previousValidAcc;

        public BatchBehaviorTests()
        {
            _previousValidAcc = AppSettings.LastValidAccount;
        }

        public void Dispose()
        {
            AppSettings.LastValidAccount = _previousValidAcc;
            AppSettings.General.Set("DisableImages", "false");
        }

        [Fact]
        public async Task Batch_DisableImagesTrue_ReturnsEmptyPromptly()
        {
            AppSettings.General.Set("DisableImages", "true");
            string img = await Batch.GetImage(12345, "Avatar");
            Assert.Equal(string.Empty, img);

            string icon = await Batch.GetGameIcon(67890);
            Assert.Equal(string.Empty, icon);
        }

        [Fact]
        public async Task Batch_ProcessBatchAndPlace_WithMockRobloxApi_ResolvesImages()
        {
            AppSettings.General.Set("DisableImages", "false");

            var fakeHandler = new StubHttpHandler(req =>
            {
                string url = req.RequestUri?.ToString() ?? string.Empty;
                if (url.Contains("batch"))
                {
                    string batchJson = "{\"data\":[{\"targetId\":9988,\"errorCode\":0,\"imageUrl\":\"https://fake.rbx/9988.png\"},{\"targetId\":7766,\"errorCode\":0,\"imageUrl\":\"https://fake.rbx/7766.png\"}]}";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(batchJson, Encoding.UTF8, "application/json")
                    };
                }

                if (url.Contains("multiget-place-details"))
                {
                    string gamesJson = "[{\"placeId\":1122,\"universeId\":7766,\"name\":\"Mock Place\"}]";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(gamesJson, Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var mockApi = Substitute.For<IRobloxApiClient>();
            var thumbClient = new RestClient(new HttpClient(fakeHandler), "https://thumbnails.roblox.com/");
            var gamesClient = new RestClient(new HttpClient(fakeHandler), "https://games.roblox.com/");

            mockApi.Thumbnails.Returns(thumbClient);
            mockApi.Games.Returns(gamesClient);
            using var _ = AppServices.Override<IRobloxApiClient>(mockApi);

            AppSettings.LastValidAccount = new Account { SecurityToken = "dummy_token" };

            // 1. GetImage
            string imgUrl = await Batch.GetImage(9988, "Avatar");
            Assert.Equal("https://fake.rbx/9988.png", imgUrl);

            // 2. GetGameIcon
            string gameIcon = await Batch.GetGameIcon(1122);
            Assert.Equal("https://fake.rbx/7766.png", gameIcon);
        }

        private sealed class StubHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
            public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(_handler(request));
        }
    }
}
