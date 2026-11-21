#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.AvaloniaUI.Controls;
using Shiro.AvaloniaUI.Views;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests;

[Collection("SerialGlobalState")]
public sealed class P0AuditRemediationTests : IDisposable
{
    private readonly ISettingsService _origProxySettings;

    public P0AuditRemediationTests()
    {
        AppSettings.ActiveIniFile ??= new IniFile();
        _origProxySettings = ProxyBindingService.Settings;
        RobloxAccountSessionClient.TestHttpClientFactory = null;
        ProxyBindingService.ClearClients();
        RobloxRateLimitCoordinator.Instance.ResetBackoff();
    }

    public void Dispose()
    {
        RobloxAccountSessionClient.TestHttpClientFactory = null;
        ProxyBindingService.Settings = _origProxySettings;
        ProxyBindingService.ClearClients();
        RobloxRateLimitCoordinator.Instance.ResetBackoff();
    }

    private sealed class RecordingSettingsSideEffects : ISettingsSideEffects
    {
        private readonly AppSettings _settings;
        public List<(int Port, bool AllowExternal, bool StoredAllowExternal)> RestartCalls { get; } = new();

        public RecordingSettingsSideEffects(AppSettings settings) => _settings = settings;

        public void ApplyTheme(AccentTheme theme, bool save) { }
        public void SetLanguage(AppLanguage lang) { }
        public void PatchClientSettings() { }
        public void SetAntiAfk(bool on) { }
        public void UpdateMultiRoblox() { }
        public void ConfigureBackgroundThrottling(bool enabled, int targetFps) { }
        public void ConfigureSmartMute(bool on) { }
        public void ConfigureAutoTrim(bool on) { }
        public void SetCpuRateLimit(int percent) { }
        public void SetAutoCookieRefresh(bool on) { }
        public void NotifyDisplayNamesChanged() { }
        public void QueueDeferredSave() { }
        public void CleanNotificationArea() { }
        public int ClearAvatarCache() => 0;
        public Task ResetEncryptionAsync() => Task.CompletedTask;
        public Task<bool> ExportVaultAsync() => Task.FromResult(false);
        public Task<bool> ImportVaultAsync() => Task.FromResult(false);
        public Task StopWebServerAsync() => Task.CompletedTask;

