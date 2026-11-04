#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Classes;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class BloxGenServiceBehaviorTests : IDisposable
    {
        private readonly FakeHttpHandler _fakeHttp;
        private readonly HttpClient _httpClient;
        private readonly IAppLogger _loggerMock;
        private readonly InMemorySettingsService _settings;
        private readonly BloxGenService _service;
        private readonly IAccountRepository _originalRepo;
        private readonly IHostUiBridgeService _originalBridge;

        public BloxGenServiceBehaviorTests()
        {
            _fakeHttp = new FakeHttpHandler();
            _httpClient = new HttpClient(_fakeHttp)
            {
                BaseAddress = new Uri("https://bloxgen.net/api/")
            };
            _loggerMock = Substitute.For<IAppLogger>();
            _settings = new InMemorySettingsService();
            _service = new BloxGenService(_httpClient, _loggerMock, _settings);

            _originalRepo = AppServices.AccountRepository;
            _originalBridge = AppServices.HostUiBridge;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
            _fakeHttp.Dispose();
            AppServices.AccountRepository = _originalRepo;
            AppServices.HostUiBridge = _originalBridge;
        }

        [Fact]
        public void SettingsProperties_GetAndSetExpectedValues()
        {
            _service.SavedApiKey = "bloxgen-secret-api-key";
            Assert.Equal("bloxgen-secret-api-key", _service.SavedApiKey);

            _service.DefaultContinent = "EU";
            Assert.Equal("EU", _service.DefaultContinent);

            _service.DefaultGenType = "verified";
            Assert.Equal("verified", _service.DefaultGenType);
        }

        [Fact]
        public async Task GenerateAccountAsync_WhenApiKeyMissing_ReturnsFailure()
        {
            _service.SavedApiKey = string.Empty;

            var result = await _service.GenerateAccountAsync(apiKey: string.Empty);

            Assert.False(result.Success);
            Assert.Null(result.Account);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        [Fact]
        public async Task GenerateAccountAsync_WhenHttpError_ReturnsApiErrorWithStatusCode()
        {
            _fakeHttp.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"message\":\"Invalid API token provided\"}")
            });

            var result = await _service.GenerateAccountAsync(apiKey: "bad-token");

            Assert.False(result.Success);
            Assert.Null(result.Account);
            Assert.Contains("401", result.ErrorMessage);
            Assert.Contains("Invalid API token provided", result.ErrorMessage);
        }

        [Fact]
        public async Task GenerateAccountAsync_WhenApiReturnsSuccessFalse_ReturnsFailureWithMessage()
        {
            _fakeHttp.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":false,\"message\":\"Out of stock for selected region\"}")
            });

            var result = await _service.GenerateAccountAsync(apiKey: "good-token");

            Assert.False(result.Success);
            Assert.Null(result.Account);
            Assert.Equal("Out of stock for selected region", result.ErrorMessage);
        }

        [Fact]
        public async Task GenerateAccountAsync_WhenMissingCookieInResponse_ReturnsFailure()
        {
            _fakeHttp.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true,\"data\":{\"username\":\"AltUserWithoutCookie\"}}")
            });

            var result = await _service.GenerateAccountAsync(apiKey: "good-token");

            Assert.False(result.Success);
            Assert.Null(result.Account);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        [Fact]
        public async Task GenerateAccountAsync_WhenSuccessful_ParsesAccountAndBalance()
        {
            const string cookie = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-...secret";
            string json = $@"{{
                ""success"": true,
                ""data"": {{
                    ""user_id"": 987654321,
                    ""username"": ""BloxAltMaster"",
                    ""password"": ""SecurePass123!"",
                    ""cookie"": ""{cookie}""
                }},
                ""remaining_balance"": 15.50
            }}";

            _fakeHttp.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });

            var result = await _service.GenerateAccountAsync(apiKey: "active-key", genType: "unverified", continent: "NA");

            Assert.True(result.Success);
            Assert.NotNull(result.Account);
            Assert.Equal("BloxAltMaster", result.Account!.Username);
            Assert.Equal(987654321, result.Account.UserId);
            Assert.Equal("SecurePass123!", result.Account.Password);
            Assert.Equal(cookie, result.Account.Cookie);
            Assert.Equal("NA", result.Account.Region);
            Assert.Equal("unverified", result.Account.GenType);
            Assert.Equal(15.50, result.RemainingBalance);
        }

        [Fact]
        public async Task GenerateAccountAsync_WhenCancelled_ReturnsCancellationError()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var result = await _service.GenerateAccountAsync(apiKey: "key", cancellationToken: cts.Token);

            Assert.False(result.Success);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        [Fact]
        public void ImportAccountToShiro_WhenAltNullOrCookieEmpty_ReturnsFalse()
        {
            Assert.False(_service.ImportAccountToShiro(null!));
            Assert.False(_service.ImportAccountToShiro(new BloxGenAccountItem { Cookie = "" }));
            Assert.False(_service.ImportAccountToShiro(new BloxGenAccountItem { Cookie = "   " }));
        }

        [Fact]
        public void ImportAccountToShiro_WhenValidAlt_SavesToRepositoryAndRefreshesUi()
        {
            var repoMock = Substitute.For<IAccountRepository>();
            var bridgeMock = Substitute.For<IHostUiBridgeService>();
            AppServices.AccountRepository = repoMock;
            AppServices.HostUiBridge = bridgeMock;

            var item = new BloxGenAccountItem
            {
                UserId = 1234567,
                Username = "ImportedAlt",
                Password = "SecretPassword",
                Cookie = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-...cookie",
                Region = "EU",
                GenType = "unverified"
            };

            bool success = _service.ImportAccountToShiro(item, groupName: "AltBatch1");

            Assert.True(success);
            repoMock.Received(1).AddThreadSafe(Arg.Is<Account>(a => a.Username == "ImportedAlt" && a.UserID == 1234567 && a.Group == "AltBatch1"));
            repoMock.Received(1).Save(true);
            bridgeMock.Received(1).RefreshAccountView();
        }

        private sealed class InMemorySettingsService : ISettingsService
        {
            private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

            public string? Get(string name) => _values.TryGetValue(name, out var val) ? val : null;

            public T? Get<T>(string name, T? defaultValue = default)
            {
                if (_values.TryGetValue(name, out var val) && val != null)
                {
                    if (typeof(T) == typeof(string)) return (T)(object)val;
                    return (T)Convert.ChangeType(val, typeof(T));
                }
                return defaultValue;
            }

            public bool GetBool(string name, bool defaultValue = false) =>
                bool.TryParse(Get(name), out var b) ? b : defaultValue;

            public int GetInt(string name, int defaultValue = 0) =>
                int.TryParse(Get(name), out var i) ? i : defaultValue;

            public decimal GetDecimal(string name, decimal defaultValue = 0m) =>
                decimal.TryParse(Get(name), out var d) ? d : defaultValue;

            public bool Exists(string name) => _values.ContainsKey(name);

            public void Set(string name, string? value, string? comment = null) => _values[name] = value;

            public void RemoveProperty(string name) => _values.Remove(name);
        }
    }
}
