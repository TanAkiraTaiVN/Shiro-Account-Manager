#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Shiro.Classes;
using Shiro.Http;
using Shiro.Services;
using Shiro.ViewModels;

namespace Shiro.AvaloniaUI.Views
{
    public class MissingAssetDisplayItem : ObservableObject
    {
        private string _name = Shiro.Services.LocalizationService.Get("Auto_MissingAssetsDia_123");
        private string _priceText = Shiro.Services.LocalizationService.Get("Auto_MissingAssetsDia_124");
        private string _imageUrl = string.Empty;
        private bool _canBuy;
        private string _buyButtonText = "Mua";

        public long AssetId { get; set; }
        public string AssetIdText => $"ID: {AssetId}";
        public long Cost { get; set; } = -1;
        public long ProductId { get; set; }
        public long SellerId { get; set; }
        public long PriceInRobux { get; set; }
        public bool IsLoaded { get; set; }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public string PriceText
        {
            get => _priceText;
            set => SetProperty(ref _priceText, value);
        }

        public string ImageUrl
        {
            get => _imageUrl;
            set => SetProperty(ref _imageUrl, value);
        }

        public bool CanBuy
        {
            get => _canBuy;
            set => SetProperty(ref _canBuy, value);
        }

        public string BuyButtonText
        {
            get => _buyButtonText;
            set => SetProperty(ref _buyButtonText, value);
        }
    }

    public partial class MissingAssetsDialog : Window
    {
        private readonly Account? _account;
        private readonly long[] _assetIds = Array.Empty<long>();
        private readonly ObservableCollection<MissingAssetDisplayItem> _items = new();
        private CancellationTokenSource? _loadCts;

        public MissingAssetsDialog()
        {
            InitializeComponent();
        }

        public MissingAssetsDialog(Account account, long[] assetIds) : this()
        {
            _account = account ?? throw new ArgumentNullException(nameof(account));
            _assetIds = assetIds ?? Array.Empty<long>();

            TxtTitle.Text = Shiro.Services.LocalizationService.Format("Auto_MissingAssetsDia_125", _account.Username);
            TxtSubtitle.Text = Shiro.Services.LocalizationService.Format("Auto_MissingAssetsDia_126", _assetIds.Length);

            AssetsListBox.ItemsSource = _items;

            Loaded += MissingAssetsDialog_Loaded;
        }