        public Task RestartWebServerAsync(int port, bool allowExternal)
        {
            bool stored = _settings.GetSection(AppSettingsKeys.Sections.WebServer)
                ?.Get<bool>(AppSettingsKeys.WebServer.AllowExternalConnections) ?? false;
            RestartCalls.Add((port, allowExternal, stored));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void SettingsViewModel_AllowExternalConnectionsToggled_SavesBeforeRestartAndRestartsServer()
    {
        var prevIni = AppSettings.ActiveIniFile;
        string tempIni = Path.Combine(Path.GetTempPath(), $"shiro-p0-set-{Guid.NewGuid():N}.ini");
        try
        {
            AppSettings.ActiveIniFile = new IniFile(tempIni);
            var settings = new AppSettings();
            settings.EnableWebServer = true;
            settings.WebServerPort = 7963;
            settings.GetSection(AppSettingsKeys.Sections.WebServer)
                ?.Set(AppSettingsKeys.WebServer.AllowExternalConnections, "false");

            var effects = new RecordingSettingsSideEffects(settings);
            var vm = new SettingsViewModel(settings, new IniSection("General"), effects);
            vm.Load();

            // 1. Toggle AllowExternalConnections while WebServer is already running
            vm.AllowExternalConnections = true;
            vm.Save();

            Assert.Single(effects.RestartCalls);
            Assert.True(effects.RestartCalls[0].AllowExternal);
            Assert.True(effects.RestartCalls[0].StoredAllowExternal);

            // 2. Enable WebServer and AllowExternalConnections simultaneously from off state
            settings.EnableWebServer = false;
            settings.GetSection(AppSettingsKeys.Sections.WebServer)
                ?.Set(AppSettingsKeys.WebServer.AllowExternalConnections, "false");
            vm.Load();
            vm.EnableWebServer = true;
            vm.AllowExternalConnections = true;
            vm.Save();

            Assert.Equal(2, effects.RestartCalls.Count);
            Assert.True(effects.RestartCalls[1].AllowExternal);
            Assert.True(effects.RestartCalls[1].StoredAllowExternal);
        }
        finally
        {
            AppSettings.ActiveIniFile = prevIni;
            if (File.Exists(tempIni))
            {
                try { File.Delete(tempIni); }
                catch (IOException ex) { AppServices.Logger.Debug($"Temp INI cleanup: {ex.Message}"); }
            }
        }
    }

    [Fact]
    public async Task AccountApiDispatcher_GroupFilterIsCaseInsensitive_AndTogglesQueueSave()
    {
        var oldAccounts = AppServices.AccountRepository.GetAll().ToList();
        var prevIni = AppSettings.ActiveIniFile;
        string prevPass = AppSettings.WebSocketPassword ?? string.Empty;
        var hostBridgeSub = Substitute.For<IHostUiBridgeService>();

        try
        {
            const string pass = "GatewayTestPass99";
            AppSettings.ActiveIniFile = new IniFile();
            AppSettings.WebSocketPassword = pass;
            AppSettings.WebServer.Set("AllowGetAccounts", "true");
            AppSettings.WebServer.Set("EveryRequestRequiresPassword", "false");
            AppSettings.General.Set("EnablePotatoMode", "false");
            AppSettings.General.Set("EnableMultiRbx", "false");

            AccountManager.ResetAccountsForTests(new[]
            {
                new Account { Username = "AlphaVip", UserID = 101, Group = "VIP", SecurityToken = "tok1", Valid = true },
                new Account { Username = "BetaOther", UserID = 102, Group = "Farm", SecurityToken = "tok2", Valid = true }
            });

            var mgr = AccountManager.Instance ?? new AccountManager();
            using var bridgeScope = AppServices.Override<IHostUiBridgeService>(hostBridgeSub);

            AccountManager.ApiRequestContext MakeReq(string path, Dictionary<string, string>? query = null)
            {
                var q = query ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return new AccountManager.ApiRequestContext
                {
                    AbsolutePath = path,
                    IsV2 = false,
                    IsLocal = true,
                    Host = "localhost",
                    Origin = string.Empty,
                    Body = string.Empty,
                    GetQuery = k => q.TryGetValue(k, out string? v) ? v : null,
                    GetHeader = k => k == "X-Shiro-Auth" ? pass : null,
                    SetStatusCode = _ => { },
                    SetContentType = _ => { }
                };
            }

            string namesRes = await mgr.ProcessApiRequestAsync(
                MakeReq("/GetAccounts", new() { ["Group"] = "vip" }));
            Assert.Equal("AlphaVip", namesRes);

            string jsonRes = await mgr.ProcessApiRequestAsync(
                MakeReq("/GetAccountsJson", new() { ["Group"] = "vip" }));
            Assert.Contains("AlphaVip", jsonRes, StringComparison.Ordinal);
            Assert.DoesNotContain("BetaOther", jsonRes, StringComparison.Ordinal);

            await mgr.ProcessApiRequestAsync(MakeReq("/PotatoMode/Toggle"));
            await mgr.ProcessApiRequestAsync(MakeReq("/MultiRoblox/Toggle"));

            hostBridgeSub.Received(2).QueueDeferredSaveSettings();
        }
        finally
        {
            AccountManager.ResetAccountsForTests(oldAccounts);
            AppSettings.WebSocketPassword = prevPass;
            AppSettings.ActiveIniFile = prevIni;
        }
    }

    [Fact]
    public void WebView2Control_UsesStoragePathsBrowserSessionsDirectory_ForPortableIsolation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"Shiro-P0-Wv2-{Guid.NewGuid():N}");
        try
        {
            using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);

