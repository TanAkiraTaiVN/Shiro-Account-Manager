#nullable enable
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
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

        [GeneratedRegex(@"([a-zA-Z]+)\s*\(([^)]+)\)", RegexOptions.CultureInvariant)]
        private static partial Regex TransformFunctionRegex();

        public static IImage? GetFlagImage(string? countryCode)
        {
            string? normalized = NormalizeCountryCode(countryCode);
            if (string.IsNullOrEmpty(normalized))
                return null;

            return Cache.GetOrAdd(normalized, LoadFlagDrawingImage);
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

        private static IImage? LoadFlagDrawingImage(string code)
        {
            try
            {
                using Stream? stream = OpenFlagSvgStream(code);
                if (stream == null)
                    return null;

                XDocument doc = XDocument.Load(stream);
                XElement? svgRoot = doc.Root;
                if (svgRoot == null)
                    return null;

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

                ParseElementChildren(svgRoot, rootGroup, Matrix.Identity, "#000000", 1.0);
                return new DrawingImage { Drawing = rootGroup };
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"[FlagIconService] Failed to parse flag '{code}.svg': {ex.Message}");
                return null;
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

        private static void ParseElementChildren(
            XElement parent,
            DrawingGroup targetGroup,
            Matrix currentTransform,
            string? inheritedFill,
            double inheritedOpacity)
        {
            foreach (XElement el in parent.Elements())
            {
                string localName = el.Name.LocalName;
                if (localName is "defs" or "clipPath" or "marker" or "mask" or "title" or "desc" or "metadata" or "linearGradient" or "radialGradient")
                    continue;

                Matrix elTransform = currentTransform;
                string? transformAttr = el.Attribute("transform")?.Value;
                if (!string.IsNullOrWhiteSpace(transformAttr))
                {
                    elTransform = ParseTransform(transformAttr) * currentTransform;
                }

                string? fillAttr = el.Attribute("fill")?.Value ?? GetStyleValue(el, "fill") ?? inheritedFill;
                double opacity = inheritedOpacity;
                if (TryParseDouble(el.Attribute("opacity")?.Value ?? GetStyleValue(el, "opacity"), out double op))
                    opacity *= Math.Clamp(op, 0.0, 1.0);
                if (TryParseDouble(el.Attribute("fill-opacity")?.Value ?? GetStyleValue(el, "fill-opacity"), out double fop))
                    opacity *= Math.Clamp(fop, 0.0, 1.0);

                if (localName == "g" || localName == "svg")
                {
                    ParseElementChildren(el, targetGroup, elTransform, fillAttr, opacity);
                    continue;
                }

                Geometry? geometry = CreateGeometry(el, localName);
                if (geometry == null)
                    continue;

                IBrush? brush = CreateBrush(fillAttr, opacity);
                IPen? pen = CreatePen(el, opacity);

                if (brush == null && pen == null)
                    continue;

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

        private static Geometry? CreateGeometry(XElement el, string localName)
        {
            try
            {
                switch (localName)
                {
                    case "path":
                    {
                        string? d = el.Attribute("d")?.Value;
                        if (string.IsNullOrWhiteSpace(d)) return null;
                        // Skip marker-only invisible paths (e.g. us.svg star grid carrier path with fill="none" and no stroke)
                        if (el.Attribute("marker-mid") != null &&
                            string.Equals(el.Attribute("fill")?.Value, "none", StringComparison.OrdinalIgnoreCase) &&
                            el.Attribute("stroke") == null)
                        {
                            return null;
                        }
                        return StreamGeometry.Parse(d);
                    }
                    case "rect":
                    {
                        TryParseDouble(el.Attribute("x")?.Value, out double x);
                        TryParseDouble(el.Attribute("y")?.Value, out double y);
                        if (!TryParseDouble(el.Attribute("width")?.Value, out double w) || w <= 0) return null;
                        if (!TryParseDouble(el.Attribute("height")?.Value, out double h) || h <= 0) return null;
                        return new RectangleGeometry(new Rect(x, y, w, h));
                    }
                    case "circle":
                    {
                        TryParseDouble(el.Attribute("cx")?.Value, out double cx);
                        TryParseDouble(el.Attribute("cy")?.Value, out double cy);
                        if (!TryParseDouble(el.Attribute("r")?.Value, out double r) || r <= 0) return null;
                        return new EllipseGeometry(new Rect(cx - r, cy - r, r * 2, r * 2));
                    }
                    case "ellipse":
                    {
                        TryParseDouble(el.Attribute("cx")?.Value, out double cx);
                        TryParseDouble(el.Attribute("cy")?.Value, out double cy);
                        if (!TryParseDouble(el.Attribute("rx")?.Value, out double rx) || rx <= 0) return null;
                        if (!TryParseDouble(el.Attribute("ry")?.Value, out double ry) || ry <= 0) return null;
                        return new EllipseGeometry(new Rect(cx - rx, cy - ry, rx * 2, ry * 2));
                    }
                    case "polygon":
                    {
                        string? pts = el.Attribute("points")?.Value;
                        if (string.IsNullOrWhiteSpace(pts)) return null;
                        return StreamGeometry.Parse("M " + pts.Trim() + " Z");
                    }
                    case "polyline":
                    {
                        string? pts = el.Attribute("points")?.Value;
                        if (string.IsNullOrWhiteSpace(pts)) return null;
                        return StreamGeometry.Parse("M " + pts.Trim());
                    }
                    default:
                        return null;
                }
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug($"[FlagIconService] CreateGeometry failed for <{localName}>", ex);
                return null;
            }
        }

        private static IBrush? CreateBrush(string? fill, double opacity)
        {
            if (string.IsNullOrWhiteSpace(fill))
                fill = "#000000";

            string trimmed = fill.Trim();
            if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (Color.TryParse(trimmed, out Color color))
            {
                return new SolidColorBrush(color, opacity);
            }

            return null;
        }

        private static IPen? CreatePen(XElement el, double opacity)
        {
            string? stroke = el.Attribute("stroke")?.Value ?? GetStyleValue(el, "stroke");
            if (string.IsNullOrWhiteSpace(stroke) ||
                string.Equals(stroke, "none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!Color.TryParse(stroke.Trim(), out Color strokeColor))
                return null;

            double thickness = 1.0;
            string? sw = el.Attribute("stroke-width")?.Value ?? GetStyleValue(el, "stroke-width");
            if (!string.IsNullOrWhiteSpace(sw))
            {
                string numeric = sw.TrimEnd('p', 'x', 't', '%');
                if (TryParseDouble(numeric, out double parsedSw) && parsedSw > 0)
                    thickness = parsedSw;
            }

            return new Pen(new SolidColorBrush(strokeColor, opacity), thickness);
        }

        private static string? GetStyleValue(XElement el, string propName)
        {
            string? style = el.Attribute("style")?.Value;
            if (string.IsNullOrWhiteSpace(style))
                return null;

            foreach (string part in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = part.IndexOf(':');
                if (colon > 0)
                {
                    string k = part[..colon].Trim();
                    if (string.Equals(k, propName, StringComparison.OrdinalIgnoreCase))
                        return part[(colon + 1)..].Trim();
                }
            }

            return null;
        }

        internal static Matrix ParseTransformForTesting(string transform) => ParseTransform(transform);

        private static Matrix ParseTransform(string transform)
        {
            Matrix result = Matrix.Identity;
            var matches = TransformFunctionRegex().Matches(transform);
            Span<Range> argRanges = stackalloc Range[6];
            Span<double> args = stackalloc double[6];

            foreach (Match m in matches)
            {
                ReadOnlySpan<char> fn = m.Groups[1].ValueSpan;
                ReadOnlySpan<char> rawArgs = m.Groups[2].ValueSpan;
                int rangeCount = rawArgs.SplitAny(argRanges, " ,\t", StringSplitOptions.RemoveEmptyEntries);
                if (rangeCount == 0) continue;

                int argCount = 0;
                for (int i = 0; i < rangeCount && argCount < 6; i++)
                {
                    if (double.TryParse(rawArgs[argRanges[i]], NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                    {
                        args[argCount++] = val;
                    }
                    else
                    {
                        argCount = 0;
                        break;
                    }
                }

                if (argCount == 0) continue;

                Matrix next = Matrix.Identity;
                if (fn.Equals("translate", StringComparison.OrdinalIgnoreCase))
                {
                    double tx = args[0];
                    double ty = argCount > 1 ? args[1] : 0;
                    next = Matrix.CreateTranslation(tx, ty);
                }
                else if (fn.Equals("scale", StringComparison.OrdinalIgnoreCase))
                {
                    double sx = args[0];
                    double sy = argCount > 1 ? args[1] : sx;
                    next = Matrix.CreateScale(sx, sy);
                }
                else if (fn.Equals("rotate", StringComparison.OrdinalIgnoreCase))
                {
                    double rad = args[0] * Math.PI / 180.0;
                    if (argCount >= 3)
                    {
                        double cx = args[1];
                        double cy = args[2];
                        next = Matrix.CreateTranslation(-cx, -cy) * Matrix.CreateRotation(rad) * Matrix.CreateTranslation(cx, cy);
                    }
                    else
                    {
                        next = Matrix.CreateRotation(rad);
                    }
                }
                else if (fn.Equals("matrix", StringComparison.OrdinalIgnoreCase) && argCount >= 6)
                {
                    next = new Matrix(args[0], args[1], args[2], args[3], args[4], args[5]);
                }

                result = next * result;
            }

            return result;
        }

        private static bool TryParseDouble(string? s, out double value)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                value = 0;
                return false;
            }

            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
