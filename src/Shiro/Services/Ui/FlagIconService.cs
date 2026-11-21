#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
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
        internal const int MaxFlagCacheEntries = 64;
        private const string EmbeddedFlagsArchiveResourceName = "Shiro.Resources.Flags.zip";
        private static readonly Lazy<byte[]?> EmbeddedFlagsArchiveBytes = new(LoadEmbeddedFlagsArchiveBytes);
        private static readonly BoundedFlagCache Cache = new(MaxFlagCacheEntries);
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
                Cache.Set(normalized, image);

            return image;
        }

        internal static void ClearCacheForTest() => Cache.Clear();

        internal static int CachedCountForTest => Cache.Count;

        internal static bool IsCachedForTest(string? countryCode)
        {
            string? normalized = NormalizeCountryCode(countryCode);
            return !string.IsNullOrEmpty(normalized) && Cache.ContainsKeyNoPromote(normalized);
        }

        internal static DrawingImage? ParseSvgForTesting(string svgXml)
        {
            if (string.IsNullOrWhiteSpace(svgXml))
                return null;

            XDocument doc = XDocument.Parse(svgXml);
            var (image, _) = BuildDrawingImageFromSvg(doc.Root);
            return image as DrawingImage;
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
                return BuildDrawingImageFromSvg(doc.Root);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[FlagIconService] Failed to parse flag '{code}.svg': {ex.Message}");
                return (null, false);
            }
        }

        private static (IImage? Image, bool Cacheable) BuildDrawingImageFromSvg(XElement? svgRoot)
        {
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

        internal static Stream? TryOpenFromEmbeddedArchive(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;

            byte[]? archiveBytes = EmbeddedFlagsArchiveBytes.Value;
            if (archiveBytes == null || archiveBytes.Length == 0)
                return null;

            string entryName = $"{code.Trim().ToLowerInvariant()}.svg";
            try
            {
                using var zipStream = new MemoryStream(archiveBytes, writable: false);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                ZipArchiveEntry? entry = archive.GetEntry(entryName);
                if (entry == null)
                    return null;

                var output = new MemoryStream();
                using (Stream entryStream = entry.Open())
                    entryStream.CopyTo(output);
                output.Position = 0;
                return output;
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[FlagIconService] Failed reading '{entryName}' from embedded Flags.zip", ex);
                return null;
            }
        }

        private static byte[]? LoadEmbeddedFlagsArchiveBytes()
        {
            try
            {
                using Stream? stream = typeof(FlagIconService).Assembly.GetManifestResourceStream(EmbeddedFlagsArchiveResourceName);
                if (stream == null)
                    return null;

                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug("[FlagIconService] Failed to load embedded Flags.zip archive", ex);
                return null;
            }
        }

        private static Stream? OpenFlagSvgStream(string code)
        {
            if (TryOpenFromEmbeddedArchive(code) is { } embeddedStream)
                return embeddedStream;

            try
            {
                var uri = new Uri($"avares://Shiro/Resources/Flags/{code}.svg");
                if (AssetLoader.Exists(uri))
                    return AssetLoader.Open(uri);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[FlagIconService] AssetLoader.Open failed for '{code}', falling back to disk", ex);
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

                ParseSingleElement(el, targetGroup, currentTransform, inheritedFill, inheritedOpacity,
                    inheritedStroke, inheritedStrokeWidth, idMap, depth);
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
            string? clipPathAttr = el.Attribute("clip-path")?.Value ?? GetStyleValue(el, "clip-path");
            Geometry? clipGeom = ResolveClipGeometry(clipPathAttr, idMap);

            double opacity = inheritedOpacity;
            if (TryParseDouble(el.Attribute("opacity")?.Value ?? GetStyleValue(el, "opacity"), out double op))
                opacity *= Math.Clamp(op, 0.0, 1.0);
            if (TryParseDouble(el.Attribute("fill-opacity")?.Value ?? GetStyleValue(el, "fill-opacity"), out double fop))
                opacity *= Math.Clamp(fop, 0.0, 1.0);

            DrawingGroup renderTarget = clipGeom != null ? new DrawingGroup { ClipGeometry = clipGeom } : targetGroup;
            Matrix childTransform = clipGeom != null ? Matrix.Identity : elTransform;

            if (localName is "g" or "svg" or "symbol")
            {
                ParseElementChildren(el, renderTarget, childTransform, fillAttr, opacity, strokeAttr, strokeWidthAttr, idMap, depth);
                if (clipGeom != null && renderTarget.Children.Count > 0)
                    AddDrawingToGroup(targetGroup, renderTarget, elTransform);
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
                    ParseSingleElement(targetEl, renderTarget, childTransform, fillAttr, opacity, strokeAttr, strokeWidthAttr, idMap, depth + 1);
                    if (clipGeom != null && renderTarget.Children.Count > 0)
                        AddDrawingToGroup(targetGroup, renderTarget, elTransform);
                }

                return;
            }

            Geometry? geometry = CreateGeometry(el, localName);
            if (geometry == null)
                return;

            IBrush? brush = CreateBrush(fillAttr, opacity, idMap);
            IPen? pen = CreatePen(strokeAttr, strokeWidthAttr, opacity, idMap);
            if (brush == null && pen == null)
                return;

            var drawing = new GeometryDrawing { Geometry = geometry, Brush = brush, Pen = pen };
            if (clipGeom != null)
            {
                renderTarget.Children.Add(drawing);
                AddDrawingToGroup(targetGroup, renderTarget, elTransform);
            }
            else
            {
                AddDrawingToGroup(targetGroup, drawing, elTransform);
            }
        }

        private static void AddDrawingToGroup(DrawingGroup targetGroup, Drawing drawing, Matrix transform)
        {
            if (transform != Matrix.Identity)
            {
                var transformedGroup = new DrawingGroup { Transform = new MatrixTransform(transform) };
                transformedGroup.Children.Add(drawing);
                targetGroup.Children.Add(transformedGroup);
                return;
            }
            targetGroup.Children.Add(drawing);
        }

        internal sealed class BoundedFlagCache
        {
            private readonly int _capacity;
            private readonly Lock _lock = new();
            private readonly Dictionary<string, LinkedListNode<(string Key, IImage? Value)>> _map = new(StringComparer.OrdinalIgnoreCase);
            private readonly LinkedList<(string Key, IImage? Value)> _order = new();

            public BoundedFlagCache(int capacity)
            {
                if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
                _capacity = capacity;
            }

            public int Count { get { lock (_lock) return _map.Count; } }

            public bool TryGetValue(string key, out IImage? value)
            {
                lock (_lock)
                {
                    if (_map.TryGetValue(key, out var node))
                    {
                        _order.Remove(node);
                        _order.AddFirst(node);
                        value = node.Value.Value;
                        return true;
                    }
                    value = null;
                    return false;
                }
            }

            public void Set(string key, IImage? value)
            {
                lock (_lock)
                {
                    if (_map.TryGetValue(key, out var existing))
                    {
                        existing.Value = (existing.Value.Key, value);
                        _order.Remove(existing);
                        _order.AddFirst(existing);
                        return;
                    }
                    while (_map.Count >= _capacity && _order.Last != null)
                    {
                        var lru = _order.Last;
                        _order.RemoveLast();
                        _map.Remove(lru.Value.Key);
                    }
                    var node = new LinkedListNode<(string Key, IImage? Value)>((key, value));
                    _order.AddFirst(node);
                    _map[key] = node;
                }
            }

            public bool ContainsKeyNoPromote(string key) { lock (_lock) return _map.ContainsKey(key); }

            public void Clear()
            {
                lock (_lock) { _map.Clear(); _order.Clear(); }
            }
        }
    }
}
