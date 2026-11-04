#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Shiro.ViewModels;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Dialogs;

public sealed class AltGenViewModelTests
{
    private readonly IBloxGenService _bloxGen = Substitute.For<IBloxGenService>();
    private readonly IToastNotifier _toasts = Substitute.For<IToastNotifier>();

    public AltGenViewModelTests()
    {
        _bloxGen.SavedApiKey.Returns(string.Empty);
        _bloxGen.DefaultContinent.Returns("RANDOM");
        _bloxGen.DefaultGenType.Returns("unverified");
    }

    private AltGenViewModel CreateVm(Action<string>? openFile = null, string? historyPath = null)
        => new(_bloxGen, _toasts, openFile, historyPath);

    [Fact]
    public async Task Generate_EmptyKey_ShowsEnterApiKey_NoCall()
    {
        var vm = CreateVm();
        vm.ApiKey = "   ";

        await vm.GenerateCommand.ExecuteAsync(null);

        Assert.Equal(LocalizationService.Get("AltGen.EnterApiKey"), vm.StatusText);
        Assert.Equal(StatusKind.Warning, vm.StatusKind);
        await _bloxGen.DidNotReceiveWithAnyArgs().GenerateAccountAsync();
    }

    [Fact]
    public async Task Generate_PersistsKeyAndDefaults()
    {
        _bloxGen.GenerateAccountAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new BloxGenAccountResult { Success = false, ErrorMessage = "err" });
        var vm = CreateVm();
        vm.ApiKey = "  k  ";
        vm.SelectedContinent = "EU";
        vm.SelectedGenType = "verified";

        await vm.GenerateCommand.ExecuteAsync(null);

        _bloxGen.Received(1).SavedApiKey = "k";
        _bloxGen.Received(1).DefaultContinent = "EU";
        _bloxGen.Received(1).DefaultGenType = "verified";
        Assert.Equal("k", vm.ApiKey.Trim());
    }

    [Fact]
    public async Task Generate_Success_ImportsWithGroupAndShowsResult()
    {
        var item = new BloxGenAccountItem
        {
            UserId = 0,
            Username = "alt_user",
            Password = "",
            Cookie = "_|WARNING:-DO-NOT-SHARE-THIS._"
        };
        _bloxGen.GenerateAccountAsync("key1", "unverified", "RANDOM", Arg.Any<CancellationToken>())
            .Returns(new BloxGenAccountResult { Success = true, Account = item });
        _bloxGen.ImportAccountToShiro(item, "AltGen").Returns(true);

        var vm = CreateVm();
        vm.ApiKey = "key1";
        vm.GroupName = "   ";

        await vm.GenerateCommand.ExecuteAsync(null);

        Assert.True(vm.HasResult);
        Assert.Equal("alt_user", vm.ResultUsername);
        Assert.Equal(LocalizationService.Get("AltGen.LoadingState"), vm.ResultUserId);
        Assert.Equal(LocalizationService.Get("AltGen.NoneState"), vm.ResultPassword);
        Assert.Equal(LocalizationService.Format("AltGen.CreateSuccessWithGroup", "AltGen"), vm.StatusText);
        Assert.Equal(StatusKind.Success, vm.StatusKind);
        _toasts.Received(1).ShowSuccess(
            LocalizationService.Get("AltGen.CreateSuccessTitle"),
            LocalizationService.Format("AltGen.AccountAdded", "alt_user"));
    }

    [Fact]
    public async Task Generate_ImportFails_ShowsSaveToShiroFailed()
    {
        var item = new BloxGenAccountItem
        {
            UserId = 99,
            Username = "alt99",
            Password = "pw",
            Cookie = "_|WARNING:-DO-NOT-SHARE-THIS._"
        };
        _bloxGen.GenerateAccountAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new BloxGenAccountResult { Success = true, Account = item });
        _bloxGen.ImportAccountToShiro(item, "Bots").Returns(false);

        var vm = CreateVm();
        vm.ApiKey = "key1";
        vm.GroupName = "Bots";

        await vm.GenerateCommand.ExecuteAsync(null);

        Assert.True(vm.HasResult);
        Assert.Equal("99", vm.ResultUserId);
        Assert.Equal("pw", vm.ResultPassword);
        Assert.Equal(LocalizationService.Get("AltGen.SaveToShiroFailed"), vm.StatusText);
        Assert.Equal(StatusKind.Warning, vm.StatusKind);
    }

    [Fact]
    public async Task Generate_ApiFailure_ShowsFailedFormat_AndErrorToast()
    {
        _bloxGen.GenerateAccountAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new BloxGenAccountResult { Success = false, ErrorMessage = "Out of stock" });

        var vm = CreateVm();
        vm.ApiKey = "key1";

        await vm.GenerateCommand.ExecuteAsync(null);

        Assert.False(vm.HasResult);
        Assert.Equal(LocalizationService.Format("AltGen.FailedFormat", "Out of stock"), vm.StatusText);
        Assert.Equal(StatusKind.Error, vm.StatusKind);
        _toasts.Received(1).ShowError(LocalizationService.Get("AltGen.CreateFailedTitle"), "Out of stock");
    }

    [Fact]
    public async Task Generate_WhileBusy_IsNoOp()
    {
        var tcs = new TaskCompletionSource<BloxGenAccountResult>();
        int calls = 0;
        _bloxGen.GenerateAccountAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref calls);
                return tcs.Task;
            });

        var vm = CreateVm();
        vm.ApiKey = "key1";

        Task first = vm.GenerateCommand.ExecuteAsync(null);
        Task second = vm.GenerateCommand.ExecuteAsync(null);

        tcs.SetResult(new BloxGenAccountResult { Success = false, ErrorMessage = "done" });
        await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void OpenHistory_Missing_ShowsNoHistory()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.csv");
        bool opened = false;
        var vm = CreateVm(openFile: _ => opened = true, historyPath: missingPath);

        vm.OpenHistoryCommand.Execute(null);

        Assert.False(opened);
        Assert.Equal(LocalizationService.Get("AltGen.NoHistory"), vm.StatusText);
        Assert.Equal(StatusKind.Warning, vm.StatusKind);
    }
}
