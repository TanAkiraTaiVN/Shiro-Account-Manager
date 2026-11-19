#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;

namespace Shiro.Services
{
    /// <summary>
    /// Loads and caches vector country flag icons from <c>Resources/Flags/*.svg</c>
    /// into native Avalonia <see cref="DrawingImage"/> instances without external SVG packages.
    /// </summary>
    public static partial class FlagIconService
    {
        private static readonly ConcurrentDictionary<string, IImage?> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly XNamespace XlinkNs = "http://www.w3.org/1999/xlink";
        private const int MaxUseDepth = 8;

        public static IImage? GetFlagImage(string? countryCode)
        {
            string? normalized = NormalizeCountryCode(countryCode);
            if (string.IsNullOrEmpty(normalized))
                return null;

            if (Cache.TryGetValue(normalized, out IImage? cached))
                return cached;

            var (image, cacheable) = LoadFlagDrawingImage(normalized);
            if (cacheable)
                Cache.TryAdd(normalized, image);

            return image;
        }

        public static string? InferCountryCode(string? countryCode, string? region)
        {
            string? normalized = NormalizeCountryCode(countryCode);
            if (!string.IsNullOrEmpty(normalized))
                return normalized;

            if (string.IsNullOrWhiteSpace(region))
                return null;

            string trimmed = region.Trim();
            int commaIdx = trimmed.LastIndexOf(',');
            if (commaIdx >= 0 && commaIdx < trimmed.Length - 1)
            {
                string suffix = trimmed[(commaIdx + 1)..].Trim();
                string? fromSuffix = NormalizeCountryCode(suffix);
                if (!string.IsNullOrEmpty(fromSuffix))
                    return fromSuffix;
            }

            if (trimmed.StartsWith("US-", StringComparison.OrdinalIgnoreCase)) return "us";
            if (trimmed.StartsWith("EU-", StringComparison.OrdinalIgnoreCase)) return "eu";
            if (trimmed.StartsWith("AP-", StringComparison.OrdinalIgnoreCase)) return "sg";

            return null;
        }

        private static string? NormalizeCountryCode(string? code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;

            string trimmed = code.Trim().ToLowerInvariant();
            if (trimmed == "uk")
                return "gb";

            if (trimmed.Length == 2 &&
                trimmed[0] >= 'a' && trimmed[0] <= 'z' &&
                trimmed[1] >= 'a' && trimmed[1] <= 'z')
            {
                return trimmed;
            }

            return null;
        }

        private static (IImage? Image, bool Cacheable) LoadFlagDrawingImage(string code)
        {
            try
            {
                using Stream? stream = OpenFlagSvgStream(code);
                if (stream == null)
                    return (null, true);

                XDocument doc = XDocument.Load(stream);
                XElement? svgRoot = doc.Root;
                if (svgRoot == null)
                    return (null, true);

                Rect viewBox = ParseViewBox(svgRoot.Attribute("viewBox")?.Value);
                var rootGroup = new DrawingGroup
                {
                    ClipGeometry = new RectangleGeometry(viewBox)
                };

                // Invisible bounding rect so DrawingImage preserves exact aspect ratio even when clipped
                rootGroup.Children.Add(new GeometryDrawing
                {
                    Geometry = new RectangleGeometry(viewBox),
                    Brush = Brushes.Transparent
                });

                var idMap = CollectIdElements(svgRoot);
                ParseElementChildren(
                    svgRoot,
                    rootGroup,
                    Matrix.Identity,
                    "#000000",
                    1.0,
                    inheritedStroke: null,
                    inheritedStrokeWidth: null,
                    idMap,
                    depth: 0);
                // Avoid caching degraded aspect-only images if StreamGeometry ran before platform initialization.
                bool hasVectorShapes = rootGroup.Children.Count > 1;
                return (new DrawingImage { Drawing = rootGroup }, hasVectorShapes);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[FlagIconService] Failed to parse flag '{code}.svg': {ex.Message}");
                return (null, false);
            }
        }

        private static Stream? OpenFlagSvgStream(string code)
        {
            try
            {
                var uri = new Uri($"avares://Shiro/Resources/Flags/{code}.svg");
                if (AssetLoader.Exists(uri))
                    return AssetLoader.Open(uri);
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[FlagIconService] AssetLoader.Open failed for '{code}', falling back to disk", ex);
            }

            string diskPath = Path.Combine(AppContext.BaseDirectory, "Resources", "Flags", $"{code}.svg");
            if (File.Exists(diskPath))
                return File.OpenRead(diskPath);

            // Fallback to source tree during tests
            string? dir = AppContext.BaseDirectory;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = Path.Combine(dir, "src", "Shiro", "Resources", "Flags", $"{code}.svg");
                if (File.Exists(candidate))
                    return File.OpenRead(candidate);
                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }

        private static Rect ParseViewBox(string? viewBoxAttr)
        {
            if (!string.IsNullOrWhiteSpace(viewBoxAttr))
            {
                string[] parts = viewBoxAttr.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4 &&
                    TryParseDouble(parts[0], out double x) &&
                    TryParseDouble(parts[1], out double y) &&
                    TryParseDouble(parts[2], out double w) &&
                    TryParseDouble(parts[3], out double h) &&
                    w > 0 && h > 0)
                {
                    return new Rect(x, y, w, h);
                }
            }

