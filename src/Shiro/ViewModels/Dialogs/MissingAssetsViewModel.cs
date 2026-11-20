#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Services;

namespace Shiro.ViewModels.Dialogs;

public sealed class MissingAssetsViewModel : DialogViewModelBase
{
    private readonly Account? _account;
    private readonly long[] _assetIds;
    private readonly IAssetCommerce _commerce;
    private readonly IUserPrompt _prompt;
    private readonly Action<string> _openUrl;
    private readonly int _concurrency;
    private readonly IAppLogger _logger;

    private string _title = LocalizationService.Get("MissingAssets.DefaultTitle");
    private string _subtitle = LocalizationService.Get("MissingAssets.DefaultSubtitle");
    private string _totalPriceText = LocalizationService.Format("MissingAssets.TotalRobux", 0);
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _purchaseCts;
    private readonly Action<bool> _onStreamerModeChanged;

    public MissingAssetsViewModel(
        Account? account = null,
        long[]? assetIds = null,
        IAssetCommerce? commerce = null,
        IUserPrompt? prompt = null,
        Action<string>? openUrl = null,
        int concurrency = 4,
        IAppLogger? logger = null)
    {
        _account = account;
        _assetIds = assetIds ?? Array.Empty<long>();
        _commerce = commerce ?? AssetCommerceService.Default;
        _prompt = prompt ?? AppServices.Prompt;
        _openUrl = openUrl ?? Utilities.OpenUrl;
        _concurrency = Math.Max(1, concurrency);
        _logger = logger ?? AppServices.Logger;

        _onStreamerModeChanged = _ => RefreshTitle();
        StreamerModeService.StateChanged += _onStreamerModeChanged;

        if (_account != null)
        {
            _title = LocalizationService.Format("MissingAssets.TitleFormat", StreamerModeService.GetDisplayUsername(_account.Username));
            _subtitle = LocalizationService.Format("MissingAssets.UnownedAssetsDetected", _assetIds.Length);
        }

        LoadCommand = TrackCommand(new AsyncRelayCommand(LoadAsync, () => !IsBusy));
        BuyCommand = TrackCommand(new AsyncRelayCommand<MissingAssetDisplayItem?>(BuyAsync, _ => !IsBusy));
        ViewOnRobloxCommand = new RelayCommand<MissingAssetDisplayItem?>(ViewOnRoblox);
        CloseCommand = new RelayCommand(() => RequestClose(null));
    }

    public ObservableCollection<MissingAssetDisplayItem> Items { get; } = new();

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    public string TotalPriceText
    {
        get => _totalPriceText;
        private set => SetProperty(ref _totalPriceText, value);
    }

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand BuyCommand { get; }
    public ICommand ViewOnRobloxCommand { get; }
    public ICommand CloseCommand { get; }

    public override void OnClosing()
    {
        StreamerModeService.StateChanged -= _onStreamerModeChanged;
        CancelSlot(ref _loadCts);
        CancelSlot(ref _purchaseCts);
        base.OnClosing();
    }

    private void RefreshTitle()
    {
        if (_account == null) return;
        AppDispatcher.Post(() =>
            Title = LocalizationService.Format("MissingAssets.TitleFormat", StreamerModeService.GetDisplayUsername(_account.Username)));
    }

    private CancellationToken BeginLoadOperation() => ReplaceOperationCts(ref _loadCts);

    private CancellationToken BeginPurchaseOperation() => ReplaceOperationCts(ref _purchaseCts);

