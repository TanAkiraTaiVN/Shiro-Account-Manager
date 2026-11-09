#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Nexus;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Dialogs;

public sealed class NexusViewModelTests
{
    private readonly IAccountRepository _repo = Substitute.For<IAccountRepository>();
    private readonly ISettingsService _accountControl = Substitute.For<ISettingsService>();

    public NexusViewModelTests()
    {
        _repo.GetAll().Returns(Array.Empty<Account>());
    }

    [Theory]
    [InlineData("5242", true, 5242)]
    [InlineData(" 65535 ", true, 65535)]
    [InlineData("1", true, 1)]
    [InlineData("0", false, 0)]
    [InlineData("65536", false, 0)]
    [InlineData("-5", false, 0)]
    [InlineData("abc", false, 0)]
    [InlineData(null, false, 0)]
    [InlineData("   ", false, 0)]
    public void TryParsePort_ValidatesRange1To65535(string? text, bool expectedOk, int expectedPort)
    {
        bool ok = NexusViewModel.TryParsePort(text, out int port);
        Assert.Equal(expectedOk, ok);
        if (expectedOk)
            Assert.Equal(expectedPort, port);
    }

    [Fact]
    public async Task Start_InvalidPort_ShowsInvalidPort_NoServer()
    {
        bool factoryCalled = false;
        var vm = new NexusViewModel(_repo, _accountControl, (ip, p) =>
        {
            factoryCalled = true;
            return new NexusWebSocketServer(ip, p);
        })
        {
            PortText = "99999"
        };

        await vm.StartServerAsync();

        Assert.False(factoryCalled);
        Assert.False(vm.IsServerRunning);
        Assert.Equal(LocalizationService.Get("Nexus.InvalidPort"), vm.StatusText);
        Assert.Equal(StatusKind.Warning, vm.StatusKind);
    }

    [Fact]
    public async Task Start_PersistsPortAndAllowExternal()
    {
        var server = new NexusWebSocketServer(IPAddress.Loopback, 6000);
        try
        {
            var vm = new NexusViewModel(_repo, _accountControl, (_, _) => server)
            {
                PortText = "6000",
                AllowExternal = false
            };

            await vm.StartServerAsync();

            Assert.True(vm.IsServerRunning);
            _accountControl.Received(1).Set("NexusPort", "6000");
            _accountControl.Received(1).Set("AllowExternalConnections", "false");
            await vm.StopServerAsync();
            Assert.False(vm.IsServerRunning);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public void ExecuteSelected_EmptyScript_ShowsEnterScriptPrompt()
    {
        var sent = new List<(string User, string Msg)>();
        var vm = new NexusViewModel(_repo, _accountControl, sendTo: (ca, msg) => sent.Add((ca.Username, msg)))
        {
            ScriptInput = "   "
        };

        vm.ExecuteSelectedCommand.Execute(null);

        Assert.Empty(sent);
        Assert.Equal(LocalizationService.Get("Nexus.EnterScriptPrompt"), vm.StatusText);
        Assert.Equal(StatusKind.Warning, vm.StatusKind);
    }

    [Fact]
    public void ExecuteSelected_SendsOnlyToChecked()
    {
        var sent = new List<(string User, string Msg)>();
        var vm = new NexusViewModel(_repo, _accountControl, sendTo: (ca, msg) => sent.Add((ca.Username, msg)))
        {
            ScriptInput = "print(1)"
        };
        vm.Accounts.Add(new ControlledAccount { Username = "a", IsChecked = true });
        vm.Accounts.Add(new ControlledAccount { Username = "b", IsChecked = false });

        vm.ExecuteSelectedCommand.Execute(null);

        Assert.Single(sent);
        Assert.Equal(("a", "execute print(1)"), sent[0]);
        Assert.Equal(LocalizationService.Get("Nexus.ScriptSentSelected"), vm.StatusText);
    }

    [Fact]
    public void LoadAccounts_MergesSavedConfigWithRepository()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"nexus-test-{Guid.NewGuid():N}.json");
        try
        {
            var saved = new List<ControlledAccount>
            {
                new() { Username = "Alice", AutoExecute = "print('hi')", PlaceId = 12345 }
            };
            File.WriteAllText(tempPath, Utilities.FastJson.Serialize(saved, indented: true));

            var accAlice = new Account { Username = "alice", UserID = 1 };
            var accBob = new Account { Username = "bob", UserID = 2 };
            _repo.GetAll().Returns(new[] { accAlice, accBob });

            var vm = new NexusViewModel(_repo, _accountControl);
            vm.LoadAccounts(tempPath);

            Assert.Equal(2, vm.Accounts.Count);
            Assert.Same(accAlice, vm.Accounts[0].LinkedAccount);
            Assert.Equal("print('hi')", vm.Accounts[0].AutoExecute);
            Assert.Equal(12345, vm.Accounts[0].PlaceId);
            Assert.Same(accBob, vm.Accounts[1].LinkedAccount);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAutoExecuteAndPlaceId()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), $"nexus-rt-{Guid.NewGuid():N}.json");
        try
        {
            var acc = new Account { Username = "charlie", UserID = 7 };
            _repo.GetAll().Returns(new[] { acc });

            var vm1 = new NexusViewModel(_repo, _accountControl);
            vm1.Accounts.Add(new ControlledAccount(acc)
            {
                AutoExecute = "loadstring('x')()",
                PlaceId = 999
            });
            vm1.SaveAccounts(tempPath);

            var vm2 = new NexusViewModel(_repo, _accountControl);
            vm2.LoadAccounts(tempPath);

            Assert.Single(vm2.Accounts);
            Assert.Equal("loadstring('x')()", vm2.Accounts[0].AutoExecute);
            Assert.Equal(999, vm2.Accounts[0].PlaceId);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [Fact]
    public async Task ToggleServer_WhileBusy_IsNoOp()
    {
        var tcs = new TaskCompletionSource();
        int starts = 0;
        var vm = new NexusViewModel(
            _repo,
            _accountControl,
            serverStarter: async _ =>
            {
                Interlocked.Increment(ref starts);
                await tcs.Task;
            });

        Task first = vm.ToggleServerCommand.ExecuteAsync(null);
        Task second = vm.ToggleServerCommand.ExecuteAsync(null);

        tcs.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, starts);
    }
}
