#nullable enable
using Shiro.Services;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Dialogs;

public sealed class AssetDetailsParserTests
{
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
}
