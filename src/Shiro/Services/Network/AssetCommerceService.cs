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
    Task<(bool Ok, string? Error)> PurchaseAsync(Account a, long assetId, long productId, long expectedPrice, long sellerId, string? collectibleItemId, string? collectibleProductId, string sellerType = "User", CancellationToken ct = default);
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

        var request = a.MakeRequest("v1/catalog/items/details", Method.Post);
        if (!string.IsNullOrWhiteSpace(a.CSRFToken))
        {
            request.AddHeader("X-CSRF-TOKEN", a.CSRFToken);
        }
        request.AddStringBody(json, "application/json");

        var response = await a.ExecuteAccountRestAsync(AppServices.RobloxApi.Catalog, "https://catalog.roblox.com/", request, ct).ConfigureAwait(false);
        if (response != null && response.StatusCode == HttpStatusCode.Forbidden)
        {
            string? newCsrf = TryGetCsrfHeader(response);
            if (!string.IsNullOrWhiteSpace(newCsrf))
            {
                a.SetCsrfToken(newCsrf);
                var retryReq = a.MakeRequest("v1/catalog/items/details", Method.Post);
                retryReq.AddHeader("X-CSRF-TOKEN", newCsrf);
                retryReq.AddStringBody(json, "application/json");
                response = await a.ExecuteAccountRestAsync(AppServices.RobloxApi.Catalog, "https://catalog.roblox.com/", retryReq, ct).ConfigureAwait(false);
            }
        }

        if (response != null && response.IsSuccessful && !string.IsNullOrWhiteSpace(response.Content))
        {
            return (true, response.Content);
        }

        return (false, string.Empty);
    }

    public async Task<(bool Ok, string? Error)> PurchaseAsync(
        Account a,
        long assetId,
        long productId,
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
            var resolveReq = a.MakeRequest("marketplace-items/v1/items/details", Method.Post);
            resolveReq.AddHeader("X-CSRF-TOKEN", currentCsrf);
            resolveReq.AddStringBody(resolveJson, "application/json");

            var resolveResp = await a.ExecuteAccountRestAsync(AppServices.RobloxApi.Api, "https://apis.roblox.com/", resolveReq, ct).ConfigureAwait(false);
            if (resolveResp != null && resolveResp.StatusCode == HttpStatusCode.Forbidden)
            {
                string? newCsrf = TryGetCsrfHeader(resolveResp);
                if (!string.IsNullOrWhiteSpace(newCsrf))
                {
                    currentCsrf = newCsrf;
                    a.SetCsrfToken(newCsrf);
                    resolveReq = a.MakeRequest("marketplace-items/v1/items/details", Method.Post);
                    resolveReq.AddHeader("X-CSRF-TOKEN", newCsrf);
                    resolveReq.AddStringBody(resolveJson, "application/json");
                    resolveResp = await a.ExecuteAccountRestAsync(AppServices.RobloxApi.Api, "https://apis.roblox.com/", resolveReq, ct).ConfigureAwait(false);
                }
            }

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
                        if (target.TryGetProperty("lowestAvailableResaleSellerId", out var lrSeller) && lrSeller.TryGetInt64(out long parsedReseller) && parsedReseller > 0)
                            sellerId = parsedReseller;
                        else if (target.TryGetProperty("resellerSellerId", out var rsProp) && rsProp.TryGetInt64(out long parsedRs) && parsedRs > 0)
                            sellerId = parsedRs;
                    }

                    if (!usingResale && sellerId <= 0 && target.TryGetProperty("creatorId", out var cIdProp) && cIdProp.TryGetInt64(out long parsedCId))
                    {
                        sellerId = parsedCId;
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
        var buyRequest = a.MakeRequest($"marketplace-sales/v1/item/{collectibleItemId}/purchase-item", Method.Post);
        buyRequest.AddHeader("X-CSRF-TOKEN", currentCsrf);
        buyRequest.AddStringBody(purchaseJson, "application/json");

        var buyResp = await a.ExecuteAccountRestAsync(AppServices.RobloxApi.Api, "https://apis.roblox.com/", buyRequest, ct).ConfigureAwait(false);
        if (buyResp != null && buyResp.StatusCode == HttpStatusCode.Forbidden)
        {
            string? newCsrf = TryGetCsrfHeader(buyResp);
            if (!string.IsNullOrWhiteSpace(newCsrf))
            {
                currentCsrf = newCsrf;
                a.SetCsrfToken(newCsrf);
                var retryBuyReq = a.MakeRequest($"marketplace-sales/v1/item/{collectibleItemId}/purchase-item", Method.Post);
                retryBuyReq.AddHeader("X-CSRF-TOKEN", newCsrf);
                retryBuyReq.AddStringBody(purchaseJson, "application/json");
                buyResp = await a.ExecuteAccountRestAsync(AppServices.RobloxApi.Api, "https://apis.roblox.com/", retryBuyReq, ct).ConfigureAwait(false);
            }
        }

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

    private static string? TryGetCsrfHeader(RestResponse resp)
    {
        foreach (var header in resp.Headers)
        {
            if (string.Equals(header.Name, "x-csrf-token", StringComparison.OrdinalIgnoreCase))
            {
                return header.Value?.ToString();
            }
        }
        return null;
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
}