    private static CancellationToken ReplaceOperationCts(ref CancellationTokenSource? slot)
    {
        var next = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref slot, next);
        CancelAndDisposeCts(prev);
        return next.Token;
    }

    private static void CancelSlot(ref CancellationTokenSource? slot)
    {
        var prev = Interlocked.Exchange(ref slot, null);
        CancelAndDisposeCts(prev);
    }

    private static void CancelAndDisposeCts(CancellationTokenSource? cts)
    {
        if (cts != null)
        {
            SafeExecuteHelper.Try(() => cts.Cancel());
            SafeExecuteHelper.Try(() => cts.Dispose());
        }
    }

    private async Task LoadAsync()
    {
        if (_account == null)
        {
            SetStatus(LocalizationService.Get("MissingAssets.AccountNotFound"), StatusKind.Warning);
            return;
        }

        CancellationToken token = BeginLoadOperation();
        try
        {
            Items.Clear();
            foreach (long id in _assetIds)
            {
                Items.Add(new MissingAssetDisplayItem { AssetId = id });
            }

            SetStatus(LocalizationService.Format("MissingAssets.LoadingAssetInfoCount", Items.Count), StatusKind.Info);

            int loaded = 0;
            long totalCost = 0;
            using var gate = new SemaphoreSlim(_concurrency, _concurrency);
            var tasks = new List<Task>(Items.Count);

            foreach (var item in Items)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (token.IsCancellationRequested) return;
                        await LoadSingleAssetAsync(item, token).ConfigureAwait(false);
                        if (token.IsCancellationRequested) return;

                        if (item.Cost > 0)
                            Interlocked.Add(ref totalCost, item.Cost);

                        int currentLoaded = Interlocked.Increment(ref loaded);
                        AppDispatcher.Post(() =>
                        {
                            if (!token.IsCancellationRequested)
                            {
                                SetStatus(LocalizationService.Format("MissingAssets.LoadingProgress", currentLoaded, Items.Count), StatusKind.Info);
                            }
                        });
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, token));
            }

            await Task.WhenAll(tasks).ConfigureAwait(true);
            if (token.IsCancellationRequested) return;

            SetStatus(LocalizationService.Format("MissingAssets.LoadingCompleted", Items.Count), StatusKind.Success);
            TotalPriceText = LocalizationService.Format("MissingAssets.TotalRobux", Interlocked.Read(ref totalCost));
        }
        catch (OperationCanceledException)
        {
            // Cancelled on close
        }
        catch (Exception ex)
        {
            _logger.Error($"[MissingAssetsViewModel] Error in LoadAsync: {ex.Message}");
        }
    }

    private async Task LoadSingleAssetAsync(MissingAssetDisplayItem item, CancellationToken ct)
    {
        if (_account == null) return;

        try
        {
            var (ok, content) = await _commerce.GetAssetDetailsAsync(_account, item.AssetId, ct).ConfigureAwait(false);
            if (!ok || string.IsNullOrWhiteSpace(content))
            {
                AppDispatcher.Post(() =>
                {
                    item.Name = LocalizationService.Format("MissingAssets.ItemNumber", item.AssetId);
                    item.PriceText = LocalizationService.Get("MissingAssets.PriceUnavailable");
                    item.CanBuy = false;
                });
                return;
            }

            AssetDetails details = AssetDetailsParser.Parse(content, item.AssetId);
            long cost = AssetDetailsParser.ComputeCost(details);
            item.ProductId = details.ProductId;
            item.SellerId = details.SellerId;
            item.CollectibleItemId = details.CollectibleItemId;
            item.CollectibleProductId = details.CollectibleProductId;
            item.SellerType = details.SellerType;
            item.PriceInRobux = details.Price > 0 ? details.Price : 0;
            item.Cost = cost;
            item.IsLoaded = true;

            AppDispatcher.Post(() =>
            {
                item.Name = details.Name;
                if (cost == 0)
                {
                    item.PriceText = LocalizationService.Get("MissingAssets.PriceFree");
                    item.CanBuy = true;
                }
                else if (cost > 0)
                {
                    item.PriceText = LocalizationService.Format("MissingAssets.PriceRobux", cost);
                    item.CanBuy = true;
                }
                else
                {
                    item.PriceText = LocalizationService.Get("MissingAssets.PriceOffSale");
                    item.CanBuy = false;
                    item.BuyButtonText = LocalizationService.Get("MissingAssets.PriceOutOfStock");
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Cancelled
        }
        catch (Exception ex)
        {
            _logger.Debug($"[MissingAssetsViewModel] Load failed for asset {item.AssetId}: {ex.Message}");
            AppDispatcher.Post(() =>
            {
                item.Name = LocalizationService.Format("MissingAssets.ItemNumber", item.AssetId);
                item.PriceText = LocalizationService.Get("MissingAssets.PriceLoadError");
                item.CanBuy = false;
            });
        }
    }

    private async Task BuyAsync(MissingAssetDisplayItem? item)
    {
        if (IsBusy) return;

        if (_account == null)
        {
            await _prompt.AlertAsync(
                LocalizationService.Get("MissingAssets.NoValidAccountInfo"),
                LocalizationService.Get("Status_Error"),
                PromptSeverity.Error).ConfigureAwait(true);
            return;
        }

        if (item == null || !item.IsLoaded)
            return;

        if (string.IsNullOrWhiteSpace(item.CollectibleItemId))
        {
            if (item.ProductId <= 0)
            {
                await _prompt.AlertAsync(
                    LocalizationService.Get("MissingAssets.NoValidProductId"),
                    LocalizationService.Get("Msg_InfoTitle"),
                    PromptSeverity.Warning).ConfigureAwait(true);
                return;
            }

            bool confirmedRedirect = await _prompt.ConfirmAsync(
                LocalizationService.Get("MissingAssets.LegacyCatalogRedirectPrompt"),
                LocalizationService.Get("MissingAssets.ConfirmPurchaseTitle")).ConfigureAwait(true);

            if (confirmedRedirect)
            {
                _openUrl($"https://www.roblox.com/catalog/{item.AssetId}/-");
            }
            return;
        }

        bool confirmed = await _prompt.ConfirmAsync(
            LocalizationService.Format("MissingAssets.ConfirmPurchasePrompt", item.Name, item.PriceText, StreamerModeService.GetDisplayUsername(_account.Username)),
            LocalizationService.Get("MissingAssets.ConfirmPurchaseTitle")).ConfigureAwait(true);

        if (!confirmed)
            return;

        IsBusy = true;
        CancellationToken ct = BeginPurchaseOperation();
        item.CanBuy = false;
        item.BuyButtonText = LocalizationService.Get("MissingAssets.PurchasingText");

        try
        {
            var (ok, error) = await _commerce.PurchaseAsync(
                _account,
                item.AssetId,
                item.ProductId,
                item.PriceInRobux,
                item.SellerId,
                item.CollectibleItemId,
                item.CollectibleProductId,
                item.SellerType,
                ct).ConfigureAwait(true);
            if (ok)
            {
                item.BuyButtonText = LocalizationService.Get("MissingAssets.PurchasedBadge");
                await _prompt.AlertAsync(
                    LocalizationService.Format("MissingAssets.PurchaseSuccess", item.Name),
                    LocalizationService.Get("Msg_SuccessTitle"),
                    PromptSeverity.Info).ConfigureAwait(true);
            }
            else
            {
                item.CanBuy = true;
                item.BuyButtonText = LocalizationService.Get("MissingAssets.BuyAction");
                string msg = string.Equals(error, LocalizationService.Get("MissingAssets.CannotGetCsrfToken"), StringComparison.Ordinal)
                    ? error!
                    : LocalizationService.Format("MissingAssets.PurchaseFailed", error);
                await _prompt.AlertAsync(msg, LocalizationService.Get("Status_Error"), PromptSeverity.Error).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            item.CanBuy = true;
            item.BuyButtonText = LocalizationService.Get("MissingAssets.BuyAction");
        }
        catch (Exception ex)
        {
            item.CanBuy = true;
            item.BuyButtonText = LocalizationService.Get("MissingAssets.BuyAction");
            await _prompt.AlertAsync(
                LocalizationService.Format("MissingAssets.TransactionError", ex.Message),
                LocalizationService.Get("Status_Error"),
                PromptSeverity.Error).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ViewOnRoblox(MissingAssetDisplayItem? item)
    {
        if (item == null) return;
        try
        {
            _openUrl($"https://www.roblox.com/catalog/{item.AssetId}/-");
        }
        catch (Exception ex)
        {
            _logger.Debug($"Open catalog URL in browser failed: {ex.Message}");
        }
    }
}
