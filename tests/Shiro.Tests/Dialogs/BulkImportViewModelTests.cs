#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Http;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Dialogs
{
    [Collection("StaticSeams")]
    public class BulkImportViewModelTests
    {
        private static readonly string ValidCookie =
            "_|WARNING:-DO-NOT-SHARE-THIS.--Sharing-this-will-allow-someone-to-log-in-as-you-and-to-steal-your-ROBUX-and-items.|_0123456789ABCDEF0123456789ABCDEF";

        private readonly IAccountRepository _repo = Substitute.For<IAccountRepository>();
        private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
        private readonly IHostUiBridgeService _host = Substitute.For<IHostUiBridgeService>();

        public BulkImportViewModelTests()
        {
            _repo.GetAll().Returns(Array.Empty<Account>());
        }

        [Fact]
        public async Task StartImport_EmptyInput_SetsPasteAccountListPrompt_Warning()
        {
            var vm = new BulkImportViewModel(_repo, _dialogs, _host) { InputText = "   " };

            await vm.StartImportAsync();

            Assert.Equal(LocalizationService.Get("BulkImport.PasteAccountListPrompt"), vm.StatusText);
            Assert.Equal(StatusKind.Warning, vm.StatusKind);
            _repo.DidNotReceiveWithAnyArgs().GetAll();
        }

        [Fact]
        public async Task StartImport_UserPassNew_LooksUpIdAndAdds()
        {
            _repo.LookupUserIdAsync("alice", Arg.Any<CancellationToken>())
                .Returns((true, 42L, (RestResponse?)null));

            Account? added = null;
            _repo.When(r => r.AddThreadSafe(Arg.Any<Account>()))
                .Do(ci => added = ci.Arg<Account>());

            var vm = new BulkImportViewModel(_repo, _dialogs, _host) { InputText = "alice:secret123" };

            await vm.StartImportAsync();

            Assert.NotNull(added);
            Assert.Equal("alice", added!.Username);
            Assert.Equal("secret123", added.Password);
            Assert.Equal(42L, added.UserID);
            Assert.Equal("Default", added.Group);
            Assert.False(added.Valid);
            _repo.Received(1).Save(true);
            Assert.Equal(1, vm.ImportedCount);
            _dialogs.Received(1).OpenFastLoginDialog(added);
        }

        [Fact]
        public async Task StartImport_ExistingUser_UpdatesPasswordOnly()
        {
            var existing = new Account { Username = "alice", Password = "old", Valid = true, SecurityToken = ValidCookie };
            _repo.GetAll().Returns(new[] { existing });

            var vm = new BulkImportViewModel(_repo, _dialogs, _host) { InputText = "ALICE:newpass" };

            await vm.StartImportAsync();

            _repo.DidNotReceiveWithAnyArgs().AddThreadSafe(default!);
            Assert.Equal("newpass", existing.Password);
            Assert.Equal(1, vm.ImportedCount);
        }

        [Fact]
        public async Task StartImport_UnauthenticatedAccounts_OpenFastLoginBatch()
        {
            _repo.LookupUserIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns((true, 100L, (RestResponse?)null));

            var vm = new BulkImportViewModel(_repo, _dialogs, _host) { InputText = "u1:p1\nu2:p2" };

            await vm.StartImportAsync();

            Assert.Equal(2, vm.ImportedCount);
            _dialogs.Received(1).OpenFastLoginDialogBatch(Arg.Is<IReadOnlyList<Account>>(l => l.Count == 2));
        }

        [Fact]
        public async Task StartImport_Cookies_RespectsConcurrencyLimit()
        {
            int active = 0;
            int maxObserved = 0;
            var gate = new TaskCompletionSource();

            _repo.AddOrUpdateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), false, Arg.Any<CancellationToken>())
                .Returns<Task<Account?>>(async _ =>
                {
                    int cur = Interlocked.Increment(ref active);
                    int prev;
                    do
                    {
                        prev = Volatile.Read(ref maxObserved);
                        if (cur <= prev) break;
                    } while (Interlocked.CompareExchange(ref maxObserved, cur, prev) != prev);

                    await Task.Yield();
                    await gate.Task;
                    Interlocked.Decrement(ref active);
                    return new Account { Username = "u", Valid = true };
                });

            var lines = new List<string>();
            for (int i = 0; i < 10; i++)
                lines.Add($"{ValidCookie}{i:D2}");

            var vm = new BulkImportViewModel(_repo, _dialogs, _host, cookieConcurrency: 4)
            {
                InputText = string.Join("\n", lines)
            };

            Task importTask = vm.StartImportAsync();
            await Task.Delay(50);
            Assert.Equal(4, Volatile.Read(ref maxObserved));

            gate.SetResult();
            await importTask;

            Assert.Equal(4, maxObserved);
            Assert.Equal(10, vm.ImportedCount);
        }

        [Fact]
        public async Task StartImport_WhileBusy_IsNoOp()
        {
            var tcs = new TaskCompletionSource<Account?>();
            int callCount = 0;
            _repo.AddOrUpdateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), false, Arg.Any<CancellationToken>())
                .Returns<Task<Account?>>(_ =>
                {
                    Interlocked.Increment(ref callCount);
                    return tcs.Task;
                });

            var vm = new BulkImportViewModel(_repo, _dialogs, _host) { InputText = ValidCookie };

            Task first = vm.StartImportAsync();
            vm.StartImportCommand.Execute(null);
            await vm.StartImportAsync();

            Assert.Equal(1, Volatile.Read(ref callCount));
            tcs.SetResult(new Account { Username = "u", Valid = true });
            await first;
        }

        [Fact]
        public async Task Close_CancelsInFlightWork()
        {
            var tcs = new TaskCompletionSource<Account?>();
            _repo.AddOrUpdateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), false, Arg.Any<CancellationToken>())
                .Returns<Task<Account?>>(async ci =>
                {
                    var ct = ci.Arg<CancellationToken>();
                    using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
                    return await tcs.Task;
                });

            var vm = new BulkImportViewModel(_repo, _dialogs, _host) { InputText = ValidCookie };
            Task importTask = vm.StartImportAsync();
            vm.OnClosing();
            await importTask;

            Assert.Equal(LocalizationService.Format("BulkImport.StoppedWithSuccessCount", 0), vm.StatusText);
            Assert.Equal(StatusKind.Warning, vm.StatusKind);
            _dialogs.DidNotReceiveWithAnyArgs().OpenFastLoginDialog(default);
            _dialogs.DidNotReceiveWithAnyArgs().OpenFastLoginDialogBatch(default!);
        }

        [Fact]
        public async Task Summary_SuccessAndFailed_FormatsSummaryResult()
        {
            _repo.AddOrUpdateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), false, Arg.Any<CancellationToken>())
                .Returns(new Account { Username = "ok", Valid = true });

            var vm = new BulkImportViewModel(_repo, _dialogs, _host)
            {
                InputText = $"{ValidCookie}\ninvalid_line_without_separator"
            };

            await vm.StartImportAsync();

            Assert.Equal(LocalizationService.Format("BulkImport.SummaryResult", 1, 1), vm.StatusText);
            Assert.Equal(StatusKind.Success, vm.StatusKind);
        }
    }
}
