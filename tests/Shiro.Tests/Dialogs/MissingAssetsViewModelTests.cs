#nullable enable
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Dialogs;

public sealed class MissingAssetsViewModelTests
{
    private readonly Account _account = new() { Username = "buyer", UserID = 10 };
    private readonly IAssetCommerce _commerce = Substitute.For<IAssetCommerce>();
    private readonly IUserPrompt _prompt = Substitute.For<IUserPrompt>();

    [Fact]
    public async Task Load_SumsOnlyPositiveCosts_AndPopulatesFreeAndOffSale()
    {
        _commerce.GetAssetDetailsAsync(_account, 1, Arg.Any<CancellationToken>())
            .Returns((true, """{"Name":"Paid","IsForSale":true,"PriceInRobux":50,"ProductId":101,"Creator":{"Id":1}}"""));
        _commerce.GetAssetDetailsAsync(_account, 2, Arg.Any<CancellationToken>())
            .Returns((true, """{"Name":"Free","IsForSale":true,"PriceInRobux":0,"ProductId":102,"Creator":{"Id":2}}"""));
        _commerce.GetAssetDetailsAsync(_account, 3, Arg.Any<CancellationToken>())
            .Returns((true, """{"Name":"Off","IsForSale":false,"PriceInRobux":999,"ProductId":103,"Creator":{"Id":3}}"""));
        _commerce.GetAssetDetailsAsync(_account, 4, Arg.Any<CancellationToken>())
            .Returns((true, "not valid json"));

        var vm = new MissingAssetsViewModel(_account, new long[] { 1, 2, 3, 4 }, _commerce, _prompt);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.Items.Count);
        Assert.Equal(LocalizationService.Format("MissingAssets.TotalRobux", 50L), vm.TotalPriceText);
        Assert.Equal(LocalizationService.Get("MissingAssets.PriceFree"), vm.Items[1].PriceText);
        Assert.True(vm.Items[1].CanBuy);
        Assert.Equal(LocalizationService.Get("MissingAssets.PriceOffSale"), vm.Items[2].PriceText);
        Assert.False(vm.Items[2].CanBuy);
        Assert.Equal(LocalizationService.Get("MissingAssets.PriceLoadError"), vm.Items[3].PriceText);
    }

    [Fact]
    public async Task Buy_Declined_NoPurchase()
    {
        _prompt.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        var vm = new MissingAssetsViewModel(_account, new long[] { 1 }, _commerce, _prompt);
        var item = new MissingAssetDisplayItem
        {
            AssetId = 1,
            Name = "Hat",
            ProductId = 55,
            SellerId = 7,
            PriceInRobux = 25,
            Cost = 25,
            CanBuy = true,
            IsLoaded = true
        };

        await vm.BuyCommand.ExecuteAsync(item);

        await _commerce.DidNotReceive().PurchaseAsync(
            Arg.Any<Account>(),
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        Assert.True(item.CanBuy);
    }

    [Fact]
    public async Task Buy_ProductIdZero_WarnsNoValidProductId()
    {
        var vm = new MissingAssetsViewModel(_account, new long[] { 1 }, _commerce, _prompt);
        var item = new MissingAssetDisplayItem
        {
            AssetId = 1,
            Name = "Hat",
            ProductId = 0,
            CanBuy = true,
            IsLoaded = true
        };

        await vm.BuyCommand.ExecuteAsync(item);

        await _prompt.Received(1).AlertAsync(
            LocalizationService.Get("MissingAssets.NoValidProductId"),
            LocalizationService.Get("Msg_InfoTitle"),
            PromptSeverity.Warning);
        await _commerce.DidNotReceive().PurchaseAsync(
            Arg.Any<Account>(),
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        Assert.Equal(0, item.ProductId);
    }

    [Fact]
    public async Task Buy_Success_SetsPurchasedBadge_AndCanBuyFalse()
    {
        _prompt.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _commerce.PurchaseAsync(_account, 15, 9, "col-1", "prod-1", "User", Arg.Any<CancellationToken>())
            .Returns((true, (string?)null));

        var vm = new MissingAssetsViewModel(_account, new long[] { 1 }, _commerce, _prompt);
        var item = new MissingAssetDisplayItem
        {
            AssetId = 1,
            Name = "Cap",
            ProductId = 77,
            SellerId = 9,
            PriceInRobux = 15,
            Cost = 15,
            CanBuy = true,
            IsLoaded = true,
            CollectibleItemId = "col-1",
            CollectibleProductId = "prod-1",
            SellerType = "User"
        };

        await vm.BuyCommand.ExecuteAsync(item);

        Assert.False(item.CanBuy);
        Assert.Equal(LocalizationService.Get("MissingAssets.PurchasedBadge"), item.BuyButtonText);
    }

    [Fact]
    public async Task Buy_Failure_RestoresBuyActionText()
    {
        _prompt.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _commerce.PurchaseAsync(_account, 15, 9, "col-1", "prod-1", "User", Arg.Any<CancellationToken>())
            .Returns((false, "InsufficientFunds"));

        var vm = new MissingAssetsViewModel(_account, new long[] { 1 }, _commerce, _prompt);
        var item = new MissingAssetDisplayItem
        {
            AssetId = 1,
            Name = "Cap",
            ProductId = 77,
            SellerId = 9,
            PriceInRobux = 15,
            Cost = 15,
            CanBuy = true,
            IsLoaded = true,
            CollectibleItemId = "col-1",
            CollectibleProductId = "prod-1",
            SellerType = "User"
        };

        await vm.BuyCommand.ExecuteAsync(item);

        Assert.True(item.CanBuy);
        Assert.Equal(LocalizationService.Get("MissingAssets.BuyAction"), item.BuyButtonText);
    }

    [Fact]
    public async Task Buy_WhileBusy_IsNoOp()
    {
        _prompt.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var tcs = new TaskCompletionSource<(bool Ok, string? Error)>();
        int calls = 0;
        _commerce.PurchaseAsync(Arg.Any<Account>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref calls);
                return tcs.Task;
            });

        var vm = new MissingAssetsViewModel(_account, new long[] { 1 }, _commerce, _prompt);
        var item = new MissingAssetDisplayItem
        {
            AssetId = 1,
            Name = "Cap",
            ProductId = 77,
            SellerId = 9,
            PriceInRobux = 15,
            Cost = 15,
            CanBuy = true,
            IsLoaded = true,
            CollectibleItemId = "col-1",
            CollectibleProductId = "prod-1",
            SellerType = "User"
        };

        Task first = vm.BuyCommand.ExecuteAsync(item);
        Task second = vm.BuyCommand.ExecuteAsync(item);

        tcs.SetResult((true, null));
        await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Buy_LegacyAsset_PromptsAndOpensBrowserCatalogUrl()
    {
        string? openedUrl = null;
        _prompt.ConfirmAsync(
            LocalizationService.Get("MissingAssets.LegacyCatalogRedirectPrompt"),
            LocalizationService.Get("MissingAssets.ConfirmPurchaseTitle")).Returns(true);

        var vm = new MissingAssetsViewModel(_account, new long[] { 1 }, _commerce, _prompt, url => openedUrl = url);
        var item = new MissingAssetDisplayItem
        {
            AssetId = 42,
            Name = "Classic Shirt",
            ProductId = 99,
            SellerId = 1,
            PriceInRobux = 10,
            Cost = 10,
            CanBuy = true,
            IsLoaded = true,
            CollectibleItemId = null
        };

        await vm.BuyCommand.ExecuteAsync(item);

        Assert.Equal("https://www.roblox.com/catalog/42/-", openedUrl);
        await _commerce.DidNotReceive().PurchaseAsync(
            Arg.Any<Account>(),
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Buy_LegacyAsset_DeclinedConfirmation_DoesNotOpenUrl()
    {
        string? openedUrl = null;
        _prompt.ConfirmAsync(
            LocalizationService.Get("MissingAssets.LegacyCatalogRedirectPrompt"),
            LocalizationService.Get("MissingAssets.ConfirmPurchaseTitle")).Returns(false);

        var vm = new MissingAssetsViewModel(_account, new long[] { 1 }, _commerce, _prompt, url => openedUrl = url);
        var item = new MissingAssetDisplayItem
        {
            AssetId = 42,
            Name = "Classic Shirt",
            ProductId = 99,
            SellerId = 1,
            PriceInRobux = 10,
            Cost = 10,
            CanBuy = true,
            IsLoaded = true,
            CollectibleItemId = null
        };

        await vm.BuyCommand.ExecuteAsync(item);

        Assert.Null(openedUrl);
        await _commerce.DidNotReceive().PurchaseAsync(
            Arg.Any<Account>(),
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Buy_CollectibleAsset_PassesAllCollectibleDetailsToCommerce()
    {
        _prompt.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        _commerce.PurchaseAsync(_account, 15, 9, "col-item-42", "col-prod-42", "Group", Arg.Any<CancellationToken>())
            .Returns((true, (string?)null));

        var vm = new MissingAssetsViewModel(_account, new long[] { 101 }, _commerce, _prompt);
        var item = new MissingAssetDisplayItem
        {
            AssetId = 101,
            Name = "UGC Limited",
            ProductId = 77,
            SellerId = 9,
            PriceInRobux = 15,
            Cost = 15,
            CanBuy = true,
            IsLoaded = true,
            CollectibleItemId = "col-item-42",
            CollectibleProductId = "col-prod-42",
            SellerType = "Group"
        };

        await vm.BuyCommand.ExecuteAsync(item);

        Assert.False(item.CanBuy);
        Assert.Equal(LocalizationService.Get("MissingAssets.PurchasedBadge"), item.BuyButtonText);
        await _commerce.Received(1).PurchaseAsync(_account, 15, 9, "col-item-42", "col-prod-42", "Group", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Close_CancelsInFlightWork()
    {
        CancellationToken capturedToken = default;
        var entered = new TaskCompletionSource();
        var tcs = new TaskCompletionSource<(bool Ok, string Content)>();
        _commerce.GetAssetDetailsAsync(_account, 1, Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedToken = ci.ArgAt<CancellationToken>(2);
                entered.TrySetResult();
                return tcs.Task;
            });

        var vm = new MissingAssetsViewModel(_account, new long[] { 1 }, _commerce, _prompt);
        Task loadTask = vm.LoadCommand.ExecuteAsync(null);
        await entered.Task;

        vm.OnClosing();
        tcs.TrySetCanceled(capturedToken);
        await loadTask;

        Assert.True(capturedToken.IsCancellationRequested);
    }
}
