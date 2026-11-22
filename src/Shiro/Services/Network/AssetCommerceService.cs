#nullable enable
using System;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;

namespace Shiro.Services;

public interface IAssetCommerce
{
    Task<(bool Ok, string Content)> GetAssetDetailsAsync(Account a, long assetId, CancellationToken ct);
    Task<(bool Ok, string? Error)> PurchaseAsync(Account a, long expectedPrice, long sellerId, string? collectibleItemId, string? collectibleProductId, string sellerType = "User", CancellationToken ct = default);
}

public sealed class AssetCommerceService : IAssetCommerce
{
    public static AssetCommerceService Default { get; } = new();

    public async Task<(bool Ok, string Content)> GetAssetDetailsAsync(Account a, long assetId, CancellationToken ct)
    {
        var payload = new CatalogItemDetailsBatchRequest(new[]
        {
            new CatalogItemDetailsItemPayload("Asset", assetId)
        });
        string json = JsonSerializer.Serialize(payload, ShiroAppJson.Default.CatalogItemDetailsBatchRequest);

        var response = await RobloxCsrfRetryHelper.ExecuteWithCsrfRetryAsync(
            a,
            AppServices.RobloxApi.Catalog,
            "https://catalog.roblox.com/",
            a.CSRFToken,
            csrf =>
            {
                var req = AppServices.AccountApiService.MakeRequest(a, "v1/catalog/items/details", Method.Post);
                if (!string.IsNullOrWhiteSpace(csrf))
                    req.AddHeader("X-CSRF-TOKEN", csrf);
                req.AddStringBody(json, "application/json");
                return req;
            },
            ct).ConfigureAwait(false);

        if (response != null && response.IsSuccessful && !string.IsNullOrWhiteSpace(response.Content))
        {
            return (true, response.Content);
        }

        return (false, string.Empty);
    }

    public async Task<(bool Ok, string? Error)> PurchaseAsync(
        Account a,
        long expectedPrice,
        long sellerId,
        string? collectibleItemId,
        string? collectibleProductId,
        string sellerType = "User",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(a?.SecurityToken) || string.IsNullOrWhiteSpace(collectibleItemId))
        {
            return (false, LocalizationService.Get("MissingAssets.LegacyPurchaseUnsupported"));
        }

        var csrf = await a.GetCSRFTokenAsync(ct).ConfigureAwait(false);
        if (!csrf.Success)
        {
            return (false, LocalizationService.Get("MissingAssets.CannotGetCsrfToken"));
        }

        string currentCsrf = csrf.Token;

