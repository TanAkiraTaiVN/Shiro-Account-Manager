#nullable enable
using System.Text.Json;
using Shiro.Services;

namespace Shiro.ViewModels.Dialogs;

public sealed record AssetDetails(string Name, bool IsForSale, long Price, long ProductId, long SellerId);

public static class AssetDetailsParser
{
    public static AssetDetails Parse(string json, long assetId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Root element is not a JSON object.");
        }

        string fallbackName = LocalizationService.Format("MissingAssets.ItemNumber", assetId);
        string name = (root.TryGetProperty("Name", out var nameElem) && nameElem.ValueKind == JsonValueKind.String
            ? nameElem.GetString()
            : null) ?? fallbackName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = fallbackName;
        }

        bool isForSale = root.TryGetProperty("IsForSale", out var saleElem) && saleElem.ValueKind == JsonValueKind.True;

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

        return new AssetDetails(name, isForSale, price, productId, sellerId);
    }

    public static long ComputeCost(AssetDetails details) =>
        details.IsForSale ? details.Price : -1;
}
