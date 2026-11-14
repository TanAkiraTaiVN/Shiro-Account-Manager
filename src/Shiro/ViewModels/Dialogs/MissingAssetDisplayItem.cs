#nullable enable
using Shiro.Services;

namespace Shiro.ViewModels.Dialogs;

public class MissingAssetDisplayItem : ObservableObject
{
    private string _name = LocalizationService.Get("MissingAssets.LoadingText");
    private string _priceText = LocalizationService.Get("MissingAssets.PriceChecking");
    private string _imageUrl = string.Empty;
    private bool _canBuy;
    private string _buyButtonText = LocalizationService.Get("MissingAssets.BuyAction");

    public long AssetId { get; set; }
    public string AssetIdText => $"ID: {AssetId}";
    public long Cost { get; set; } = -1;
    public long ProductId { get; set; }
    public long SellerId { get; set; }
    public string? CollectibleItemId { get; set; }
    public string? CollectibleProductId { get; set; }
    public string SellerType { get; set; } = "User";
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
