#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NSubstitute;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SequentialStateTests")]
    public class CoreCoverageExpansionTests
    {
        [Fact]
        public void HostUiBridgeService_InvokesRegisteredCallbacks()
        {
            var bridge = new HostUiBridgeService();
            string? lastStatus = null;
            bridge.SetDashboardStatusHandler = msg => lastStatus = msg;
            bridge.SetDashboardStatus("Ready");
            Assert.Equal("Ready", lastStatus);

            bool refreshed = false;
            bridge.RefreshAccountViewHandler = _ => refreshed = true;
            bridge.RefreshAccountView();
            Assert.True(refreshed);

            bridge.SignalNextLaunchAccount();
            bridge.CancelLaunching();
            bridge.UpdateMultiRoblox();
            bridge.CloseAllRobloxFromUi();
            bridge.MultiLaunchHomeFromHost();
            bridge.SaveSettings();
            bridge.QueueDeferredSaveSettings();
            bridge.EnsureBackendInitialized();
            bridge.InitializePostStartup();
            bridge.SetAutoCookieRefreshEnabled(true);
            Assert.Empty(bridge.GetCurrentSelectedAccountsFromHost());

            var mockDialog = Substitute.For<IDialogService>();
            mockDialog.ShowInfo("Test");
            mockDialog.Received(1).ShowInfo("Test");
        }

        [Fact]
        public void DomainContracts_CanBeSubstitutedWithNSubstitute()
        {
            var toast = Substitute.For<IToastService>();
            var win = Substitute.For<IRobloxWindowService>();
            var mem = Substitute.For<IMemoryOptimizer>();

            toast.ShowInfo("Message");
            win.TileRunningWindows();
            mem.TrimWorkingSets();

            toast.Received(1).ShowInfo("Message");
            win.Received(1).TileRunningWindows();
            mem.Received(1).TrimWorkingSets();
        }

        [Fact]
        public void AccountFileDecoder_CoversAllBranches()
        {
            byte[] key = RandomNumberGenerator.GetBytes(32);
            byte[] wrongKey = RandomNumberGenerator.GetBytes(32);
            byte[] validJson = Encoding.UTF8.GetBytes("[]");
            byte[] invalidJson = Encoding.UTF8.GetBytes("{not-valid-json");

            byte[] encryptedValid = Cryptography.Encrypt(validJson, key);
            byte[] encryptedInvalid = Cryptography.Encrypt(invalidJson, key);

            var baseKeys = new AccountDecodeKeys(
                GetVaultKeyCopy: () => wrongKey.AsSpan().ToArray(),
                TryRecoverVaultKey: _ => wrongKey.AsSpan().ToArray(),
                UnprotectDpapi: _ => throw new CryptographicException("Not DPAPI"),
                PlaintextOptIn: false);

            // 1. Wrong masterPassword -> NeedsMasterPassword
            var resWrongMaster = AccountFileDecoder.Decode(encryptedValid, wrongKey, baseKeys);
            Assert.Equal(AccountDecodeStatus.NeedsMasterPassword, resWrongMaster.Status);

            // 2. TryRecoverVaultKey throws -> NeedsMasterPassword
            var throwingKeys = baseKeys with
            {
                GetVaultKeyCopy = () => throw new InvalidOperationException("vault err"),
                TryRecoverVaultKey = _ => throw new InvalidOperationException("recover err")
            };
            var resThrowRecover = AccountFileDecoder.Decode(encryptedValid, null, throwingKeys);
            Assert.Equal(AccountDecodeStatus.NeedsMasterPassword, resThrowRecover.Status);

            // 3. TryRecoverVaultKey returns wrong key -> NeedsMasterPassword
            var resWrongRecover = AccountFileDecoder.Decode(encryptedValid, null, baseKeys);
            Assert.Equal(AccountDecodeStatus.NeedsMasterPassword, resWrongRecover.Status);

            // 4. Encrypted corrupt JSON -> Corrupt
            var validVaultKeys = baseKeys with { GetVaultKeyCopy = () => key.AsSpan().ToArray() };
            var resCorruptEnc = AccountFileDecoder.Decode(encryptedInvalid, null, validVaultKeys);
            Assert.Equal(AccountDecodeStatus.Corrupt, resCorruptEnc.Status);

            // 5. UnprotectDpapi succeeds with legacyScope: true -> Loaded + NeedsMigration
            var dpapiKeys = baseKeys with { UnprotectDpapi = _ => (validJson, true) };
            var resDpapi = AccountFileDecoder.Decode(validJson, null, dpapiKeys);
            Assert.Equal(AccountDecodeStatus.Loaded, resDpapi.Status);
            Assert.True(resDpapi.NeedsMigration);

            // 6. PlaintextOptIn with corrupt JSON -> Corrupt
            var plainCorruptKeys = baseKeys with { PlaintextOptIn = true };
            var resPlainCorrupt = AccountFileDecoder.Decode(invalidJson, null, plainCorruptKeys);
            Assert.Equal(AccountDecodeStatus.Corrupt, resPlainCorrupt.Status);
        }

        [Fact]
        public void StoragePaths_And_VaultKey_CoverUpgradeAndCorruptBranches()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroCov_" + Guid.NewGuid().ToString("N"));
            try
            {
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    Assert.NotEmpty(StoragePaths.AccountDumpsDirectory);
                    Assert.NotEmpty(StoragePaths.NexusOutputDirectory);
                    Assert.NotEmpty(StoragePaths.LogDirectory);
                    Assert.Equal(tempDir, StoragePaths.ResolveUserFile(tempDir));
                    Assert.Contains("rel.txt", StoragePaths.ResolveUserFile("rel.txt"));
                    Assert.NotEmpty(StoragePaths.ResolveAsset(""));

                    Assert.Throws<ArgumentException>(() => StoragePaths.SaveRecoveredVaultKey(new byte[10]));

                    // Raw 32-byte key on disk -> upgraded to DPAPI on GetOrCreateVaultKey
                    StoragePaths.ClearCachedVaultKey();
                    byte[] rawKey = RandomNumberGenerator.GetBytes(32);
                    File.WriteAllBytes(StoragePaths.VaultKeyFile, rawKey);
                    byte[] loadedRaw = StoragePaths.GetOrCreateVaultKey();
                    Assert.Equal(rawKey, loadedRaw);

                    // Corrupt 7-byte key on disk -> backed up to .bak and regenerated
                    StoragePaths.ClearCachedVaultKey();
                    if (File.Exists(StoragePaths.VaultKeyFile + ".bak"))
                        File.Delete(StoragePaths.VaultKeyFile + ".bak");
                    File.WriteAllBytes(StoragePaths.VaultKeyFile, new byte[] { 1, 2, 3, 4, 5, 6, 7 });
                    byte[] regenerated = StoragePaths.GetOrCreateVaultKeyCopy();
                    Assert.Equal(32, regenerated.Length);
                    Assert.True(File.Exists(StoragePaths.VaultKeyFile + ".bak"));

                    // CleanupStaleRuntimeArtifacts with stale & fresh browser session directories + AtomicWriteText empty
                    string staleSession = Path.Combine(StoragePaths.BrowserSessionsDirectory, "stale_prof");
                    string freshSession = Path.Combine(StoragePaths.BrowserSessionsDirectory, "fresh_prof");
                    Directory.CreateDirectory(staleSession);
                    Directory.CreateDirectory(freshSession);
                    Directory.SetLastWriteTimeUtc(staleSession, DateTime.UtcNow.AddDays(-10));
                    Directory.SetLastWriteTimeUtc(freshSession, DateTime.UtcNow);
                    int cleaned = StoragePaths.CleanupStaleRuntimeArtifacts(TimeSpan.FromDays(2));
                    Assert.True(cleaned >= 1);
                    Assert.False(Directory.Exists(staleSession));
                    Assert.True(Directory.Exists(freshSession));

                    string emptyTxt = Path.Combine(tempDir, "empty_atomic.txt");
                    StoragePaths.AtomicWriteText(emptyTxt, "");
                    Assert.Equal(0, new FileInfo(emptyTxt).Length);
                    Assert.False(StoragePaths.IsWithinDirectory("\0bad", tempDir));

                    // Locked VaultKeyFile error branches
                    StoragePaths.ClearCachedVaultKey();
                    using (var keyLock = new FileStream(StoragePaths.VaultKeyFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        StoragePaths.SaveRecoveredVaultKey(RandomNumberGenerator.GetBytes(32));
                        StoragePaths.ClearCachedVaultKey();
                        byte[] fallbackKey = StoragePaths.GetOrCreateVaultKey();
                        Assert.Equal(32, fallbackKey.Length);
                    }
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch
                {
                    // Logger may hold a file handle in tempDir
                }
            }
        }

        [Fact]
        public void AccountStore_CoversBackupLockFallbackAndPruning()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroStoreCov_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string accFile = Path.Combine(tempDir, "AccountData.json");
                string backupsDir = Path.Combine(tempDir, "Backups");
                var store = new AccountStore(accFile, backupsDir);

                store.CreateBackup(Array.Empty<byte>());
                store.CreateHistoricalBackup(Array.Empty<byte>());
                Assert.Empty(store.GetHistoricalBackups());

                byte[] payload1 = Encoding.UTF8.GetBytes("[{\"Username\":\"u1\"}]");
                byte[] payload2 = Encoding.UTF8.GetBytes("[{\"Username\":\"u2\"}]");
                store.Write(payload1);
                store.WriteWithBackup(payload2, forceHistoricalBackup: true);

                // Create 12 historical backups to trigger pruning (>10)
                Directory.CreateDirectory(backupsDir);
                for (int i = 0; i < 12; i++)
                {
                    string p = Path.Combine(backupsDir, $"AccountData_20260101_1200{i:D2}.json.bak");
                    File.WriteAllBytes(p, payload1);
                }
                store.CreateHistoricalBackup(payload2);
                Assert.True(store.GetHistoricalBackups().Count <= AccountStore.MaxHistoricalBackups);

                // RestoreHistoricalBackup edge cases
                Assert.False(store.RestoreHistoricalBackup(""));
                string emptyBak = Path.Combine(tempDir, "empty.bak");
                File.WriteAllBytes(emptyBak, Array.Empty<byte>());
                Assert.False(store.RestoreHistoricalBackup(emptyBak));

                var validBak = store.GetHistoricalBackups()[0];
                Assert.True(store.RestoreHistoricalBackup(validBak));

                // Read() fallback to .backup when primary is locked
                using (var lockStream = new FileStream(accFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    byte[] fallbackBytes = store.Read();
                    Assert.NotEmpty(fallbackBytes);

                    // Both primary and backup locked -> returns empty
                    using (var lockBak = new FileStream(store.BackupFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        Assert.Empty(store.Read());
                        Assert.False(store.RestoreHistoricalBackup(store.BackupFile));
                    }
                }
            }
            finally
            {
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch
                {
                    // Logger may hold a file handle in tempDir
                }
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task RobloxServerQueryService_And_SettingsApi_CoverParsersAndGuards()
        {
            var querySvc = RobloxServerQueryService.Default;
            var emptyRes = await querySvc.QueryServerRegionAndPingAsync(null!, 0, "");
            Assert.False(emptyRes.Success);

            var parseMethod = typeof(RobloxServerQueryService).GetMethod(
                "TryParseRegionFromJson",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(parseMethod);

            object?[] args1 = { "{\"city\":\"Tokyo\",\"countryCode\":\"JP\"}", null };
            Assert.True((bool)parseMethod!.Invoke(null, args1)!);
            Assert.Equal("Tokyo, JP", args1[1]);

            object?[] args2 = { "{\"success\":false}", null };
            Assert.False((bool)parseMethod.Invoke(null, args2)!);

            object?[] args3 = { "{\"status\":\"fail\"}", null };
            Assert.False((bool)parseMethod.Invoke(null, args3)!);

            var settingsApi = RobloxSettingsApiService.Instance;
            Assert.False(await settingsApi.CheckPinAsync(null!));
            Assert.False(await settingsApi.UnlockPinAsync(null!, "123"));
            Assert.False(await settingsApi.SetFollowPrivacyAsync(null!, 0));
            Assert.False(await settingsApi.ChangePasswordAsync(null!, "a", "b"));
            Assert.False(await settingsApi.ChangeEmailAsync(null!, "a", "b"));
            Assert.False(await settingsApi.LogOutOfOtherSessionsAsync(null!));

            var accUnlocked = new Account { Username = "pinUser", PinUnlocked = DateTime.Now.AddMinutes(5) };
            Assert.True(await settingsApi.CheckPinAsync(accUnlocked));
            Assert.True(await settingsApi.UnlockPinAsync(accUnlocked, "1234"));

            var accNoToken = new Account { Username = "noTok", SecurityToken = "" };
            Assert.False(await settingsApi.CheckPinAsync(accNoToken, Internal: false));
            Assert.False(await settingsApi.CheckPinAsync(accNoToken, Internal: true));

            var workflow = AccountWorkflowService.Default;
            Assert.NotNull(workflow);

            Assert.False(RobloxSettingsApiService.ParseAccessCode(null!, out _));
            var legacyResp = new Shiro.Http.RestResponse { StatusCode = System.Net.HttpStatusCode.OK, Content = "Roblox.GameLauncher.joinPrivateGame(1818, '11111111-2222-3333-4444-555555555555'" };
            Assert.True(RobloxSettingsApiService.ParseAccessCode(legacyResp, out string parsedCode1));
            Assert.Equal("11111111-2222-3333-4444-555555555555", parsedCode1);

            var modernResp = new Shiro.Http.RestResponse { StatusCode = System.Net.HttpStatusCode.OK, Content = "{\"accessCode\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"}" };
            Assert.True(RobloxSettingsApiService.ParseAccessCode(modernResp, out string parsedCode2));
            Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", parsedCode2);

            var (srvOk, _) = await settingsApi.SetServerAsync(null!, 1818, "job");
            Assert.False(srvOk);
            await settingsApi.SetDisplayNameAsync(null!, "Name");
            await settingsApi.SetAvatarAsync(null!, "");
            await settingsApi.SetAvatarAsync(accUnlocked, "{bad-json");
            await settingsApi.SetAvatarAsync(accUnlocked, "[1,2,3]");

            var mainVm = new Shiro.ViewModels.MainViewModel();
            var srvVm = new Shiro.ViewModels.ServerListViewModel(mainVm);
            srvVm.SearchPlayerUsername = "";
            srvVm.SearchPlayerCommand.Execute(null);
            Assert.NotEmpty(srvVm.PlayerSearchStatus);

            srvVm.SearchPlayerUsername = "builderman";
            srvVm.PlaceIdInput = "invalid";
            srvVm.SearchPlayerCommand.Execute(null);
            Assert.NotEmpty(srvVm.PlayerSearchStatus);

            srvVm.ToggleSortPlayersCommand.Execute(null);
            Assert.Equal("▲", srvVm.PlayersSortHeaderIndicator);
            srvVm.ToggleSortPlayersCommand.Execute(null);
            Assert.Equal("▼", srvVm.PlayersSortHeaderIndicator);
            srvVm.ToggleSortPlayersCommand.Execute(null);

            srvVm.ToggleSortPingCommand.Execute(null);
            Assert.Equal("▲", srvVm.PingSortHeaderIndicator);
            srvVm.ToggleSortPingCommand.Execute(null);
            Assert.Equal("▼", srvVm.PingSortHeaderIndicator);
            srvVm.ToggleSortPingCommand.Execute(null);

            srvVm.HideFullServers = true;
            srvVm.HideVipServers = true;
            srvVm.NotifyCurrentGameChanged();
            srvVm.NotifyRecentGamesChanged();

            var gameItem = new Shiro.ViewModels.GameSearchItemViewModel { PlaceId = 999991, Name = "TestGame", ThumbnailUrl = "https://tr.rbxcdn.com/test" };
            srvVm.AddFavoriteCommand.Execute(gameItem);
            srvVm.AddFavoriteCommand.Execute(gameItem);
            var fav = System.Linq.Enumerable.FirstOrDefault(srvVm.Favorites, f => f.PlaceId == 999991);
            if (fav != null)
            {
                srvVm.SelectFavoriteCommand.Execute(fav);
                srvVm.RemoveFavoriteCommand.Execute(fav);
            }

            srvVm.SelectGameCommand.Execute(gameItem);
            var recentItem = new Shiro.ViewModels.RecentGameItemViewModel { PlaceId = 999992, Name = "RecentTest" };
            srvVm.SelectRecentGameInLibraryCommand.Execute(recentItem);
            srvVm.LaunchRecentGameInLibraryCommand.Execute(recentItem);

            var srvItem = new Shiro.ViewModels.ServerItemViewModel
            {
                JobId = "job-123",
                Type = "Public",
                Playing = 5,
                MaxPlayers = 10,
                Ping = 45,
                CountryCode = "JP",
                FlagEmoji = "JP",
                Region = "Tokyo",
                Ip = "128.116.1.1",
                IsBusyPinging = true,
                RawData = new ServerData { id = "job-123", playing = 5, maxPlayers = 10, ping = 45 }
            };
            Assert.NotEmpty(srvItem.PlayerDisplay);
            Assert.True(srvItem.PlayerPercentage > 0);
            Assert.NotEmpty(srvItem.PingDisplay);
            Assert.NotNull(srvItem.PingColor);
            srvItem.Ping = 120;
            Assert.NotNull(srvItem.PingColor);
            srvItem.Ping = 200;
            Assert.NotNull(srvItem.PingColor);
            srvItem.Ping = -1;
            Assert.NotNull(srvItem.PingColor);
            Assert.NotEmpty(srvItem.RegionDisplay);
            srvItem.NotifyLanguageChanged();
            srvVm.CopyJobIdCommand.Execute(srvItem);

            srvVm.WatcherEnabled = !srvVm.WatcherEnabled;
            srvVm.ExitOnBeta = !srvVm.ExitOnBeta;
            srvVm.ExitIfNoConnection = !srvVm.ExitIfNoConnection;
            srvVm.SaveWindowPositions = !srvVm.SaveWindowPositions;
            srvVm.VerifyDataModel = !srvVm.VerifyDataModel;
            srvVm.IgnoreExistingProcesses = !srvVm.IgnoreExistingProcesses;
            srvVm.CloseRbxMemory = !srvVm.CloseRbxMemory;
            srvVm.MemoryThresholdMb = 1024;

            var accModel = new Account
            {
                Username = "TestPlayer99",
                UserID = 12345678,
                SecurityToken = "_|WARNING:-DO-NOT-SHARE-THIS|--token",
                Valid = true,
                RobuxBalance = 500
            };
            var accVm = new Shiro.ViewModels.AccountViewModel(accModel);
            accVm.Alias = "MyAlias";
            accVm.Group = "VIP";
            accVm.Description = "Test description";
            accVm.Pinned = true;
            accVm.Pinned = false;
            accVm.Valid = false;
            accVm.Valid = true;
            accVm.IsSelected = true;
            accVm.IsLaunching = true;
            accVm.IsInGame = true;
            accVm.IsLocalClientRunning = true;
            accVm.IsCommandPaletteSelected = true;
            accVm.LiveMemoryText = "512 MB";
            accVm.NotifyLanguageChanged();
            accVm.NotifyStatusIssueChanged();
            accVm.NotifyDisplayNameChanged();
            accVm.NotifyThemeChanged();
            accVm.NotifyValidityChanged();
            accVm.NotifyRobuxChanged();
            accVm.NotifyAllPropertiesChanged();
            accVm.UpdateModel(new Account { Username = "UpdatedPlayer", UserID = 999 });
            Assert.NotEmpty(accVm.PrimaryTitle);
            Assert.NotEmpty(accVm.SecondaryTitle);
            Assert.NotEmpty(accVm.DisplayTitle);
            Assert.NotEmpty(accVm.GroupDisplay);
            Assert.NotEmpty(accVm.GroupTooltip);
            Assert.NotEmpty(accVm.LaunchButtonText);
            Assert.NotEmpty(accVm.FocusWindowButtonText);
            Assert.NotEmpty(accVm.ViewBanButtonText);
            Assert.NotEmpty(accVm.BanNoticeText);
            Assert.NotEmpty(accVm.Initials);
            Assert.NotNull(accVm.RobuxText);
            Assert.NotNull(accVm.RobuxFormatted);
            Assert.NotEmpty(accVm.RobuxTooltip);
            Account.MarkNativeAuthRateLimited(15, "TestPlayer99", "TicketAPI");
        }
    }
}