            string expectedPrefix = Path.GetFullPath(StoragePaths.BrowserSessionsDirectory);
            string actual = Path.GetFullPath(WebView2Control.GetEmbeddedBrowserDataFolder());

            Assert.StartsWith(expectedPrefix, actual, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(actual));

            string ebRoot = Path.Combine(actual, "EBWebView");
            string oldProfile = Path.Combine(ebRoot, "Shiro_" + Guid.NewGuid().ToString("N"));
            string freshProfile = Path.Combine(ebRoot, "Shiro_" + Guid.NewGuid().ToString("N"));
            string nonShiroDir = Path.Combine(ebRoot, "Cache");
            Directory.CreateDirectory(oldProfile);
            Directory.CreateDirectory(freshProfile);
            Directory.CreateDirectory(nonShiroDir);
            Directory.SetLastWriteTimeUtc(oldProfile, DateTime.UtcNow.AddHours(-2));

            WebView2Control.CleanupOrphanedProfiles(actual);

            Assert.False(Directory.Exists(oldProfile));
            Assert.True(Directory.Exists(freshProfile));
            Assert.True(Directory.Exists(nonShiroDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch (IOException ex) { AppServices.Logger.Debug($"WebView2 test cleanup: {ex.Message}"); }
            }
        }
    }

    [Theory]
    [InlineData("user:1234:pass@1.2.3.4:8080", "http", "1.2.3.4", 8080, "user", "1234:pass")]
    [InlineData("http://user:1234:p:a@1.2.3.4:8080", "http", "1.2.3.4", 8080, "user", "1234:p:a")]
    [InlineData("socks5://alice:8080:pw@proxy.local:1080", "socks5", "proxy.local", 1080, "alice", "8080:pw")]
    [InlineData("10.0.0.1:8080:alice:p@ss:word:123", "http", "10.0.0.1", 8080, "alice", "p@ss:word:123")]
    [InlineData("10.0.0.1:8080:alice@corp.com:pass", "http", "10.0.0.1", 8080, "alice@corp.com", "pass")]
    [InlineData("socks5://proxy.example.com:1080@bob:pass456", "socks5", "proxy.example.com", 1080, "bob", "pass456")]
    public void ProxyStringParser_ColonBeforeAtWithNumericSegment_ParsesAllFormatsAccurately(
        string raw, string expectedScheme, string expectedHost, int expectedPort, string expectedUser, string expectedPass)
    {
        var parsed = ProxyStringParser.ParseProxy(raw);
        Assert.NotNull(parsed);
        Assert.Equal(expectedScheme, parsed!.Protocol);
        Assert.Equal(expectedHost, parsed.Host);
        Assert.Equal(expectedPort, parsed.Port);
        Assert.Equal(expectedUser, parsed.Username);
        Assert.Equal(expectedPass, parsed.Password);
    }