        private async void MissingAssetsDialog_Loaded(object? sender, RoutedEventArgs e)
        {
            if (_account == null)
            {
                TxtStatus.Text = Shiro.Services.LocalizationService.Get("Auto_MissingAssetsDia_127");
                return;
            }

            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            try
            {
                foreach (long id in _assetIds)
                {
                    _items.Add(new MissingAssetDisplayItem { AssetId = id });
                }

                TxtStatus.Text = Shiro.Services.LocalizationService.Format("Auto_MissingAssetsDia_128", _items.Count);

                int loaded = 0;
                long totalCost = 0;
                using var gate = new SemaphoreSlim(4, 4);
                var tasks = new List<Task>(_items.Count);

                foreach (var item in _items)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        await gate.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            if (token.IsCancellationRequested) return;
                            await LoadAssetDetailsAsync(item, token).ConfigureAwait(false);
                            if (token.IsCancellationRequested) return;

                            if (item.Cost > 0)
                                Interlocked.Add(ref totalCost, item.Cost);

                            int currentLoaded = Interlocked.Increment(ref loaded);
                            AppDispatcher.Post(() =>
                            {
                                if (!token.IsCancellationRequested)
                                    TxtStatus.Text = Shiro.Services.LocalizationService.Format("Auto_MissingAssetsDia_129", currentLoaded, _items.Count);
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

                TxtStatus.Text = Shiro.Services.LocalizationService.Format("Auto_MissingAssetsDia_130", _items.Count);
                TxtTotalPrice.Text = Shiro.Services.LocalizationService.Format("Auto_MissingAssetsDia_131", Interlocked.Read(ref totalCost));
            }
            catch (OperationCanceledException) { /* canceled */ }
            catch (Exception ex)
            {
                Program.Logger?.Error($"[MissingAssetsDialog] Error in Loaded: {ex.Message}");
            }
        }

        private async Task LoadAssetDetailsAsync(MissingAssetDisplayItem item, CancellationToken cancellationToken = default)
        {
            if (_account == null || item == null) return;
            try
            {
                var request = _account.MakeRequest($"v2/assets/{item.AssetId}/details");
                var response = await _account.ExecuteAccountRestAsync(AppServices.RobloxApi.Economy, "https://economy.roblox.com", request, cancellationToken).ConfigureAwait(false);
                if (response != null && response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
                {
                    using var doc = JsonDocument.Parse(response.Content);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        string name = (root.TryGetProperty("Name", out var nameElem) && nameElem.ValueKind == JsonValueKind.String
                            ? nameElem.GetString()
                            : Shiro.Services.LocalizationService.Format("Auto_MissingAssetsDia_132", item.AssetId)) ?? Shiro.Services.LocalizationService.Format("Auto_MissingAssetsDia_132", item.AssetId);

                        bool isForSale = root.TryGetProperty("IsForSale", out var saleElem) &&
                                         (saleElem.ValueKind == JsonValueKind.True || (saleElem.ValueKind == JsonValueKind.False && false));

                        long price = -1;
                        if (root.TryGetProperty("PriceInRobux", out var priceElem))
                        {
                            if (priceElem.ValueKind == JsonValueKind.Number && priceElem.TryGetInt64(out long parsedNum))
                                price = parsedNum;
                            else if (priceElem.ValueKind == JsonValueKind.String && long.TryParse(priceElem.GetString(), out long parsedStr))
                                price = parsedStr;
                        }

                        long productId = 0;
                        if (root.TryGetProperty("ProductId", out var prodElem))
                        {
                            if (prodElem.ValueKind == JsonValueKind.Number && prodElem.TryGetInt64(out long pNum))
                                productId = pNum;
                            else if (prodElem.ValueKind == JsonValueKind.String && long.TryParse(prodElem.GetString(), out long pStr))
                                productId = pStr;
                        }

                        long sellerId = 0;
                        if (root.TryGetProperty("Creator", out var creatorElem) && creatorElem.ValueKind == JsonValueKind.Object &&
                            creatorElem.TryGetProperty("Id", out var creatorIdElem))
                        {
                            if (creatorIdElem.ValueKind == JsonValueKind.Number && creatorIdElem.TryGetInt64(out long sNum))
                                sellerId = sNum;
                            else if (creatorIdElem.ValueKind == JsonValueKind.String && long.TryParse(creatorIdElem.GetString(), out long sStr))
                                sellerId = sStr;
                        }

                        long cost = isForSale ? price : -1;
                        item.ProductId = productId;
                        item.SellerId = sellerId;
                        item.PriceInRobux = price > 0 ? price : 0;
                        item.Cost = cost;
                        item.IsLoaded = true;

                        AppDispatcher.Post(() =>
                        {
                            item.Name = name ?? Shiro.Services.LocalizationService.Format("Auto_MissingAssetsDia_132", item.AssetId);
                            if (cost == 0)
                            {
                                item.PriceText = Shiro.Services.LocalizationService.Get("Auto_MissingAssetsDia_133");
                                item.CanBuy = true;
                            }
                            else if (cost > 0)
                            {
                                item.PriceText = Shiro.Services.LocalizationService.Format("Auto_MissingAssetsDia_134", cost);
                                item.CanBuy = true;
                            }
                            else
                            {
                                item.PriceText = Shiro.Services.LocalizationService.Get("Auto_MissingAssetsDia_135");
                                item.CanBuy = false;
                                item.BuyButtonText = Shiro.Services.LocalizationService.Get("Auto_MissingAssetsDia_136");
                            }
                        });
                        return;
                    }
                }

                AppDispatcher.Post(() =>
                {
                    item.Name = Shiro.Services.LocalizationService.Format("Auto_MissingAssetsDia_132", item.AssetId);
                    item.PriceText = Shiro.Services.LocalizationService.Get("Auto_MissingAssetsDia_137");
                    item.CanBuy = false;
                });
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"[MissingAssetsDialog] Load failed for asset {item.AssetId}: {ex.Message}");
                AppDispatcher.Post(() =>
                {
                    item.Name = Shiro.Services.LocalizationService.Format("Auto_MissingAssetsDia_132", item.AssetId);
                    item.PriceText = Shiro.Services.LocalizationService.Get("Auto_MissingAssetsDia_138");
                });
            }
        }

        private void BtnViewOnRoblox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is MissingAssetDisplayItem item)
            {
                try
                {
                    string url = $"https://www.roblox.com/catalog/{item.AssetId}/-";
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Program.Logger?.Debug($"Open catalog URL in browser failed: {ex.Message}");
                }
            }
        }