            return new Rect(0, 0, 640, 480);
        }

        private static Dictionary<string, XElement> CollectIdElements(XElement svgRoot)
        {
            var idMap = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (XElement descendant in svgRoot.Descendants())
            {
                string? id = descendant.Attribute("id")?.Value;
                if (!string.IsNullOrWhiteSpace(id))
                    idMap.TryAdd(id.Trim(), descendant);
            }

            return idMap;
        }

        private static void ParseElementChildren(
            XElement parent,
            DrawingGroup targetGroup,
            Matrix currentTransform,
            string? inheritedFill,
            double inheritedOpacity,
            string? inheritedStroke,
            string? inheritedStrokeWidth,
            IReadOnlyDictionary<string, XElement> idMap,
            int depth)
        {
            foreach (XElement el in parent.Elements())
            {
                string localName = el.Name.LocalName;
                if (localName is "defs" or "clipPath" or "marker" or "mask" or "symbol" or "title" or "desc" or "metadata" or "linearGradient" or "radialGradient")
                    continue;

                ParseSingleElement(
                    el,
                    targetGroup,
                    currentTransform,
                    inheritedFill,
                    inheritedOpacity,
                    inheritedStroke,
                    inheritedStrokeWidth,
                    idMap,
                    depth);
            }
        }

        private static void ParseSingleElement(
            XElement el,
            DrawingGroup targetGroup,
            Matrix currentTransform,
            string? inheritedFill,
            double inheritedOpacity,
            string? inheritedStroke,
            string? inheritedStrokeWidth,
            IReadOnlyDictionary<string, XElement> idMap,
            int depth)
        {
            string localName = el.Name.LocalName;
            Matrix elTransform = currentTransform;

            if (localName == "use")
            {
                TryParseDouble(el.Attribute("x")?.Value, out double ux);
                TryParseDouble(el.Attribute("y")?.Value, out double uy);
                if (ux != 0 || uy != 0)
                    elTransform = Matrix.CreateTranslation(ux, uy) * elTransform;
            }

            string? transformAttr = el.Attribute("transform")?.Value;
            if (!string.IsNullOrWhiteSpace(transformAttr))
            {
                elTransform = ParseTransform(transformAttr) * elTransform;
            }

            string? fillAttr = el.Attribute("fill")?.Value ?? GetStyleValue(el, "fill") ?? inheritedFill;
            string? strokeAttr = el.Attribute("stroke")?.Value ?? GetStyleValue(el, "stroke") ?? inheritedStroke;
            string? strokeWidthAttr = el.Attribute("stroke-width")?.Value ?? GetStyleValue(el, "stroke-width") ?? inheritedStrokeWidth;

            double opacity = inheritedOpacity;
            if (TryParseDouble(el.Attribute("opacity")?.Value ?? GetStyleValue(el, "opacity"), out double op))
                opacity *= Math.Clamp(op, 0.0, 1.0);
            if (TryParseDouble(el.Attribute("fill-opacity")?.Value ?? GetStyleValue(el, "fill-opacity"), out double fop))
                opacity *= Math.Clamp(fop, 0.0, 1.0);

            if (localName is "g" or "svg" or "symbol")
            {
                ParseElementChildren(
                    el,
                    targetGroup,
                    elTransform,
                    fillAttr,
                    opacity,
                    strokeAttr,
                    strokeWidthAttr,
                    idMap,
                    depth);
                return;
            }

            if (localName == "use")
            {
                if (depth >= MaxUseDepth)
                    return;

                string? href = el.Attribute("href")?.Value ?? el.Attribute(XlinkNs + "href")?.Value;
                if (string.IsNullOrWhiteSpace(href))
                    return;

                string targetId = href.TrimStart('#').Trim();
                if (!string.IsNullOrEmpty(targetId) &&
                    idMap.TryGetValue(targetId, out XElement? targetEl) &&
                    !ReferenceEquals(targetEl, el))
                {
                    ParseSingleElement(
                        targetEl,
                        targetGroup,
                        elTransform,
                        fillAttr,
                        opacity,
                        strokeAttr,
                        strokeWidthAttr,
                        idMap,
                        depth + 1);
                }

                return;
            }

            Geometry? geometry = CreateGeometry(el, localName);
            if (geometry == null)
                return;

            IBrush? brush = CreateBrush(fillAttr, opacity);
            IPen? pen = CreatePen(strokeAttr, strokeWidthAttr, opacity);

            if (brush == null && pen == null)
                return;

            var drawing = new GeometryDrawing
            {
                Geometry = geometry,
                Brush = brush,
                Pen = pen
            };

            if (elTransform != Matrix.Identity)
            {
                var transformedGroup = new DrawingGroup
                {
                    Transform = new MatrixTransform(elTransform)
                };
                transformedGroup.Children.Add(drawing);
                targetGroup.Children.Add(transformedGroup);
            }
            else
            {
                targetGroup.Children.Add(drawing);
            }
        }
    }
}