    [Fact]
    public void ProxyBindingService_InvalidProxyIsNotPoisonedInCache_AndClearClientsDoesNotCreateSharedClients()
    {
        ProxyBindingService.ClearClients();

        var clientsField = typeof(SharedHttpClient).GetField("Clients", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(clientsField);
        var sharedDict = clientsField!.GetValue(null) as System.Collections.IDictionary;
        Assert.NotNull(sharedDict);
        int sharedCountBefore = sharedDict!.Count;

        // ClearClients and PruneClients must not instantiate new SharedHttpClient entries.
        ProxyBindingService.ClearClients();
        ProxyBindingService.PruneClients(Array.Empty<string>());
        Assert.Equal(sharedCountBefore, sharedDict.Count);
        Assert.False(SharedHttpClient.IsSharedInstance(null));
        using var localClient = new HttpClient();
        Assert.False(SharedHttpClient.IsSharedInstance(localClient));

        var proxyDictField = typeof(ProxyBindingService).GetField("_proxyClients", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(proxyDictField);
        var proxyDict = proxyDictField!.GetValue(null) as System.Collections.IDictionary;
        Assert.NotNull(proxyDict);

        Assert.Throws<InvalidOperationException>(() => ProxyBindingService.GetClient("bad_proxy_no_port"));
        Assert.Empty(proxyDict!);
    }

    [Fact]
    public async Task AccountSessionMaintenanceService_WhenValidAccountExpires_UpdatesValiditySavesAndNotifiesHostBridge()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"Shiro-P0-Maint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string savePath = Path.Combine(tempDir, "AccountData.json");
        string bakDir = Path.Combine(tempDir, "Backups");

        var oldAccounts = AppServices.AccountRepository.GetAll().ToList();
        var prevStore = AccountPersistenceService.Default.Store;
        var prevStart = AccountPersistenceService.Default.StartTime;
        var hostBridgeSub = Substitute.For<IHostUiBridgeService>();

        try
        {
            using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
            StoragePaths.ReloadVaultKeyCache();
            StoragePaths.GetOrCreateVaultKey();

            RobloxAccountSessionClient.TestHttpClientFactory = _ => new HttpClient(new FakeHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Unauthorized)));

            var acc = new Account
            {
                Username = "ExpiringUser",
                UserID = 80801,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS.--" + new string('Z', 120),
                Valid = true,
                LastAttemptedRefresh = DateTime.MinValue
            };

            AccountManager.ResetAccountsForTests(new[] { acc });
            AccountPersistenceService.Default.Store = new AccountStore(savePath, bakDir);
            AccountPersistenceService.Default.StartTime = DateTime.Now.AddMinutes(-5);

            using var bridgeScope = AppServices.Override<IHostUiBridgeService>(hostBridgeSub);
            using var gate = new SemaphoreSlim(1, 1);

            await AccountSessionMaintenanceService.Default.RefreshStaleCookiesAsync(gate, CancellationToken.None);

            Assert.False(acc.Valid);
            hostBridgeSub.Received(1).NotifyAccountValidityChanged(acc, false);
            Assert.True(File.Exists(savePath));
        }
        finally
        {
            RobloxAccountSessionClient.TestHttpClientFactory = null;
            AccountPersistenceService.Default.Store = prevStore;
            AccountPersistenceService.Default.StartTime = prevStart;
            AccountManager.ResetAccountsForTests(oldAccounts);
            StoragePaths.ReloadVaultKeyCache();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch (IOException ex) { AppServices.Logger.Debug($"Maintenance test cleanup: {ex.Message}"); }
            }
        }
    }

    [Fact]
    public void LogRedactor_RedactsCsrfTicketBasicAuthVersionedWebhooksAndMidLineFormSecrets()
    {
        string csrf = "Failed request x-csrf-token: AbcDef123456 on login";
        Assert.DoesNotContain("AbcDef123456", LogRedactor.Redact(csrf), StringComparison.Ordinal);

        string ticket = "Header rbx-authentication-ticket=TICKET_SECRET_999 failed";
        Assert.DoesNotContain("TICKET_SECRET_999", LogRedactor.Redact(ticket), StringComparison.Ordinal);

        string basic = "Authorization: Basic dXNlcjpwYXNzd29yZDEyMw==";
        string basicRedacted = LogRedactor.Redact(basic);
        Assert.DoesNotContain("dXNlcjpwYXNzd29yZDEyMw==", basicRedacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_TOKEN]", basicRedacted, StringComparison.Ordinal);

        string v10Webhook = "POST https://discord.com/api/v10/webhooks/123456789/SecretWebhookToken_ABC";
        string webhookRedacted = LogRedactor.Redact(v10Webhook);
        Assert.DoesNotContain("SecretWebhookToken_ABC", webhookRedacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_WEBHOOK]", webhookRedacted, StringComparison.Ordinal);

        string midLineSecret = "Debug form payload token=superSecret999&user=bob";
        string midRedacted = LogRedactor.Redact(midLineSecret);
        Assert.DoesNotContain("superSecret999", midRedacted, StringComparison.Ordinal);
        Assert.Contains("token=[REDACTED]", midRedacted, StringComparison.Ordinal);

        string benignEquals = "Diagnostics count=5 ok=true";
        Assert.False(LogRedactor.MayContainSensitiveData(benignEquals.AsSpan()));
    }

    [Theory]
    [InlineData("https://www.roblox.com/catalog/1/-", true)]
    [InlineData("http://example.com/path", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-msdt:/id", false)]
    [InlineData("/relative/path", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Utilities_TryValidateWebUrl_AllowsOnlyHttpAndHttps(string? input, bool expectedValid)
    {
        bool ok = Utilities.TryValidateWebUrl(input, out Uri? uri);
        Assert.Equal(expectedValid, ok);
        if (expectedValid)
            Assert.NotNull(uri);
        else
            Assert.Null(uri);
    }

    [Fact]
    public async Task InputDialog_Prompt_WithoutAvaloniaDesktopOrOwner_ReturnsCancelImmediately()
    {
        // Ensures InputDialog.PromptAsync cannot hang or throw when invoked without desktop UI or valid owner
        var desktopApp = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        if (desktopApp?.MainWindow == null)
        {
            var sw = Stopwatch.StartNew();
            string res = await InputDialog.PromptAsync("Test prompt", "Test", owner: null);
            sw.Stop();
            Assert.Equal("/UC", res);
            Assert.True(sw.ElapsedMilliseconds < 1000, $"Prompt blocked for {sw.ElapsedMilliseconds}ms");
        }
    }

    [Fact]
    public async Task RobloxRateLimitCoordinator_PresenceBackoffDoesNotBlockAuthenticationCategory()
    {
        var coordinator = new RobloxRateLimitCoordinator(AppServices.Logger);
        coordinator.RecordRateLimitHit(RateLimitCategory.Presence, null, defaultBackoffSeconds: 4);

        Assert.True(coordinator.IsBackingOff);
        Assert.True(coordinator.IsBackingOffCategory(RateLimitCategory.Presence));
        Assert.False(coordinator.IsBackingOffCategory(RateLimitCategory.Authentication));

        var sw = Stopwatch.StartNew();
        int res = await coordinator.ExecuteWithPacingAsync(
            RateLimitCategory.Authentication,
            () => Task.FromResult(777),
            minIntervalMs: 0);
        sw.Stop();

        Assert.Equal(777, res);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Authentication waited {sw.ElapsedMilliseconds}ms on Presence backoff.");
    }

    [Fact]
    public void AccountListFilterController_UpdateAccountLiveStatuses_ClearsInGameWhenPresenceNullAndClientStops()
    {
        var accModel = new Account { Username = "OfflineCleanup", Presence = null, Valid = true };
        var accVm = new Shiro.ViewModels.AccountViewModel(accModel) { IsInGame = true };

        bool changed = Shiro.ViewModels.AccountListFilterController.UpdateAccountLiveStatuses(
            new[] { accVm },
            new Dictionary<Account, int>());

        Assert.True(changed);
        Assert.False(accVm.IsInGame);
    }

    [Fact]
    public void AccentThemeService_ParseTheme_MatchesLocalizedMixedCaseStrings()
    {
        var prevLang = LocalizationService.CurrentLanguage;
        try
        {
            LocalizationService.SetLanguage(AppLanguage.Vietnamese);
            Assert.Equal(AccentTheme.Violet, AccentThemeService.ParseTheme(LocalizationService.Get("AccentTheme.Purple")));
            Assert.Equal(AccentTheme.Pink, AccentThemeService.ParseTheme(LocalizationService.Get("AccentTheme.Pink")));
            Assert.Equal(AccentTheme.Emerald, AccentThemeService.ParseTheme(LocalizationService.Get("AccentTheme.Green")));
        }
        finally
        {
            LocalizationService.SetLanguage(prevLang);
        }
    }
}