        private async void BtnBuy_Click(object sender, RoutedEventArgs e)
        {
            if (_account == null)
            {
                await MessageBoxDialog.ShowAsync(Shiro.Services.LocalizationService.Get("Auto_MissingAssetsDia_139"), Shiro.Services.LocalizationService.Get("Status_Error"), MessageBoxButtons.OK, MessageBoxIcon.Error, this);
                return;
            }

            if (sender is Button btn && btn.DataContext is MissingAssetDisplayItem item)
            {
                if (!item.IsLoaded) return;

                long productId = item.ProductId;
                long sellerId = item.SellerId;
                long priceInRobux = item.PriceInRobux;

                if (productId <= 0)
                {
                    await MessageBoxDialog.ShowAsync(Shiro.Services.LocalizationService.Get("Auto_MissingAssetsDia_140"), Shiro.Services.LocalizationService.Get("Msg_InfoTitle"), MessageBoxButtons.OK, MessageBoxIcon.Warning, this);
                    return;
                }

                var confirm = await MessageBoxDialog.ShowAsync(
                    Shiro.Services.LocalizationService.Format("Auto_MissingAssetsDia_141", item.Name, item.PriceText, _account.Username),
                    Shiro.Services.LocalizationService.Get("Auto_MissingAssetsDia_142"), MessageBoxButtons.YesNo, MessageBoxIcon.Question, this);

                if (confirm != MessageBoxResult.Yes) return;

                item.CanBuy = false;
                item.BuyButtonText = Shiro.Services.LocalizationService.Get("Auto_MissingAssetsDia_143");

                try
                {
                    var csrf = await _account.GetCSRFTokenAsync().ConfigureAwait(true);
                    if (!csrf.Success)
                    {
                        await MessageBoxDialog.ShowAsync(Shiro.Services.LocalizationService.Get("Auto_MissingAssetsDia_144"), Shiro.Services.LocalizationService.Get("Status_Error"), MessageBoxButtons.OK, MessageBoxIcon.Error, this);
                        item.CanBuy = true;
                        item.BuyButtonText = "Mua";
                        return;
                    }

                    var buyRequest = _account.MakeRequest($"v1/purchases/products/{productId}", Method.Post);
                    buyRequest.AddHeader("X-CSRF-Token", csrf.Token);
                    buyRequest.AddJsonBody(new
                    {
                        expectedCurrency = 1,
                        expectedPrice = priceInRobux,
                        expectedSellerId = sellerId
                    });

                    var response = await _account.ExecuteAccountRestAsync(AppServices.RobloxApi.Economy, "https://economy.roblox.com", buyRequest).ConfigureAwait(true);
                    if (response != null && response.IsSuccessful)
                    {
                        item.BuyButtonText = Shiro.Services.LocalizationService.Get("Auto_MissingAssetsDia_145");
                        await MessageBoxDialog.ShowAsync(Shiro.Services.LocalizationService.Format("Auto_MissingAssetsDia_146", item.Name), Shiro.Services.LocalizationService.Get("Msg_SuccessTitle"), MessageBoxButtons.OK, MessageBoxIcon.Information, this);
                    }
                    else
                    {
                        item.CanBuy = true;
                        item.BuyButtonText = "Mua";
                        await MessageBoxDialog.ShowAsync(Shiro.Services.LocalizationService.Format("Auto_MissingAssetsDia_147", response?.ErrorMessage ?? response?.Content), Shiro.Services.LocalizationService.Get("Status_Error"), MessageBoxButtons.OK, MessageBoxIcon.Error, this);
                    }
                }
                catch (Exception ex)
                {
                    item.CanBuy = true;
                    item.BuyButtonText = "Mua";
                    await MessageBoxDialog.ShowAsync(Shiro.Services.LocalizationService.Format("Auto_MissingAssetsDia_148", ex.Message), Shiro.Services.LocalizationService.Get("Status_Error"), MessageBoxButtons.OK, MessageBoxIcon.Error, this);
                }
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            try
            {
                _loadCts?.Cancel();
                _loadCts?.Dispose();
                _loadCts = null;
            }
            catch (Exception ex)
            {
                Program.Logger?.Debug($"Cancel load cts threw: {ex.Message}");
            }
            base.OnClosing(e);
        }
    }
}
