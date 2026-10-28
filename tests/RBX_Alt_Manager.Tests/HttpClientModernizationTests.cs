using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;
using Xunit;

namespace Shiro.Tests
{
    public class HttpClientModernizationTests
    {
        [Fact]
        public void RestRequest_HeadersAndCookies_AreFormattedCorrectly()
        {
            var req = new RestRequest("v1/test", Method.Post);
            req.AddHeader("X-CSRF-TOKEN", "test-token");
            req.AddCookie(".ROBLOSECURITY", "test-cookie");
            req.AddCookie("guest", "true");

            Assert.Equal("v1/test", req.Resource);
            Assert.Equal(Method.Post, req.Method);
            Assert.Equal("test-token", req.Headers["X-CSRF-TOKEN"]);
            Assert.Contains(".ROBLOSECURITY=test-cookie", req.Headers["Cookie"]);
            Assert.Contains("guest=true", req.Headers["Cookie"]);
        }

        [Fact]
        public void RestRequest_Parameters_AreCaptured()
        {
            var req = new RestRequest("v1/search", Method.Get);
            req.AddQueryParameter("keyword", "bloxfruits");
            req.AddParameter("limit", "100");

            Assert.Equal(2, req.Parameters.Count);
            Assert.Equal("keyword", req.Parameters[0].Key);
            Assert.Equal("bloxfruits", req.Parameters[0].Value);
            Assert.Equal("limit", req.Parameters[1].Key);
            Assert.Equal("100", req.Parameters[1].Value);
        }

        [Fact]
        public async Task RestClient_ExecutesRequest_AndParsesResponseCookies()
        {
            var handler = new MockHttpMessageHandler((request, ct) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"status\":\"ok\"}", System.Text.Encoding.UTF8, "application/json")
                };
                response.Headers.Add("Set-Cookie", ".ROBLOSECURITY=_|WARNING:-DO-NOT-SHARE-THIS; path=/; domain=.roblox.com; HttpOnly");
                response.Headers.Add("X-RateLimit-Reset", "1700000000");
                return Task.FromResult(response);
            });

            using var httpClient = new HttpClient(handler);
            using var client = new RestClient(httpClient, "https://api.roblox.com");

            var request = new RestRequest("v1/test", Method.Get);
            var response = await client.ExecuteAsync(request);

            Assert.True(response.IsSuccessful);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("{\"status\":\"ok\"}", response.Content);

            var cookie = response.Cookies[".ROBLOSECURITY"];
            Assert.NotNull(cookie);
            Assert.Equal("_|WARNING:-DO-NOT-SHARE-THIS", cookie.Value);

            var header = response.Headers.Find(h => h.Name.Equals("X-RateLimit-Reset", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(header);
            Assert.Equal("1700000000", header.Value?.ToString());
        }

        private sealed class MockHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

            public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return _handler(request, cancellationToken);
            }
        }
    }
}
