#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests.Network
{
    [Collection("StaticSeams")]
    public sealed class GameInfoServiceTests : IDisposable
    {
        public GameInfoServiceTests()
        {
            GameInfoService.HttpClientFactoryForTests = null;
        }

        public void Dispose()
        {
            GameInfoService.HttpClientFactoryForTests = null;
        }

        [Fact]
        public async Task GetExperienceTitleAsync_DevelopUniverseFallback_ResolvesName()
        {
            const long placeId = 99887766L;
            var handler = new FakeHttpHandler()
                // 1. Initial attempt: apis.roblox.com/universes/v1/places/{placeId}/universe
                .Enqueue(HttpStatusCode.OK, """{"universeId":554433}""")
                // 2. Initial games.roblox.com call fails or returns empty/placeholder
                .Enqueue(HttpStatusCode.NotFound, "{}")
                // 3. Fallback step 1: apis.roblox.com/universes/v1/places/{placeId}/universe
                .Enqueue(HttpStatusCode.OK, """{"universeId":554433}""")
                // 4. Fallback step 2: develop.roblox.com/v1/universes/554433
                .Enqueue(HttpStatusCode.OK, """{"name":"Fallback Modern Place"}""");

            GameInfoService.HttpClientFactoryForTests = () => new HttpClient(handler);

            string? title = await GameInfoService.ResolvePlaceNameAsync(placeId);

            Assert.Equal("Fallback Modern Place", title);
        }
    }
}
