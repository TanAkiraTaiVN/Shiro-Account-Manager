#nullable enable
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public sealed class FlagIconServiceTests
    {
        private static readonly string[] SampleCountryCodes =
        {
            "ad", "ae", "af", "ag", "ai", "al", "am", "ao", "aq", "ar",
            "as", "at", "au", "aw", "ax", "az", "ba", "bb", "bd", "be",
            "bf", "bg", "bh", "bi", "bj", "bl", "bm", "bn", "bo", "bq",
            "br", "bs", "bt", "bv", "bw", "by", "bz", "ca", "cc", "cd",
            "cf", "cg", "ch", "ci", "ck", "cl", "cm", "cn", "co", "cr",
            "cu", "cv", "cw", "cx", "cy", "cz", "de", "dj", "dk", "dm",
            "do", "dz", "ec", "ee", "eg", "eh", "er", "es"
        };

        private const string GradientSvgSample = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 100 100">
              <defs>
                <linearGradient id="base-grad">
                  <stop offset="0" stop-color="#ff0000" stop-opacity="0.5"/>
                  <stop offset="100%" stop-color="#0000ff"/>
                </linearGradient>
                <linearGradient id="lin-user" xlink:href="#base-grad" x1="10" y1="20" x2="90" y2="80" gradientUnits="userSpaceOnUse" spreadMethod="reflect"/>
                <radialGradient id="rad-rel" href="#base-grad" cx="0.25" cy="0.75" r="0.4" fx="0.3" fy="0.7"/>
              </defs>
              <rect x="0" y="0" width="50" height="50" fill="url('#lin-user')" opacity="0.5"/>
              <circle cx="70" cy="70" r="20" fill='url("#rad-rel")'/>
            </svg>
            """;

        private const string ClipPathSvgSample = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 100 100">
              <defs>
                <rect id="clip-ref" x="0" y="0" width="25" height="25"/>
                <clipPath id="multi-clip" clip-rule="evenodd">
                  <use xlink:href="#clip-ref" x="5" y="5"/>
                  <g transform="translate(10, 0)">
                    <circle cx="50" cy="50" r="25"/>
                  </g>
                </clipPath>
                <linearGradient id="stroke-grad" x1="0%" y1="0%" x2="100%" y2="0%" spreadMethod="repeat" gradientTransform="scale(2)">
                  <stop offset="0%" stop-color="#ffffff"/>
                  <stop offset="100%" stop-color="#000000"/>
                </linearGradient>
              </defs>
              <g style="clip-path: url(#multi-clip)" transform="translate(10, 5)">
                <rect x="0" y="0" width="100" height="100" fill="#22aa44" stroke="url(#stroke-grad)" stroke-width="2"/>
              </g>
            </svg>
            """;

        [AvaloniaFact]
        public void FlagIconService_GetFlagImage_RendersRealFlagsWithClipPathsAndGradients()
        {
            foreach (string clippedCode in new[] { "ag", "ai", "aw" })
            {
                var img = Assert.IsType<DrawingImage>(FlagIconService.GetFlagImage(clippedCode));
                var root = Assert.IsType<DrawingGroup>(img.Drawing);
                Assert.True(ContainsClipGeometry(root), $"Expected {clippedCode}.svg to apply ClipGeometry from <clipPath>.");
            }

            var ni = Assert.IsType<DrawingImage>(FlagIconService.GetFlagImage("ni"));
            var niRoot = Assert.IsType<DrawingGroup>(ni.Drawing);
            Assert.True(ContainsBrush<LinearGradientBrush>(niRoot), "Expected ni.svg to render LinearGradientBrush fills.");

            var bz = Assert.IsType<DrawingImage>(FlagIconService.GetFlagImage("bz"));
            var bzRoot = Assert.IsType<DrawingGroup>(bz.Drawing);
            Assert.True(ContainsBrush<RadialGradientBrush>(bzRoot), "Expected bz.svg to render RadialGradientBrush fills.");

            var gt = Assert.IsType<DrawingImage>(FlagIconService.GetFlagImage("gt"));
            var gtRoot = Assert.IsType<DrawingGroup>(gt.Drawing);
            Assert.True(ContainsBrush<LinearGradientBrush>(gtRoot), "Expected gt.svg to render LinearGradientBrush fills.");
            Assert.True(ContainsBrush<RadialGradientBrush>(gtRoot), "Expected gt.svg to render RadialGradientBrush fills.");
        }

        [AvaloniaFact]
        public void FlagIconService_ParseSvg_ResolvesLinearAndRadialGradientsWithInheritanceAndQuotedUrls()
        {
            var img = Assert.IsType<DrawingImage>(FlagIconService.ParseSvgForTesting(GradientSvgSample));
            var root = Assert.IsType<DrawingGroup>(img.Drawing);

            var brushes = CollectBrushes(root);
            var linear = Assert.Single(brushes, b => b is LinearGradientBrush) as LinearGradientBrush;
            Assert.NotNull(linear);
            Assert.Equal(new RelativePoint(10, 20, RelativeUnit.Absolute), linear!.StartPoint);
            Assert.Equal(new RelativePoint(90, 80, RelativeUnit.Absolute), linear.EndPoint);
            Assert.Equal(GradientSpreadMethod.Reflect, linear.SpreadMethod);
            Assert.Equal(2, linear.GradientStops.Count);
            Assert.Equal(0.0, linear.GradientStops[0].Offset);
            Assert.Equal(255, linear.GradientStops[0].Color.R);
            Assert.InRange(linear.GradientStops[0].Color.A, (byte)63, (byte)64); // 0.5 stop-opacity * 0.5 opacity
            Assert.Equal(1.0, linear.GradientStops[1].Offset);
            Assert.Equal((byte)128, linear.GradientStops[1].Color.A); // 1.0 * 0.5 opacity

            var radial = Assert.Single(brushes, b => b is RadialGradientBrush) as RadialGradientBrush;
            Assert.NotNull(radial);
            Assert.Equal(new RelativePoint(0.25, 0.75, RelativeUnit.Relative), radial!.Center);
            Assert.Equal(new RelativePoint(0.3, 0.7, RelativeUnit.Relative), radial.GradientOrigin);
            Assert.Equal(new RelativeScalar(0.4, RelativeUnit.Relative), radial.RadiusX);
            Assert.Equal(new RelativeScalar(0.4, RelativeUnit.Relative), radial.RadiusY);
            Assert.Equal(2, radial.GradientStops.Count);
        }

        [AvaloniaFact]
        public void FlagIconService_ParseSvg_ResolvesClipPathGeometryGroupFromStyle()
        {
            var img = Assert.IsType<DrawingImage>(FlagIconService.ParseSvgForTesting(ClipPathSvgSample));
            var root = Assert.IsType<DrawingGroup>(img.Drawing);

            var transformed = Assert.IsType<DrawingGroup>(root.Children[1]);
            Assert.NotNull(transformed.Transform);
            Assert.Equal(Matrix.CreateTranslation(10, 5), transformed.Transform!.Value);

            var clipped = Assert.IsType<DrawingGroup>(transformed.Children[0]);
            var geomGroup = Assert.IsType<GeometryGroup>(clipped.ClipGeometry);
            Assert.Equal(FillRule.EvenOdd, geomGroup.FillRule);
            Assert.Equal(2, geomGroup.Children.Count);

            var stroked = Assert.IsType<GeometryDrawing>(clipped.Children[0]);
            Assert.NotNull(stroked.Pen);
            var strokeGrad = Assert.IsType<LinearGradientBrush>(stroked.Pen!.Brush);
            Assert.Equal(GradientSpreadMethod.Repeat, strokeGrad.SpreadMethod);
            Assert.NotNull(strokeGrad.Transform);
        }

        [AvaloniaFact]
        public void FlagIconService_GetFlagImage_BoundsCacheCapacityTo64()
        {
            FlagIconService.ClearCacheForTest();
            try
            {
                Assert.Equal(64, FlagIconService.MaxFlagCacheEntries);
                Assert.Equal(0, FlagIconService.CachedCountForTest);

                foreach (string code in SampleCountryCodes)
                {
                    Assert.NotNull(FlagIconService.GetFlagImage(code));
                }

                Assert.InRange(FlagIconService.CachedCountForTest, 1, FlagIconService.MaxFlagCacheEntries);
                Assert.True(FlagIconService.IsCachedForTest("es"));
            }
            finally
            {
                FlagIconService.ClearCacheForTest();
            }
        }

        [Fact]
        public void BoundedFlagCache_EvictsLeastRecentlyUsedAndPromotesHits()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new FlagIconService.BoundedFlagCache(0));

            var cache = new FlagIconService.BoundedFlagCache(3);
            var imgA = new DrawingImage();
            var imgB = new DrawingImage();
            var imgC = new DrawingImage();
            var imgD = new DrawingImage();
            var imgC2 = new DrawingImage();

            cache.Set("a", imgA);
            cache.Set("b", imgB);
            cache.Set("c", imgC);
            cache.Set("c", imgC2);
            Assert.Equal(3, cache.Count);

            // Access "a" so "b" becomes least-recently-used ("c" was just overwritten).
            Assert.True(cache.TryGetValue("a", out IImage? hitA));
            Assert.Same(imgA, hitA);
            Assert.False(cache.TryGetValue("missing", out _));

            // Insert "d" -> must evict "b", keeping "a", "c", "d".
            cache.Set("d", imgD);
            Assert.Equal(3, cache.Count);
            Assert.False(cache.ContainsKeyNoPromote("b"));
            Assert.True(cache.ContainsKeyNoPromote("a"));
            Assert.True(cache.ContainsKeyNoPromote("c"));
            Assert.True(cache.ContainsKeyNoPromote("d"));

            Assert.True(cache.TryGetValue("c", out IImage? hitC));
            Assert.Same(imgC2, hitC);
        }

        private static bool ContainsClipGeometry(DrawingGroup root)
        {
            foreach (Drawing child in root.Children)
            {
                if (child is DrawingGroup group)
                {
                    if (group.ClipGeometry != null)
                        return true;
                    if (ContainsClipGeometry(group))
                        return true;
                }
            }
            return false;
        }

        private static bool ContainsBrush<TBrush>(DrawingGroup root) where TBrush : class, IBrush
        {
            foreach (Drawing child in root.Children)
            {
                if (child is GeometryDrawing gd && gd.Brush is TBrush)
                    return true;
                if (child is DrawingGroup group && ContainsBrush<TBrush>(group))
                    return true;
            }
            return false;
        }

        private static List<IBrush> CollectBrushes(DrawingGroup root)
        {
            var result = new List<IBrush>();
            foreach (Drawing child in root.Children)
            {
                if (child is GeometryDrawing gd && gd.Brush != null && gd.Brush != Brushes.Transparent)
                    result.Add(gd.Brush);
                else if (child is DrawingGroup group)
                    result.AddRange(CollectBrushes(group));
            }
            return result;
        }
    }
}
