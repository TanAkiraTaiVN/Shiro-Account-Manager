using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Shiro.Http;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests
{
    [Collection("CookieGuardSerial")]
    public class RenovationAndPerformanceTests
    {
        [Fact]
        public async Task RobloxAudioService_WasapiNativeVtable_DoesNotThrowInvalidCastOnMtaThread()
        {
            // Verify raw unmanaged COM vtable enumeration runs cleanly on MTA ThreadPool threads without InvalidCastException
            var exception = await Record.ExceptionAsync(() => Task.Run(() =>
            {
                _ = RobloxAudioService.IsAnyRobloxUnmuted();
                _ = RobloxAudioService.ApplyFocusAudioState(0, new[] { 9999991 });
            }));

            Assert.Null(exception);
        }

        [Fact]
        public void RobloxAudioService_ShouldApplySmartMuteState_RespectsComFailureBackoff()
        {
            // During active 10s COM failure backoff window, Smart Mute must not spin every 1.5s
            bool duringBackoff = RobloxAudioService.ShouldApplySmartMuteState(
                activePid: 1234,
                lastActivePid: 0,
                runningRobloxCount: 2,
                lastRunningRobloxCount: -1,
                nowTick: 15_000,
                lastFailureTick: 10_000);
            Assert.False(duringBackoff);

            // Once 10s backoff expires, Smart Mute resumes normally
            bool afterBackoff = RobloxAudioService.ShouldApplySmartMuteState(
                activePid: 1234,
                lastActivePid: 0,
                runningRobloxCount: 2,
                lastRunningRobloxCount: -1,
                nowTick: 21_000,
                lastFailureTick: 10_000);
            Assert.True(afterBackoff);
        }

        [Fact]
        public void RobloxProcess_Modern0745LogPatterns_MatchLegacyAnd2026Formats()
        {
            string modernLaunch = "2026-10-31T09:05:00.745Z,1.745162,29ac,6,Warning [FLog::DataModelStarterModule] Launching experience at 113290951185459";
            string legacyJoin = "2025-05-01T12:00:00.000Z,2.100,1a2b,6 [FLog::Output] ! Joining game '550e8400-e29b-41d4-a716-446655440000' place 189707 at 128.116.50.1";
            string modernConnect = "2026-11-02T08:34:08.487Z,2.487760,8054,6,Info [DFLog::RbxTransportDummyClient] Connected to server at 128.116.46.33:51791";
            Assert.True(RobloxProcess.IsLaunchingExperienceLogLine(modernLaunch));
            Assert.False(RobloxProcess.IsJoiningGameLogLine(modernLaunch));
            Assert.True(RobloxProcess.IsJoiningGameLogLine(legacyJoin));
            Assert.True(RobloxProcess.IsJoiningGameLogLine(modernConnect));

            string modernDisconnect = "2026-10-31T09:06:10.123Z,71.123456,29ac,6,Info [FLog::Network] Sending disconnect with reason: 277";
            Assert.True(RobloxProcess.TryParseDisconnectReason(modernDisconnect, out int reason));
            Assert.Equal(277, reason);

            string modernDmInit = "2026-10-31T09:05:01.154Z,2.154521,29ac,6,Info [FLog::UgcExperienceController] UgcExperienceController: join: initialized dm 0x27bce3f5100";
            Assert.True(RobloxProcess.TryParseDataModelInit(modernDmInit, out long dmId));
            Assert.Equal(0x27bce3f5100L, dmId);

            string modernReturn = "2026-10-31T09:06:46.500Z,107.500,29ac,6 [FLog::SingleSurfaceApp] returnToLuaAppInternal: ... App has been initialized, returning from game.";
            Assert.True(RobloxProcess.IsReturnToLuaAppLine(modernReturn, hasActiveGameDataModel: false));

            string modernStageLuaApp = "2026-10-31T09:06:46.876Z,107.876442,29ac,6 [FLog::SingleSurfaceApp] setStage: (stage:LuaApp)";
            Assert.True(RobloxProcess.IsReturnToLuaAppLine(modernStageLuaApp, hasActiveGameDataModel: true));
            Assert.False(RobloxProcess.IsReturnToLuaAppLine(modernStageLuaApp, hasActiveGameDataModel: false));
        }

        [Fact]
        public void RobloxDirectLauncher_CookieGuardScope_RefCountsNestedBatchesAndDefersRestore()
        {
            Assert.Equal(0, RobloxDirectLauncher.ActiveCookieGuardCount);
            Assert.False(RobloxDirectLauncher.IsCookieGuardHeld);

            using (var outer = RobloxDirectLauncher.BeginCookieGuardScope(restoreDelayOnDisposeMs: 50_000))
            {
                Assert.Equal(1, RobloxDirectLauncher.ActiveCookieGuardCount);
                Assert.True(RobloxDirectLauncher.IsCookieGuardHeld);
                using (var inner = RobloxDirectLauncher.BeginCookieGuardScope(restoreDelayOnDisposeMs: 50_000))
                {
                    Assert.Equal(2, RobloxDirectLauncher.ActiveCookieGuardCount);
                    Assert.True(RobloxDirectLauncher.IsCookieGuardHeld);
                    // Calling RestoreOriginalCookies() without override must defer while guard is held
                    Assert.False(RobloxDirectLauncher.RestoreOriginalCookies());
                }
                Assert.Equal(1, RobloxDirectLauncher.ActiveCookieGuardCount);
                Assert.True(RobloxDirectLauncher.IsCookieGuardHeld);
            }

            Assert.Equal(0, RobloxDirectLauncher.ActiveCookieGuardCount);
            Assert.False(RobloxDirectLauncher.IsCookieGuardHeld);
        }

        [Fact]
        public void MainViewModel_VirtualizedAccountRows_ChunksIntoThreeColumnRowsAndLookupIsO1()
        {
            var vm = new MainViewModel();
            var accounts = new List<Account>();
            for (int i = 1; i <= 7; i++)
            {
                accounts.Add(new Account
                {
                    UserID = 1000 + i,
                    Username = $"Farmer_{i}",
                    Group = i <= 4 ? "Alpha" : "Beta",
                    Valid = true
                });
            }

            vm.LoadAccounts(accounts);
            Assert.Equal(7, vm.FilteredAccounts.Count);
            Assert.Equal(3, vm.FilteredAccountRows.Count); // 3 + 3 + 1

            Assert.True(vm.FilteredAccountRows[0].HasItem2);
            Assert.True(vm.FilteredAccountRows[0].HasItem3);
            Assert.False(vm.FilteredAccountRows[2].HasItem2);
            Assert.False(vm.FilteredAccountRows[2].HasItem3);

            // O(1) lookup by reference, UserID, and case-insensitive Username
            Assert.Same(vm.Accounts[0], vm.TryGetAccountViewModel(accounts[0]));
            Assert.NotNull(vm.TryGetAccountViewModel(new Account { UserID = 1005 }));
            Assert.Equal("Farmer_5", vm.TryGetAccountViewModel(new Account { UserID = 1005 })!.Username);
            Assert.Equal(1003, vm.TryGetAccountViewModel(new Account { Username = "FARMER_3" })!.UserId);

            // Group filter updates FilteredAccountRows for VirtualizingStackPanel
            vm.SelectedGroup = "Alpha";
            Assert.Equal(4, vm.FilteredAccounts.Count);
            Assert.Equal(2, vm.FilteredAccountRows.Count); // 3 + 1
        }

        [Fact]
        public async Task RestClient_ExecuteJsonAsync_StreamsSourceGeneratedJsonAndHandlesRateLimits()
        {
            const string payload = "{\"userPresences\":[{\"userPresenceType\":2,\"lastLocation\":\"Blox Fruits\",\"placeId\":2753915549,\"universeId\":994732206,\"userId\":42}]}";
            using var okHttp = new HttpClient(new FakeHttpHandler().Enqueue(HttpStatusCode.OK, payload));
            using var client = new RestClient(okHttp, "https://presence.roblox.com");

            var (success, data, statusCode, _) = await client.ExecuteJsonAsync(
                new RestRequest("v1/presence/users", Method.Post).AddJsonBody(new RobloxPresenceUsersRequest(new[] { 42L })),
                ShiroAppJson.Default.RobloxPresenceEnvelope);

            Assert.True(success);
            Assert.Equal(HttpStatusCode.OK, statusCode);
            Assert.NotNull(data);
            Assert.Single(data!.userPresences);
            Assert.Equal(UserPresenceType.InGame, data.userPresences[0].userPresenceType);
            Assert.Equal(42L, data.userPresences[0].userId);

            using var rateLimitedHttp = new HttpClient(new FakeHttpHandler().Enqueue(HttpStatusCode.TooManyRequests, "{}"));
            using var rlClient = new RestClient(rateLimitedHttp, "https://presence.roblox.com");
            var rlResult = await rlClient.ExecuteJsonAsync(
                new RestRequest("v1/presence/users", Method.Post),
                ShiroAppJson.Default.RobloxPresenceEnvelope);

            Assert.False(rlResult.Success);
            Assert.Equal(HttpStatusCode.TooManyRequests, rlResult.StatusCode);
        }

        [Fact]
        public void ForegroundWindowService_ShouldDispatchProcessChange_DeduplicatesIdenticalPid()
        {
            uint lastPid = 0;
            Assert.False(ForegroundWindowService.ShouldDispatchProcessChange(0, ref lastPid));
            Assert.True(ForegroundWindowService.ShouldDispatchProcessChange(4200, ref lastPid));
            Assert.Equal(4200u, lastPid);
            Assert.False(ForegroundWindowService.ShouldDispatchProcessChange(4200, ref lastPid));
            Assert.True(ForegroundWindowService.ShouldDispatchProcessChange(5300, ref lastPid));
            Assert.Equal(5300u, lastPid);
        }

        [Fact]
        public void RobloxJobObjectService_BuildCpuRateControlInfo_ConfiguresHardCapAndDisablesCleanly()
        {
            var disabled = RobloxJobObjectService.BuildCpuRateControlInfo(0);
            Assert.Equal(0u, disabled.ControlFlags);
            Assert.Equal(0u, disabled.CpuRate);

            var capped65 = RobloxJobObjectService.BuildCpuRateControlInfo(65);
            Assert.Equal(0x5u, capped65.ControlFlags); // ENABLE (0x1) | HARD_CAP (0x4)
            Assert.Equal(6500u, capped65.CpuRate);

            bool okSet = RobloxJobObjectService.SetCpuRateLimit(70);
            Assert.True(okSet);
            Assert.Equal(70, RobloxJobObjectService.CurrentCpuRatePercent);

            bool okClear = RobloxJobObjectService.SetCpuRateLimit(0);
            Assert.True(okClear);
            Assert.Equal(0, RobloxJobObjectService.CurrentCpuRatePercent);
            RobloxJobObjectService.Close();
        }

        [Fact]
        public void MainViewModel_NormalizePlaceIdInput_ExtractsPlaceIdFromRobloxUrl()
        {
            var vm = new MainViewModel();
            vm.NormalizePlaceIdInput("https://www.roblox.com/games/2753915549/Blox-Fruits");
            Assert.Equal("2753915549", vm.PlaceId);
        }

        [Fact]
        public void AccountRuntimeStateStore_PersistsWindowBoundsAndLastUseWithoutDirtyingVault()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "ShiroRuntimeStateTest_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                AccountRuntimeStateStore.ResetInMemoryCacheForTests();

                var acc = new Account
                {
                    UserID = 777888,
                    Username = "RuntimeHero",
                    LastUse = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    IsDirty = false
                };

                DateTime newerUse = new DateTime(2026, 10, 31, 15, 30, 0, DateTimeKind.Utc);
                AccountRuntimeStateStore.RecordLastUse(acc, newerUse, filePathOverride: tempFile);
                AccountRuntimeStateStore.RecordWindowBounds(acc, 120, 240, 800, 600, filePathOverride: tempFile);

                // Verify SetWindowBounds on Account does NOT mark Account.IsDirty = true
                acc.IsDirty = false;
                acc.SetWindowBounds(120, 240, 800, 600);
                Assert.False(acc.IsDirty);

                // Hydrate into a fresh Account instance loaded from encrypted vault
                var reloaded = new Account
                {
                    UserID = 777888,
                    Username = "RuntimeHero",
                    LastUse = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    IsDirty = false
                };

                int hydrated = AccountRuntimeStateStore.HydrateAccounts(new[] { reloaded }, filePathOverride: tempFile);
                Assert.Equal(1, hydrated);
                Assert.False(reloaded.IsDirty);
                Assert.Equal(newerUse, reloaded.LastUse);
                Assert.Equal("120", reloaded.GetField("Window_Position_X"));
                Assert.Equal("240", reloaded.GetField("Window_Position_Y"));
                Assert.Equal("800", reloaded.GetField("Window_Width"));
                Assert.Equal("600", reloaded.GetField("Window_Height"));
            }
            finally
            {
                AccountRuntimeStateStore.ResetInMemoryCacheForTests();
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        [Fact]
        public void ScrollVirtualization_RowFlagsAndNonBlockingAvatarGetter_ReturnImmediately()
        {
            var a1 = new AccountViewModel(new Account { UserID = 9876543210123L, Username = "ScrollAcc1" });
            var row = new AccountRowViewModel(a1);
            Assert.True(row.HasItem1);
            Assert.False(row.HasItem2);
            Assert.False(row.HasItem3);

            // Repeated AvatarImage reads during scroll frames must never throw or block
            for (int i = 0; i < 100; i++)
            {
                Assert.Null(a1.AvatarImage);
            }

            AccountViewModel.ClearAvatarMemoryCache();
            Assert.Null(a1.AvatarImage);

            bool invoked = false;
            AppDispatcher.PostBackground(() => invoked = true);
            Assert.True(invoked);
        }

        [Fact]
        public void LaunchAndJoinCommands_WhenZeroAccountsSelected_NeverSilentlyPickUnselectedAccount()
        {
            var vm = new MainViewModel();
            var acc1 = new Account { UserID = 101, Username = "UnselectedFirst", Valid = true };
            var acc2 = new Account { UserID = 102, Username = "SelectedSecond", Valid = true };
            vm.LoadAccounts(new List<Account> { acc1, acc2 });
            vm.PlaceId = "2753915549";

            int multiHomeCalls = 0;
            int joinServerCalls = 0;
            IList<AccountViewModel>? joinedAccounts = null;

            vm.OnMultiLaunchRequested = _ => multiHomeCalls++;
            vm.OnJoinServerSelectedRequested = list =>
            {
                joinServerCalls++;
                joinedAccounts = list;
            };

            // 0 accounts selected -> NONE of the launch/play/join commands may launch acc1
            vm.LaunchSelectedCommand.Execute(null);
            vm.JoinServerSelectedCommand.Execute(null);
            vm.ServerListViewModel.LaunchGameCommand.Execute(new GameSearchItemViewModel { PlaceId = 2753915549, Name = "Blox Fruits" });
            vm.ServerListViewModel.LaunchFavoriteCommand.Execute(new FavoriteGameItemViewModel { PlaceId = 2753915549, Name = "Blox Fruits" });
            vm.ServerListViewModel.JoinServerCommand.Execute(new ServerItemViewModel { JobId = "550e8400-e29b-41d4-a716-446655440000", Type = "Public" });

            Assert.Equal(0, multiHomeCalls);
            Assert.Equal(0, joinServerCalls);

            // Now explicitly select acc2 -> ONLY acc2 is launched
            vm.FilteredAccounts.Single(a => a.Username == "SelectedSecond").IsSelected = true;
            vm.ServerListViewModel.LaunchGameCommand.Execute(new GameSearchItemViewModel { PlaceId = 2753915549, Name = "Blox Fruits" });

            Assert.Equal(1, joinServerCalls);
            Assert.NotNull(joinedAccounts);
            Assert.Single(joinedAccounts!);
            Assert.Equal("SelectedSecond", joinedAccounts![0].Username);
        }

        [Fact]
        public void Sync_WhenLargeDifference_FiresSingleResetEvent()
        {
            var collection = new BulkObservableCollection<int>(System.Linq.Enumerable.Range(1, 100));
            int resetCount = 0;
            int replaceCount = 0;
            int removeCount = 0;

            collection.CollectionChanged += (s, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resetCount++;
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace) replaceCount++;
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove) removeCount++;
            };

            // Reversing 100 items is a major reordering
            var reversed = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Reverse(System.Linq.Enumerable.Range(1, 100)));
            collection.Sync(reversed);

            Assert.Equal(1, resetCount);
            Assert.Equal(0, replaceCount);
            Assert.Equal(0, removeCount);
            Assert.Equal(reversed, collection);
        }

        [Fact]
        public void Sync_WhenSingleItemModified_PreservesFineGrainedReplace()
        {
            var list = new List<int> { 1, 2, 3, 4, 5 };
            var collection = new BulkObservableCollection<int>(list);
            int replaceCount = 0;

            collection.CollectionChanged += (s, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace) replaceCount++;
            };

            var updated = new List<int> { 1, 2, 99, 4, 5 };
            collection.Sync(updated);

            Assert.Equal(1, replaceCount);
            Assert.Equal(updated, collection);
        }
    }
}
