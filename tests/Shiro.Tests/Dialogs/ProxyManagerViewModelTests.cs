#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Dialogs
{
    [Collection("StaticSeams")]
    public class ProxyManagerViewModelTests
    {
        private readonly IProxyStore _store = Substitute.For<IProxyStore>();

        public ProxyManagerViewModelTests()
        {
            _store.Load().Returns(new List<ProxyItem>());
            _store.Parse(Arg.Any<string?>()).Returns(ci => ProxyManagerService.ParseProxy(ci.Arg<string?>() ?? string.Empty));
        }

        [Fact]
        public void AddEntries_CountsAddedDuplicatesInvalid()
        {
            _store.Load().Returns(new List<ProxyItem>
            {
                new ProxyItem { Protocol = "http", Host = "9.9.9.9", Port = 8080 }
            });

            var vm = new ProxyManagerViewModel(_store);
            var (added, duplicates, invalid) = vm.AddEntries("1.1.1.1:80, 1.1.1.1:80; 9.9.9.9:8080; bad_entry");

            Assert.Equal(1, added);
            Assert.Equal(2, duplicates);
            Assert.Equal(1, invalid);
            Assert.Equal(2, vm.Proxies.Count);
        }

        [Fact]
        public void AddProxy_AllDuplicates_ShowsProxyAlreadyExists()
        {
            _store.Load().Returns(new List<ProxyItem>
            {
                new ProxyItem { Protocol = "http", Host = "1.1.1.1", Port = 80 }
            });

            var vm = new ProxyManagerViewModel(_store) { NewProxyText = "1.1.1.1:80" };
            vm.AddProxyCommand.Execute(null);

            Assert.Equal(LocalizationService.Get("ProxyManager.ProxyAlreadyExists"), vm.StatusText);
            Assert.Equal(StatusKind.Warning, vm.StatusKind);
        }

        [Fact]
        public void AddProxy_AllInvalid_ShowsInvalidProxyFormat()
        {
            var vm = new ProxyManagerViewModel(_store) { NewProxyText = "not_a_proxy" };
            vm.AddProxyCommand.Execute(null);

            Assert.Equal(LocalizationService.Get("ProxyManager.InvalidProxyFormat"), vm.StatusText);
            Assert.Equal(StatusKind.Error, vm.StatusKind);
        }

        [Fact]
        public void RemoveDead_RemovesOnlyMinusTwo()
        {
            _store.Load().Returns(new List<ProxyItem>
            {
                new ProxyItem { Host = "dead.local", Port = 80, LatencyMs = -2 },
                new ProxyItem { Host = "untested.local", Port = 80, LatencyMs = -1 },
                new ProxyItem { Host = "online.local", Port = 80, LatencyMs = 50 }
            });

            var vm = new ProxyManagerViewModel(_store);
            vm.RemoveDeadCommand.Execute(null);

            Assert.Equal(2, vm.Proxies.Count);
            Assert.DoesNotContain(vm.Proxies, p => p.Host == "dead.local");
            Assert.Equal(LocalizationService.Format("ProxyManager.DeletedFailedProxies", 1), vm.StatusText);
        }

        [Fact]
        public async Task TestAll_Empty_ShowsNoProxiesToCheck_AndDoesNotCallStore()
        {
            var vm = new ProxyManagerViewModel(_store);
            await vm.TestAllAsync();

            Assert.Equal(LocalizationService.Get("ProxyManager.NoProxiesToCheck"), vm.StatusText);
            await _store.DidNotReceiveWithAnyArgs().TestAllAsync(default!, default);
        }

        [Fact]
        public async Task TestAll_Summary_CountsOnlineAndDead()
        {
            var p1 = new ProxyItem { Host = "1.1.1.1", Port = 80, LatencyMs = 45 };
            var p2 = new ProxyItem { Host = "2.2.2.2", Port = 80, LatencyMs = -2 };
            _store.Load().Returns(new List<ProxyItem> { p1, p2 });
            _store.TestAllAsync(Arg.Any<IEnumerable<ProxyItem>>(), Arg.Any<CancellationToken>())
                .Returns(Task.CompletedTask);

            var vm = new ProxyManagerViewModel(_store);
            await vm.TestAllAsync();

            Assert.Equal(LocalizationService.Format("ProxyManager.CheckSummaryResult", 1, 2, 1), vm.StatusText);
        }

        [Fact]
        public async Task TestAll_WhileBusy_IsNoOp()
        {
            var tcs = new TaskCompletionSource();
            int calls = 0;
            _store.Load().Returns(new List<ProxyItem> { new ProxyItem { Host = "1.1.1.1", Port = 80 } });
            _store.TestAllAsync(Arg.Any<IEnumerable<ProxyItem>>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    Interlocked.Increment(ref calls);
                    return tcs.Task;
                });

            var vm = new ProxyManagerViewModel(_store);
            Task first = vm.TestAllAsync();
            vm.TestAllCommand.Execute(null);
            await vm.TestAllAsync();

            Assert.Equal(1, Volatile.Read(ref calls));
            tcs.SetResult();
            await first;
        }

        [Fact]
        public async Task Close_CancelsInFlightWork()
        {
            CancellationToken capturedToken = default;
            var tcs = new TaskCompletionSource();
            _store.Load().Returns(new List<ProxyItem> { new ProxyItem { Host = "1.1.1.1", Port = 80 } });
            _store.TestAllAsync(Arg.Any<IEnumerable<ProxyItem>>(), Arg.Any<CancellationToken>())
                .Returns(async ci =>
                {
                    capturedToken = ci.Arg<CancellationToken>();
                    using var reg = capturedToken.Register(() => tcs.TrySetCanceled(capturedToken));
                    await tcs.Task;
                });

            var vm = new ProxyManagerViewModel(_store);
            Task run = vm.TestAllAsync();
            vm.OnClosing();
            await run;

            Assert.True(capturedToken.IsCancellationRequested);
        }

        [Fact]
        public void Save_PersistsAndRequestsCloseTrue()
        {
            var vm = new ProxyManagerViewModel(_store);
            bool? closedWith = null;
            vm.CloseRequested += (_, r) => closedWith = r;

            vm.SaveCommand.Execute(null);

            _store.Received(1).Save(vm.Proxies);
            Assert.Equal(true, closedWith);
        }
    }
}
