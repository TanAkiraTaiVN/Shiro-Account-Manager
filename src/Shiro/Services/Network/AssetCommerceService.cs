#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Shiro.Http;

namespace Shiro.Services;

public interface IAssetCommerce
{
    Task<(bool Ok, string Content)> GetAssetDetailsAsync(Account a, long assetId, CancellationToken ct);
    Task<(bool Ok, string? Error)> PurchaseAsync(Account a, long productId, long expectedPrice, long sellerId, CancellationToken ct);
}

public sealed class AssetCommerceService : IAssetCommerce
{
    public static AssetCommerceService Default { get; } = new();

    public async Task<(bool Ok, string Content)> GetAssetDetailsAsync(Account a, long assetId, CancellationToken ct)
    {
        var request = a.MakeRequest($"v2/assets/{assetId}/details");
        var response = await a.ExecuteAccountRestAsync(AppServices.RobloxApi.Economy, "https://economy.roblox.com", request, ct).ConfigureAwait(false);
        if (response != null && response.IsSuccessful && !string.IsNullOrEmpty(response.Content))
        {
            return (true, response.Content);
        }
        return (false, string.Empty);
    }

    public async Task<(bool Ok, string? Error)> PurchaseAsync(Account a, long productId, long expectedPrice, long sellerId, CancellationToken ct)
    {
        var csrf = await a.GetCSRFTokenAsync(ct).ConfigureAwait(false);
        if (!csrf.Success)
        {
            return (false, LocalizationService.Get("MissingAssets.CannotGetCsrfToken"));
        }

        var buyRequest = a.MakeRequest($"v1/purchases/products/{productId}", Method.Post);
        buyRequest.AddHeader("X-CSRF-Token", csrf.Token);
        buyRequest.AddJsonBody(new PurchaseProductPayload(1, expectedPrice, sellerId));

        var response = await a.ExecuteAccountRestAsync(AppServices.RobloxApi.Economy, "https://economy.roblox.com", buyRequest, ct).ConfigureAwait(false);
        if (response != null && response.IsSuccessful)
        {
            return (true, null);
        }

        return (false, response?.ErrorMessage ?? response?.Content);
    }
}