        if (string.IsNullOrWhiteSpace(collectibleProductId))
        {
            var resolvePayload = new MarketplaceItemsDetailsRequest(new[] { collectibleItemId });
            string resolveJson = JsonSerializer.Serialize(resolvePayload, ShiroAppJson.Default.MarketplaceItemsDetailsRequest);
            var resolveResp = await RobloxCsrfRetryHelper.ExecuteWithCsrfRetryAsync(
                a,
                AppServices.RobloxApi.Api,
                "https://apis.roblox.com/",
                currentCsrf,
                csrf =>
                {
                    var req = AppServices.AccountApiService.MakeRequest(a, "marketplace-items/v1/items/details", Method.Post);
                    req.AddHeader("X-CSRF-TOKEN", csrf);
                    req.AddStringBody(resolveJson, "application/json");
                    return req;
                },
                ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(a.CSRFToken))
                currentCsrf = a.CSRFToken;

            if (resolveResp != null && resolveResp.IsSuccessful && !string.IsNullOrWhiteSpace(resolveResp.Content))
            {
                try
                {
                    using var doc = JsonDocument.Parse(resolveResp.Content);
                    JsonElement target = doc.RootElement;
                    if (target.ValueKind == JsonValueKind.Array && target.GetArrayLength() > 0)
                    {
                        target = target[0];
                    }
                    else if (target.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array && dataArr.GetArrayLength() > 0)
                    {
                        target = dataArr[0];
                    }

                    if (target.TryGetProperty("collectibleProductId", out var cpProp) && cpProp.ValueKind == JsonValueKind.String)
                    {
                        collectibleProductId = cpProp.GetString();
                    }

                    bool usingResale = false;
                    if (target.TryGetProperty("hasResellers", out var hrProp) && hrProp.ValueKind == JsonValueKind.True &&
                        target.TryGetProperty("lowestAvailableResaleProductId", out var larpProp) && larpProp.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(larpProp.GetString()))
                    {
                        collectibleProductId = larpProp.GetString();
                        usingResale = true;
                        if (target.TryGetProperty("lowestAvailableResaleSellerId", out var lrSeller) && TryReadInt64(lrSeller, out long parsedReseller) && parsedReseller > 0)
                            sellerId = parsedReseller;
                        else if (target.TryGetProperty("resellerSellerId", out var rsProp) && TryReadInt64(rsProp, out long parsedRs) && parsedRs > 0)
                            sellerId = parsedRs;

                        if (TryReadResalePrice(target, out long resalePrice))
                            expectedPrice = resalePrice;

                        if ((target.TryGetProperty("lowestAvailableResaleSellerType", out var lrTypeProp) && TryParseSellerType(lrTypeProp, out string parsedLrType)) ||
                            (target.TryGetProperty("resellerSellerType", out var rsTypeProp) && TryParseSellerType(rsTypeProp, out parsedLrType)))
                        {
                            sellerType = parsedLrType;
                        }
                    }

                    if (!usingResale && sellerId <= 0 && target.TryGetProperty("creatorId", out var cIdProp) && TryReadInt64(cIdProp, out long parsedCId))
                    {
                        sellerId = parsedCId;
                        if (target.TryGetProperty("creatorType", out var ctProp) && TryParseSellerType(ctProp, out string parsedCreatorType))
                            sellerType = parsedCreatorType;
                    }
                }
                catch (JsonException ex)
                {
                    AppServices.Logger.Debug($"[AssetCommerceService] Failed to parse marketplace-items response: {ex.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(collectibleProductId))
            {
                return (false, LocalizationService.Get("MissingAssets.PriceUnavailable"));
            }
        }

        var purchasePayload = new MarketplacePurchasePayload(
            collectibleItemId: collectibleItemId,
            collectibleProductId: collectibleProductId,
            expectedCurrency: 1,
            expectedPrice: expectedPrice,
            expectedPurchaserId: a.UserID.ToString(CultureInfo.InvariantCulture),
            expectedPurchaserType: "User",
            expectedSellerId: sellerId,
            expectedSellerType: string.IsNullOrWhiteSpace(sellerType) ? "User" : sellerType,
            idempotencyKey: Guid.NewGuid().ToString());

        string purchaseJson = JsonSerializer.Serialize(purchasePayload, ShiroAppJson.Default.MarketplacePurchasePayload);
        var buyResp = await RobloxCsrfRetryHelper.ExecuteWithCsrfRetryAsync(
            a,
            AppServices.RobloxApi.Api,
            "https://apis.roblox.com/",
            currentCsrf,
            csrf =>
            {
                var req = AppServices.AccountApiService.MakeRequest(a, $"marketplace-sales/v1/item/{collectibleItemId}/purchase-item", Method.Post);
                req.AddHeader("X-CSRF-TOKEN", csrf);
                req.AddStringBody(purchaseJson, "application/json");
                return req;
            },
            ct).ConfigureAwait(false);

        if (buyResp == null)
        {
            return (false, "No response from server");
        }

        if (!buyResp.IsSuccessful)
        {
            string? err = TryExtractErrorMessage(buyResp.Content);
            return (false, err ?? buyResp.ErrorMessage ?? buyResp.Content);
        }

        if (string.IsNullOrWhiteSpace(buyResp.Content))
        {
            return (false, "Empty purchase response from server.");
        }

        try
        {
            using var buyDoc = JsonDocument.Parse(buyResp.Content);
            var root = buyDoc.RootElement;
            if (root.TryGetProperty("purchased", out var pElem) && pElem.ValueKind == JsonValueKind.True)
            {
                return (true, null);
            }

            string? err = TryExtractErrorMessage(buyResp.Content) ?? "Purchase was not completed.";
            return (false, err);
        }
        catch (JsonException ex)
        {
            AppServices.Logger.Debug($"[AssetCommerceService] Failed to parse purchase response JSON: {ex.Message}");
            return (false, "Invalid purchase response format.");
        }
    }

    private static string? TryExtractErrorMessage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("errorMessage", out var em) && em.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(em.GetString()))
                return em.GetString();
            if (root.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rs.GetString()))
                return rs.GetString();
            if (root.TryGetProperty("purchaseResult", out var pr) && pr.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pr.GetString()))
                return pr.GetString();
            if (root.TryGetProperty("errors", out var errArr) && errArr.ValueKind == JsonValueKind.Array && errArr.GetArrayLength() > 0)
            {
                var first = errArr[0];
                if (first.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    return m.GetString();
            }
        }
        catch (JsonException ex)
        {
            AppServices.Logger.Debug($"[AssetCommerceService] Failed to parse error response JSON: {ex.Message}");
        }
        return null;
    }

    private static bool TryReadResalePrice(JsonElement target, out long price)
    {
        if (target.TryGetProperty("lowestAvailableResalePrice", out var larp) && TryReadInt64(larp, out price) && price >= 0)
            return true;
        if (target.TryGetProperty("lowestResalePrice", out var lrp) && TryReadInt64(lrp, out price) && price >= 0)
            return true;
        if (target.TryGetProperty("lowestPrice", out var lp) && TryReadInt64(lp, out price) && price >= 0)
            return true;

        price = 0;
        return false;
    }

    private static bool TryReadInt64(JsonElement prop, out long value)
    {
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out value))
            return true;
        if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;

        value = 0;
        return false;
    }

    private static bool TryParseSellerType(JsonElement prop, out string sellerType)
    {
        if (prop.ValueKind == JsonValueKind.String)
        {
            string? raw = prop.GetString()?.Trim();
            if (string.Equals(raw, "Group", StringComparison.OrdinalIgnoreCase) || raw == "2")
            {
                sellerType = "Group";
                return true;
            }
            if (string.Equals(raw, "User", StringComparison.OrdinalIgnoreCase) || raw == "1")
            {
                sellerType = "User";
                return true;
            }
            if (!string.IsNullOrEmpty(raw))
            {
                sellerType = raw;
                return true;
            }
        }
        else if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int num))
        {
            if (num == 2)
            {
                sellerType = "Group";
                return true;
            }
            if (num == 1)
            {
                sellerType = "User";
                return true;
            }
        }

        sellerType = string.Empty;
        return false;
    }
}
