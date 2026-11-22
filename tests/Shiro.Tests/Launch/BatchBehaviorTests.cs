#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
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
            Batch.ResetPendingWorkersForTest();
            _previousValidAcc = AppSettings.LastValidAccount;
        }

        public void Dispose()
        {
            Batch.ResetPendingWorkersForTest();
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

            using var fakeHandler = new StubHttpHandler(req =>
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
            using var thumbHttp = new HttpClient(fakeHandler, disposeHandler: false);
            using var gamesHttp = new HttpClient(fakeHandler, disposeHandler: false);
            using var thumbClient = new RestClient(thumbHttp, "https://thumbnails.roblox.com/");
            using var gamesClient = new RestClient(gamesHttp, "https://games.roblox.com/");

            mockApi.Thumbnails.Returns(thumbClient);
            mockApi.Games.Returns(gamesClient);
            using var apiScope = AppServices.Override<IRobloxApiClient>(mockApi);

            try
            {
                AppSettings.LastValidAccount = new Account { SecurityToken = "dummy_token" };

                // 1. GetImage
                string imgUrl = await Batch.GetImage(9988, "Avatar");
                Assert.Equal("https://fake.rbx/9988.png", imgUrl);

                // 2. GetGameIcon
                string gameIcon = await Batch.GetGameIcon(1122);
                Assert.Equal("https://fake.rbx/7766.png", gameIcon);
            }
            finally
            {
                Batch.ResetPendingWorkersForTest();
                Batch.PlaceDetails.TryRemove(1122, out _);
                Batch.GameIcons.TryRemove(1122, out _);
            }
        }

        [Fact]
        public async Task Batch_ResetPendingWorkersForTest_FencesSupersededProcessPlaceWorker()
        {
            using var suppressSave = Account.BeginSuppressDeferredSaveScope();
            var prevFactory = RobloxAccountSessionClient.TestHttpClientFactory;
            var prevValidAcc = AppServices.SelectionState.LastValidAccount;
            bool hadDisableImages = AppSettings.General.Exists("DisableImages");
            string? prevDisableImages = AppSettings.General.Get("DisableImages");

            long firstPlaceId = 920_000_000L + Random.Shared.Next(1, 499_999);
            long secondPlaceId = 920_500_000L + Random.Shared.Next(1, 499_999);
            long secondUniverseId = 820_500_000L + Random.Shared.Next(1, 499_999);

            var enteredFirstRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondPlaceUrls = new List<string>();

            using var firstPlaceHandler = new AsyncStubHttpHandler(async (_, ct) =>
            {
                enteredFirstRequest.TrySetResult(true);
                await releaseFirstRequest.Task.WaitAsync(ct).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                };
            });

            using var secondPlaceHandler = new StubHttpHandler(req =>
            {
                string url = req.RequestUri?.AbsoluteUri ?? string.Empty;
                secondPlaceUrls.Add(url);
                if (url.Contains(secondPlaceId.ToString(), StringComparison.Ordinal))
                {
                    string gamesJson = $"[{{\"placeId\":{secondPlaceId},\"universeId\":{secondUniverseId},\"name\":\"Fenced Second Place\"}}]";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(gamesJson, Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]", Encoding.UTF8, "application/json")
                };
            });

            using var firstHttpClient = new HttpClient(firstPlaceHandler, disposeHandler: false);
            using var secondHttpClient = new HttpClient(secondPlaceHandler, disposeHandler: false);

            try
            {
                AppSettings.General.Set("DisableImages", "false");
                AppServices.SelectionState.LastValidAccount = new Account
                {
                    Username = "BatchFenceUser",
                    UserID = 123456789L,
                    SecurityToken = "dummy_token"
                };

                var thumbHandler = new StubHttpHandler(req =>
                {
                    string url = req.RequestUri?.AbsoluteUri ?? string.Empty;
                    if (url.Contains("v1/batch", StringComparison.OrdinalIgnoreCase))
                    {
                        string batchJson = $"{{\"data\":[{{\"targetId\":{secondUniverseId},\"errorCode\":0,\"imageUrl\":\"https://fake.rbx/icon-{secondUniverseId}.png\"}}]}}";
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(batchJson, Encoding.UTF8, "application/json")
                        };
                    }

                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                });

                var mockApi = Substitute.For<IRobloxApiClient>();
                using var thumbHttp = new HttpClient(thumbHandler);
                using var thumbClient = new RestClient(thumbHttp, "https://thumbnails.roblox.com/");
                mockApi.Thumbnails.Returns(thumbClient);
                using var apiScope = AppServices.Override<IRobloxApiClient>(mockApi);

                RobloxAccountSessionClient.TestHttpClientFactory = _ => firstHttpClient;
                Task<string> firstTask = Batch.GetGameIcon(firstPlaceId);
                await enteredFirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Batch.ResetPendingWorkersForTest();
                RobloxAccountSessionClient.TestHttpClientFactory = _ => secondHttpClient;
                Task<string> secondTask = Batch.GetGameIcon(secondPlaceId);

                releaseFirstRequest.TrySetResult(true);

                string firstIcon = await firstTask;
                string secondIcon = await secondTask;

                Assert.Equal("UNK", firstIcon);
                Assert.Equal($"https://fake.rbx/icon-{secondUniverseId}.png", secondIcon);
                Assert.Single(secondPlaceUrls);
                Assert.Contains(secondPlaceId.ToString(), secondPlaceUrls[0], StringComparison.Ordinal);
                Assert.DoesNotContain(firstPlaceId.ToString(), secondPlaceUrls[0], StringComparison.Ordinal);
            }
            finally
            {
                releaseFirstRequest.TrySetResult(true);
                Batch.ResetPendingWorkersForTest();
                Batch.PlaceDetails.TryRemove(firstPlaceId, out _);
                Batch.PlaceDetails.TryRemove(secondPlaceId, out _);
                Batch.GameIcons.TryRemove(firstPlaceId, out _);
                Batch.GameIcons.TryRemove(secondPlaceId, out _);
                RobloxAccountSessionClient.TestHttpClientFactory = prevFactory;
                AppServices.SelectionState.LastValidAccount = prevValidAcc;
                if (hadDisableImages) AppSettings.General.Set("DisableImages", prevDisableImages);
                else AppSettings.General.RemoveProperty("DisableImages");
            }
        }

        private sealed class AsyncStubHttpHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
            public AsyncStubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                _handler(request, cancellationToken);
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
