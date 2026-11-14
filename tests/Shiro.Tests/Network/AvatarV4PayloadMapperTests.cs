#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests.Network
{
    public class AvatarV4PayloadMapperTests
    {
        [Fact]
        public void TryMapPayload_FullPayload_MapsAllFourUpdateTypesAndNormalizedValues()
        {
            string json = @"{
                ""playerAvatarType"": ""3"",
                ""scales"": {
                    ""height"": 1.05,
                    ""width"": 0.95,
                    ""head"": 1.0,
                    ""depth"": 1.0,
                    ""proportion"": 0.2,
                    ""bodyType"": 0.1
                },
                ""bodyColors"": {
                    ""headColorId"": 1,
                    ""torsoColor"": ""#112233"",
                    ""rightArmColor3"": ""445566"",
                    ""leftArmColor"": ""#778899"",
                    ""rightLegColorId"": 361,
                    ""leftLegColor"": ""#AABBCC""
                },
                ""assets"": [
                    { ""id"": 12345 },
                    { ""id"": 67890, ""meta"": { ""order"": 1, ""puffiness"": 0.5 } }
                ]
            }";

            bool success = AvatarV4PayloadMapper.TryMapPayload(json, out var request, out var error);

            Assert.True(success);
            Assert.Null(error);
            Assert.NotNull(request);
            Assert.Equal(new[] { "UpdateAvatarType", "UpdateScales", "UpdateBodyColors", "UpdateAssets" }, request!.UpdateTypes);

            var model = request.AvatarDefinition.UpdateAvatarModel;
            Assert.NotNull(model);
            Assert.Equal("R15", model.PlayerAvatarType);

            Assert.NotNull(model.Scales);
            Assert.Equal(1.05, model.Scales!.Height);
            Assert.Equal(0.95, model.Scales.Width);
            Assert.Equal(1.0, model.Scales.Head);
            Assert.Equal(1.0, model.Scales.Depth);
            Assert.Equal(0.2, model.Scales.Proportion);
            Assert.Equal(0.1, model.Scales.BodyType);

            Assert.NotNull(model.BodyColors);
            Assert.Equal("#F2F3F3", model.BodyColors!.HeadColor);
            Assert.Equal("#112233", model.BodyColors.TorsoColor);
            Assert.Equal("#445566", model.BodyColors.RightArmColor);
            Assert.Equal("#778899", model.BodyColors.LeftArmColor);
            Assert.Equal("#564236", model.BodyColors.RightLegColor);
            Assert.Equal("#AABBCC", model.BodyColors.LeftLegColor);

            Assert.NotNull(model.Assets);
            Assert.Equal(2, model.Assets!.Length);
            Assert.Equal(12345, model.Assets[0].Id);
            Assert.Null(model.Assets[0].Meta);
            Assert.Equal(67890, model.Assets[1].Id);
            Assert.NotNull(model.Assets[1].Meta);

            string serialized = JsonSerializer.Serialize(request, ShiroAppJson.Default.AvatarV4PatchRequest);
            Assert.Contains("\"updateTypes\":[\"UpdateAvatarType\",\"UpdateScales\",\"UpdateBodyColors\",\"UpdateAssets\"]", serialized);
            Assert.Contains("\"playerAvatarType\":\"R15\"", serialized);
            Assert.Contains("\"headColor\":\"#F2F3F3\"", serialized);
            Assert.Contains("\"torsoColor\":\"#112233\"", serialized);
            Assert.Contains("\"rightLegColor\":\"#564236\"", serialized);
            Assert.Contains("\"order\":1", serialized);
        }

        [Theory]
        [InlineData("1", "R6")]
        [InlineData("R6", "R6")]
        [InlineData("r6", "R6")]
        [InlineData(1, "R6")]
        [InlineData("3", "R15")]
        [InlineData("R15", "R15")]
        [InlineData("r15", "R15")]
        [InlineData(3, "R15")]
        [InlineData("CustomAvatarType", "CustomAvatarType")]
        public void TryMapPayload_PlayerAvatarType_Normalizations(object input, string expected)
        {
            string json = input is int intVal
                ? $"{{\"playerAvatarType\": {intVal}}}"
                : $"{{\"playerAvatarType\": \"{input}\"}}";

            bool success = AvatarV4PayloadMapper.TryMapPayload(json, out var request, out _);

            Assert.True(success);
            Assert.NotNull(request);
            Assert.Single(request!.UpdateTypes);
            Assert.Equal("UpdateAvatarType", request.UpdateTypes[0]);
            Assert.Equal(expected, request.AvatarDefinition.UpdateAvatarModel.PlayerAvatarType);
        }

        [Theory]
        [InlineData("{\"playerAvatarType\": \"\"}")]
        [InlineData("{\"playerAvatarType\": \"   \"}")]
        [InlineData("{\"playerAvatarType\": 0}")]
        [InlineData("{\"playerAvatarType\": null}")]
        public void TryMapPayload_InvalidPlayerAvatarType_OmitsUpdateAvatarType(string json)
        {
            bool success = AvatarV4PayloadMapper.TryMapPayload(json, out var request, out _);

            Assert.False(success);
            Assert.Null(request);
        }

        [Fact]
        public void TryMapPayload_Scales_SupportsScaleOrScales_IgnoresNulls()
        {
            string jsonWithScale = "{\"scale\": {\"height\": 1.1, \"head\": 0.9}}";
            bool ok = AvatarV4PayloadMapper.TryMapPayload(jsonWithScale, out var request, out _);

            Assert.True(ok);
            Assert.NotNull(request);
            Assert.Single(request!.UpdateTypes);
            Assert.Equal("UpdateScales", request.UpdateTypes[0]);
            Assert.Equal(1.1, request.AvatarDefinition.UpdateAvatarModel.Scales?.Height);
            Assert.Equal(0.9, request.AvatarDefinition.UpdateAvatarModel.Scales?.Head);
            Assert.Null(request.AvatarDefinition.UpdateAvatarModel.Scales?.Width);

            string serialized = JsonSerializer.Serialize(request, ShiroAppJson.Default.AvatarV4PatchRequest);
            Assert.Contains("\"height\":1.1", serialized);
            Assert.Contains("\"head\":0.9", serialized);
            Assert.DoesNotContain("\"width\"", serialized);
            Assert.DoesNotContain("\"depth\"", serialized);
        }

        [Fact]
        public void TryMapPayload_BodyColors_MapsPaletteAndHexVariants()
        {
            string json = @"{
                ""bodyColor3s"": {
                    ""headColor"": ""#aabbcc"",
                    ""torsoColor"": ""112233"",
                    ""rightArmColor3"": 1004,
                    ""leftArmColor3"": ""1001"",
                    ""rightLegColorId"": 1007,
                    ""leftLegColorId"": 24
                }
            }";

            bool ok = AvatarV4PayloadMapper.TryMapPayload(json, out var request, out _);

            Assert.True(ok);
            Assert.NotNull(request);
            Assert.Single(request!.UpdateTypes);
            Assert.Equal("UpdateBodyColors", request.UpdateTypes[0]);

            var bc = request.AvatarDefinition.UpdateAvatarModel.BodyColors;
            Assert.NotNull(bc);
            Assert.Equal("#AABBCC", bc!.HeadColor);
            Assert.Equal("#112233", bc.TorsoColor);
            Assert.Equal("#FF0000", bc.RightArmColor); // 1004
            Assert.Equal("#F8F8F8", bc.LeftArmColor);  // 1001
            Assert.Equal("#A34B4B", bc.RightLegColor); // 1007
            Assert.Equal("#F5CD30", bc.LeftLegColor);  // 24
        }

        [Fact]
        public void TryMapPayload_Assets_PreservesIdAndMeta_FiltersInvalid()
        {
            string json = @"{
                ""assets"": [
                    { ""id"": 111 },
                    { ""id"": 222, ""meta"": { ""position"": [0.1, 0.2] } },
                    { ""id"": -1 },
                    { ""id"": 0 }
                ]
            }";

            bool ok = AvatarV4PayloadMapper.TryMapPayload(json, out var request, out _);

            Assert.True(ok);
            Assert.NotNull(request);
            Assert.Single(request!.UpdateTypes);
            Assert.Equal("UpdateAssets", request.UpdateTypes[0]);

            var assets = request.AvatarDefinition.UpdateAvatarModel.Assets;
            Assert.NotNull(assets);
            Assert.Equal(2, assets!.Length);
            Assert.Equal(111, assets[0].Id);
            Assert.Equal(222, assets[1].Id);
            Assert.NotNull(assets[1].Meta);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-a-json")]
        [InlineData("[1, 2, 3]")]
        [InlineData("\"string\"")]
        [InlineData("{}")]
        [InlineData("{\"unknown\": 123}")]
        public void TryMapPayload_NegativeCases_ReturnsFalseSafely(string? invalidJson)
        {
            bool ok = AvatarV4PayloadMapper.TryMapPayload(invalidJson, out var request, out var error);

            Assert.False(ok);
            Assert.Null(request);
            if (invalidJson == "not-a-json")
            {
                Assert.NotNull(error);
            }
        }

        [Fact]
        public void ExtractInvalidAssetIds_ParsesAllVerifiedWireFormats()
        {
            string v4ValidationJson = @"{
                ""validation"": {
                    ""invalidAssets"": [
                        { ""id"": 111 },
                        { ""id"": 222 }
                    ]
                }
            }";
            Assert.Equal(new long[] { 111, 222 }, AvatarV4PayloadMapper.ExtractInvalidAssetIds(v4ValidationJson));

            string topLevelInvalidAssets = @"{
                ""invalidAssets"": [
                    { ""id"": 222 },
                    { ""id"": 333 }
                ]
            }";
            Assert.Equal(new long[] { 222, 333 }, AvatarV4PayloadMapper.ExtractInvalidAssetIds(topLevelInvalidAssets));

            string legacyInvalidAssetIds = @"{
                ""invalidAssetIds"": [ 333, 444 ]
            }";
            Assert.Equal(new long[] { 333, 444 }, AvatarV4PayloadMapper.ExtractInvalidAssetIds(legacyInvalidAssetIds));

            string combinedJson = @"{
                ""validation"": {
                    ""invalidAssets"": [ { ""id"": 111 }, { ""id"": -5 } ]
                },
                ""invalidAssets"": [ { ""id"": 222 }, { ""id"": 111 } ],
                ""invalidAssetIds"": [ 222, 333, 0 ]
            }";
            Assert.Equal(new long[] { 111, 222, 333 }, AvatarV4PayloadMapper.ExtractInvalidAssetIds(combinedJson));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not-json")]
        [InlineData("[]")]
        [InlineData("{}")]
        public void ExtractInvalidAssetIds_NegativeAndMalformedInputs_ReturnsEmptyArray(string? responseContent)
        {
            long[] result = AvatarV4PayloadMapper.ExtractInvalidAssetIds(responseContent);
            Assert.NotNull(result);
            Assert.Empty(result);
        }
    }
}
