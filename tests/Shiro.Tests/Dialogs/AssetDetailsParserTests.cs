#nullable enable
using Shiro.Services;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Dialogs;

public sealed class AssetDetailsParserTests
{
    private const string ResaleListingWithSellerIdJson = """
        {
          "data": [
            {
              "id": 1007,
              "name": "Resale Fedora",
              "isOffSale": true,
              "hasResellers": true,
              "lowestPrice": 350,
              "lowestAvailableResaleProductId": "resale-prod-1",
              "lowestAvailableResaleSellerId": 77777,
              "creatorId": 99999,
              "creatorType": "User"
            }
          ]
        }
        """;

    [Fact]
    public void Parse_ValidJson_ExtractsNumericAndStringFields()
    {
        const string json = """{"Name":"Hat","IsForSale":true,"PriceInRobux":50,"ProductId":9,"Creator":{"Id":"7"}}""";
        AssetDetails details = AssetDetailsParser.Parse(json, 1001);

        Assert.Equal("Hat", details.Name);
        Assert.True(details.IsForSale);
        Assert.Equal(50, details.Price);
        Assert.Equal(9, details.ProductId);
        Assert.Equal(7, details.SellerId);
        Assert.Equal(50, AssetDetailsParser.ComputeCost(details));
    }

    [Fact]
    public void Parse_OffSale_ComputeCostReturnsMinusOne()
    {
        const string json = """{"Name":"Sword","IsForSale":false,"PriceInRobux":"120","ProductId":"11","Creator":{"Id":22}}""";
        AssetDetails details = AssetDetailsParser.Parse(json, 1002);

        Assert.Equal("Sword", details.Name);
        Assert.False(details.IsForSale);
        Assert.Equal(-1, AssetDetailsParser.ComputeCost(details));
    }

    [Fact]
    public void Parse_MissingName_UsesFallbackItemNumber()
    {
        const string json = """{"IsForSale":true,"PriceInRobux":0,"ProductId":5}""";
        AssetDetails details = AssetDetailsParser.Parse(json, 42);

        Assert.Equal(LocalizationService.Format("MissingAssets.ItemNumber", 42L), details.Name);
        Assert.Equal(0, AssetDetailsParser.ComputeCost(details));
    }

    [Fact]
    public void Parse_ModernCatalogBatchJson_ExtractsFields()
    {
        const string json = """
        {
          "data": [
            {
              "id": 1003,
              "name": "Fedora",
              "price": 100,
              "productId": 456,
              "creatorTargetId": 789,
              "creatorType": "User",
              "collectibleItemId": "col-item-1",
              "collectibleProductId": "col-prod-1"
            }
          ]
        }
        """;
        AssetDetails details = AssetDetailsParser.Parse(json, 1003);

        Assert.Equal("Fedora", details.Name);
        Assert.True(details.IsForSale);
        Assert.Equal(100, details.Price);
        Assert.Equal(456, details.ProductId);
        Assert.Equal(789, details.SellerId);
        Assert.Equal("User", details.SellerType);
        Assert.Equal("col-item-1", details.CollectibleItemId);
        Assert.Equal("col-prod-1", details.CollectibleProductId);
    }

    [Fact]
    public void Parse_ModernResaleListing_SetsIsForSaleTrueAndResaleProductId()
    {
        const string json = """
        {
          "data": [
            {
              "id": 1004,
              "name": "Limited Hat",
              "isOffSale": true,
              "hasResellers": true,
              "lowestPrice": 250,
              "lowestAvailableResaleProductId": "resale-prod-1",
              "creatorTargetId": 999,
              "creatorType": "Group"
            }
          ]
        }
        """;
        AssetDetails details = AssetDetailsParser.Parse(json, 1004);

        Assert.Equal("Limited Hat", details.Name);
        Assert.True(details.IsForSale);
        Assert.Equal(250, details.Price);
        Assert.Equal("resale-prod-1", details.CollectibleProductId);
        Assert.Equal("Group", details.SellerType);
    }

    [Fact]
    public void Parse_ModernOffSale_SetsIsForSaleFalse()
    {
        const string json = """
        {
          "data": [
            {
              "id": 1005,
              "name": "Expired Hat",
              "isOffSale": true,
              "hasResellers": false,
              "priceStatus": "Off Sale"
            }
          ]
        }
        """;
        AssetDetails details = AssetDetailsParser.Parse(json, 1005);

        Assert.Equal("Expired Hat", details.Name);
        Assert.False(details.IsForSale);
        Assert.Equal(-1, AssetDetailsParser.ComputeCost(details));
    }

    [Fact]
    public void Parse_ModernFree_SetsPriceZeroAndIsForSaleTrue()
    {
        const string json = """
        {
          "data": [
            {
              "id": 1006,
              "name": "Free Cap",
              "priceStatus": "Free"
            }
          ]
        }
        """;
        AssetDetails details = AssetDetailsParser.Parse(json, 1006);

        Assert.Equal("Free Cap", details.Name);
        Assert.True(details.IsForSale);
        Assert.Equal(0, details.Price);
        Assert.Equal(0, AssetDetailsParser.ComputeCost(details));
    }

    [Fact]
    public void Parse_ModernResaleListingWithResaleSellerId_UsesResaleSellerIdInsteadOfCreatorId()
    {
        AssetDetails details = AssetDetailsParser.Parse(ResaleListingWithSellerIdJson, 1007);

        Assert.True(details.IsForSale);
        Assert.Equal(350, details.Price);
        Assert.Equal("resale-prod-1", details.CollectibleProductId);
        Assert.Equal(77777, details.SellerId);
    }
}
