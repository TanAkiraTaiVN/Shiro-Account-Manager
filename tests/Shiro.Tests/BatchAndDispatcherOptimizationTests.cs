#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Shiro.Classes;
using Shiro.Services;
using Shiro.ViewModels;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class BatchAndDispatcherOptimizationTests
    {
        [Fact]
        public void BatchLookupResult_ResolvesByRequestIdFirst_AndFallsBackToTargetId()
        {
            var lookup = new Batch.BatchLookupResult();
            lookup.AddItems(new List<ThumbnailBatchItem>
            {
                new ThumbnailBatchItem
                {
                    RequestId = "1001::AvatarHeadShot:150x150:png:regular",
                    TargetId = 1001,
                    ErrorCode = 0,
                    ImageUrl = "https://tr.rbxcdn.com/headshot-150.png"
                },
                new ThumbnailBatchItem
                {
                    RequestId = "1001::AvatarBust:420x420:png:regular",
                    TargetId = 1001,
                    ErrorCode = 0,
                    ImageUrl = "https://tr.rbxcdn.com/bust-420.png"
                },
                new ThumbnailBatchItem
                {
                    RequestId = null,
                    TargetId = 2002,
                    ErrorCode = 0,
                    ImageUrl = "https://tr.rbxcdn.com/fallback-2002.png"
                },
                new ThumbnailBatchItem
                {
                    RequestId = "3003::AvatarHeadShot:150x150:png:regular",
                    TargetId = 3003,
                    ErrorCode = 4, // Error item should be ignored
                    ImageUrl = "https://tr.rbxcdn.com/error.png"
                }
            });

            // Exact RequestId disambiguates multiple thumbnail types for the same TargetId (1001)
            Assert.Equal(
                "https://tr.rbxcdn.com/headshot-150.png",
                lookup.Resolve("1001::AvatarHeadShot:150x150:png:regular", 1001));
            Assert.Equal(
                "https://tr.rbxcdn.com/bust-420.png",
                lookup.Resolve("1001::AvatarBust:420x420:png:regular", 1001));

            // Falls back to TargetId when RequestId is not matched
            Assert.Equal(
                "https://tr.rbxcdn.com/fallback-2002.png",
                lookup.Resolve("2002::GameIcon:150x150:png:regular", 2002));

            // Non-existent or errored item returns UNK
            Assert.Equal("UNK", lookup.Resolve("3003::AvatarHeadShot:150x150:png:regular", 3003));
            Assert.Equal("UNK", lookup.Resolve("9999::AvatarHeadShot:150x150:png:regular", 9999));
        }

        [Fact]
        public void AccountLineParser_ClassifyAndParseAccountLine_ZeroSplitSpanEnumeratorMatchesAllFormats()
        {
            string cookieA = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you|_" + new string('A', 60);
            string cookieB = "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you|_" + new string('B', 60);

            string rawInput = string.Join("\r\n", new[]
            {
                "",
                "   ",
                $"userOne:colonPass:{cookieA}",
                $"userTwo|pipePass|{cookieB}",
                $"   .ROBLOSECURITY={cookieA}; path=/; domain=.roblox.com   ",
                "plainUser:plainPassword123",
                "invalid_single_token_without_delim"
            });

            var stats = AccountLineParser.Classify(rawInput);

            Assert.Equal(3, stats.Cookies.Count);
            Assert.Equal((cookieA, "colonPass"), stats.Cookies[0]);
            Assert.Equal((cookieB, "pipePass"), stats.Cookies[1]);
            Assert.Equal((cookieA, (string?)null), stats.Cookies[2]);
            Assert.Single(stats.UserPass);
            Assert.Equal(("plainUser", "plainPassword123"), stats.UserPass[0]);
            Assert.Equal(1, stats.Failed);

            var parsedComboColon = AccountLineParser.ParseAccountLine($"userOne:colonPass:{cookieA}");
            Assert.Equal("colonPass", parsedComboColon.Password);
            Assert.Equal(cookieA, parsedComboColon.Cookie);

            var parsedComboPipe = AccountLineParser.ParseAccountLine($"userTwo|pipePass|{cookieB}");
            Assert.Equal("pipePass", parsedComboPipe.Password);
            Assert.Equal(cookieB, parsedComboPipe.Cookie);

            var parsedCookieOnly = AccountLineParser.ParseAccountLine($".ROBLOSECURITY={cookieA}; HttpOnly");
            Assert.Null(parsedCookieOnly.Password);
            Assert.Equal(cookieA, parsedCookieOnly.Cookie);
        }

        [Fact]
        public void AccountApiDispatcher_ResolveTargetAccount_AndSourceGenPayloads_WorkCorrectly()
        {
            var acc = new Account
            {
                Username = "DispatcherApiTestUser",
                UserID = 777888999L,
                Alias = "ApiAlias",
                Description = "Desc",
                Group = "TestGroup",
                SecurityToken = "token123",
                Valid = true
            };

            AppServices.AccountRepository.AddThreadSafe(acc);
            try
            {
                var byName = AccountApiDispatcher.ResolveTargetAccount("dispatcherapitestuser");
                var byId = AccountApiDispatcher.ResolveTargetAccount("777888999");
                var missing = AccountApiDispatcher.ResolveTargetAccount("non_existent_dispatcher_user");

                Assert.Same(acc, byName);
                Assert.Same(acc, byId);
                Assert.Null(missing);

                string v2Resp = AccountApiDispatcher.WebServerResponse("hello", true);
                using var doc = JsonDocument.Parse(v2Resp);
                Assert.True(doc.RootElement.GetProperty("Success").GetBoolean());
                Assert.Equal("hello", doc.RootElement.GetProperty("Message").GetString());

                string exportedJson = AccountExportService.ExportAccounts(new[] { acc }, AccountExportFormat.Json);
                using var exportDoc = JsonDocument.Parse(exportedJson);
                Assert.Equal(JsonValueKind.Array, exportDoc.RootElement.ValueKind);
                Assert.Equal(1, exportDoc.RootElement.GetArrayLength());
                var first = exportDoc.RootElement[0];
                Assert.Equal("DispatcherApiTestUser", first.GetProperty("Username").GetString());
                Assert.Equal(777888999L, first.GetProperty("UserID").GetInt64());
                Assert.Equal("token123", first.GetProperty("SecurityToken").GetString());
            }
            finally
            {
                AppServices.AccountRepository.Remove(acc);
            }
        }

        [Fact]
        public void ServerListViewModel_ApplyServerSortAndFilter_SortsAndFiltersInPlace()
        {
            var vm = new ServerListViewModel();
            var s1 = new ServerItemViewModel { JobId = "job-1", Playing = 10, MaxPlayers = 10, Ping = 120, Type = "Public", Region = "US-East" };
            var s2 = new ServerItemViewModel { JobId = "job-2", Playing = 5, MaxPlayers = 10, Ping = 45, Type = "Public", Region = "SG" };
            var s3 = new ServerItemViewModel { JobId = "job-3", Playing = 8, MaxPlayers = 10, Ping = 80, Type = "Public", Region = "JP" };
            var s4 = new ServerItemViewModel { JobId = "VIP:abc", Playing = 1, MaxPlayers = 10, Ping = 30, Type = "VIP", Region = "VIP Private" };

            vm.SetRawServers(new[] { s1, s2, s3, s4 });
            Assert.Equal(4, vm.Servers.Count);

            // PlayersAsc
            vm.SortMode = ServerSortMode.PlayersAsc;
            Assert.Equal(new[] { "VIP:abc", "job-2", "job-3", "job-1" }, vm.Servers.Select(s => s.JobId).ToArray());

            // PlayersDesc
            vm.SortMode = ServerSortMode.PlayersDesc;
            Assert.Equal(new[] { "job-1", "job-3", "job-2", "VIP:abc" }, vm.Servers.Select(s => s.JobId).ToArray());

            // PingAsc
            vm.SortMode = ServerSortMode.PingAsc;
            Assert.Equal(new[] { "VIP:abc", "job-2", "job-3", "job-1" }, vm.Servers.Select(s => s.JobId).ToArray());

            // PingDesc
            vm.SortMode = ServerSortMode.PingDesc;
            Assert.Equal(new[] { "job-1", "job-3", "job-2", "VIP:abc" }, vm.Servers.Select(s => s.JobId).ToArray());

            // HideFullServers (s1 is 10/10) and HideVipServers (s4 is VIP and 1 player)
            vm.HideFullServers = true;
            vm.HideVipServers = true;
            vm.SortMode = ServerSortMode.PingAsc;
            Assert.Equal(new[] { "job-2", "job-3" }, vm.Servers.Select(s => s.JobId).ToArray());
        }
    }
}

