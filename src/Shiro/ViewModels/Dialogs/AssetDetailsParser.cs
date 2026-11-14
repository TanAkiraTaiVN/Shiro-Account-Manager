#nullable enable
using System.Text.Json;
using Shiro.Services;

namespace Shiro.ViewModels.Dialogs;

public sealed record AssetDetails(string Name, bool IsForSale, long Price, long ProductId, long SellerId, string? CollectibleItemId = null, string? CollectibleProductId = null, string SellerType = "User");

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

        JsonElement target = root;
        if (root.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array && dataArr.GetArrayLength() > 0)
        {
            target = dataArr[0];
        }

        string fallbackName = LocalizationService.Format("MissingAssets.ItemNumber", assetId);
        string? rawName = null;
        if ((target.TryGetProperty("name", out var nameProp) || target.TryGetProperty("Name", out nameProp)) &&
            nameProp.ValueKind == JsonValueKind.String)
        {
            rawName = nameProp.GetString();
        }
        string name = string.IsNullOrWhiteSpace(rawName) ? fallbackName : rawName;

        bool isOffSale = target.TryGetProperty("isOffSale", out var offSaleProp) && offSaleProp.ValueKind == JsonValueKind.True;
        bool hasResellers = target.TryGetProperty("hasResellers", out var resellersProp) && resellersProp.ValueKind == JsonValueKind.True;

        string? priceStatus = target.TryGetProperty("priceStatus", out var psProp) && psProp.ValueKind == JsonValueKind.String
            ? psProp.GetString()
            : null;

        bool? isPurchasable = null;
        if (target.TryGetProperty("isPurchasable", out var purchProp))
        {
            if (purchProp.ValueKind == JsonValueKind.True) isPurchasable = true;
            else if (purchProp.ValueKind == JsonValueKind.False) isPurchasable = false;
        }

        bool hasLegacyIsForSale = target.TryGetProperty("IsForSale", out var legacySaleElem);
        bool legacyIsOffSale = hasLegacyIsForSale && legacySaleElem.ValueKind == JsonValueKind.False;

        long? lowestPrice = null;
        if (target.TryGetProperty("lowestPrice", out var lpElem))
        {
            if (lpElem.ValueKind == JsonValueKind.Number && lpElem.TryGetInt64(out long lpNum)) lowestPrice = lpNum;
            else if (lpElem.ValueKind == JsonValueKind.String && long.TryParse(lpElem.GetString(), out long lpStr)) lowestPrice = lpStr;
        }

        long? price = null;
        if (target.TryGetProperty("price", out var pElem))
        {
            if (pElem.ValueKind == JsonValueKind.Number && pElem.TryGetInt64(out long pNum)) price = pNum;
            else if (pElem.ValueKind == JsonValueKind.String && long.TryParse(pElem.GetString(), out long pStr)) price = pStr;
        }
        else if (target.TryGetProperty("PriceInRobux", out var pirElem))
        {
            if (pirElem.ValueKind == JsonValueKind.Number && pirElem.TryGetInt64(out long pirNum)) price = pirNum;
            else if (pirElem.ValueKind == JsonValueKind.String && long.TryParse(pirElem.GetString(), out long pirStr)) price = pirStr;
        }

        string? lowestResaleProductId = null;
        if (target.TryGetProperty("lowestAvailableResaleProductId", out var larpElem))
        {
            if (larpElem.ValueKind == JsonValueKind.String) lowestResaleProductId = larpElem.GetString();
            else if (larpElem.ValueKind == JsonValueKind.Number && larpElem.TryGetInt64(out long larpNum)) lowestResaleProductId = larpNum.ToString();
        }

        string? collectibleItemId = null;
        if (target.TryGetProperty("collectibleItemId", out var ciElem) && ciElem.ValueKind == JsonValueKind.String)
        {
            collectibleItemId = ciElem.GetString();
        }

        string? collectibleProductId = null;
        if (target.TryGetProperty("collectibleProductId", out var cpElem) && cpElem.ValueKind == JsonValueKind.String)
        {
            collectibleProductId = cpElem.GetString();
        }

        bool isForSale;
        long finalPrice = -1;

        if (isOffSale && hasResellers && lowestPrice.HasValue && lowestPrice.Value > 0)
        {
            isForSale = true;
            finalPrice = lowestPrice.Value;
            if (!string.IsNullOrWhiteSpace(lowestResaleProductId))
            {
                collectibleProductId = lowestResaleProductId;
            }
        }
        else if (isOffSale || legacyIsOffSale || string.Equals(priceStatus, "Off Sale", System.StringComparison.OrdinalIgnoreCase) || (isPurchasable == false && !hasResellers))
        {
            isForSale = false;
        }
        else if (string.Equals(priceStatus, "Free", System.StringComparison.OrdinalIgnoreCase))
        {
            isForSale = true;
            finalPrice = 0;
        }
        else if (price.HasValue && price.Value >= 0)
        {
            isForSale = true;
            finalPrice = price.Value;
        }
        else if (lowestPrice.HasValue && lowestPrice.Value >= 0)
        {
            isForSale = true;
            finalPrice = lowestPrice.Value;
        }
        else
        {
            isForSale = hasLegacyIsForSale && legacySaleElem.ValueKind == JsonValueKind.True;
            if (isForSale && price.HasValue)
            {
                finalPrice = price.Value;
            }
        }

        long productId = 0;
        if (target.TryGetProperty("productId", out var prodElem) || target.TryGetProperty("ProductId", out prodElem))
        {
            if (prodElem.ValueKind == JsonValueKind.Number && prodElem.TryGetInt64(out long pNum)) productId = pNum;
            else if (prodElem.ValueKind == JsonValueKind.String && long.TryParse(prodElem.GetString(), out long pStr)) productId = pStr;
        }

        long sellerId = 0;
        if (target.TryGetProperty("creatorTargetId", out var cElem) ||
            target.TryGetProperty("expectedSellerId", out cElem) ||
            target.TryGetProperty("creatorId", out cElem))
        {
            if (cElem.ValueKind == JsonValueKind.Number && cElem.TryGetInt64(out long cNum)) sellerId = cNum;
            else if (cElem.ValueKind == JsonValueKind.String && long.TryParse(cElem.GetString(), out long cStr)) sellerId = cStr;
        }
        else if (target.TryGetProperty("Creator", out var creatorElem) && creatorElem.ValueKind == JsonValueKind.Object &&
                 creatorElem.TryGetProperty("Id", out var creatorIdElem))
        {
            if (creatorIdElem.ValueKind == JsonValueKind.Number && creatorIdElem.TryGetInt64(out long sNum)) sellerId = sNum;
            else if (creatorIdElem.ValueKind == JsonValueKind.String && long.TryParse(creatorIdElem.GetString(), out long sStr)) sellerId = sStr;
        }

        string sellerType = "User";
        if (target.TryGetProperty("creatorType", out var ctElem))
        {
            if (ctElem.ValueKind == JsonValueKind.String && string.Equals(ctElem.GetString(), "Group", System.StringComparison.OrdinalIgnoreCase))
                sellerType = "Group";
            else if (ctElem.ValueKind == JsonValueKind.Number && ctElem.TryGetInt32(out int ctNum) && ctNum == 2)
                sellerType = "Group";
            else if (ctElem.ValueKind == JsonValueKind.String && ctElem.GetString() == "2")
                sellerType = "Group";
        }

        return new AssetDetails(name, isForSale, finalPrice, productId, sellerId, collectibleItemId, collectibleProductId, sellerType);
    }

    public static long ComputeCost(AssetDetails details) =>
        details.IsForSale ? details.Price : -1;
}
