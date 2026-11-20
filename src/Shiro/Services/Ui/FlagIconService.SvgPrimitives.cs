#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;

namespace Shiro.Services
{
    public static partial class FlagIconService
    {
        [GeneratedRegex(@"([a-zA-Z]+)\s*\(([^)]+)\)", RegexOptions.CultureInvariant)]
        private static partial Regex TransformFunctionRegex();

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

        private static IBrush? CreateBrush(
            string? fill,
            double opacity,
            IReadOnlyDictionary<string, XElement>? idMap = null)
        {
            if (string.IsNullOrWhiteSpace(fill))
                fill = "#000000";

            string trimmed = fill.Trim();
            if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase))
                return null;

            if (TryExtractUrlId(trimmed, out string refId))
            {
                if (idMap != null && idMap.TryGetValue(refId, out XElement? gradEl))
                    return CreateGradientBrush(gradEl, opacity, idMap);
                return null;
            }

            if (Color.TryParse(trimmed, out Color color))
                return new SolidColorBrush(color, opacity);

            return null;
        }

        private static IPen? CreatePen(
            string? stroke,
            string? strokeWidth,
            double opacity,
            IReadOnlyDictionary<string, XElement>? idMap = null)
        {
            if (string.IsNullOrWhiteSpace(stroke) ||
                string.Equals(stroke.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            IBrush? strokeBrush = CreateBrush(stroke, opacity, idMap);
            if (strokeBrush == null)
                return null;

            double thickness = 1.0;
            if (!string.IsNullOrWhiteSpace(strokeWidth))
            {
                string numeric = strokeWidth.TrimEnd('p', 'x', 't', '%');
                if (TryParseDouble(numeric, out double parsedSw) && parsedSw > 0)
                    thickness = parsedSw;
            }

            return new Pen(strokeBrush, thickness);
        }

        private static bool TryExtractUrlId(string? value, out string id)
        {
            id = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string trimmed = value.Trim();
            if (!trimmed.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
                return false;

            int closeIdx = trimmed.IndexOf(')');
            if (closeIdx <= 4)
                return false;

            string inner = trimmed[4..closeIdx].Trim();
            if (inner.Length >= 2 &&
                ((inner[0] == '\'' && inner[^1] == '\'') ||
                 (inner[0] == '"' && inner[^1] == '"')))
            {
                inner = inner[1..^1].Trim();
            }

            if (!inner.StartsWith('#'))
                return false;

            id = inner[1..].Trim();
            return !string.IsNullOrEmpty(id);
        }

        private static Geometry? ResolveClipGeometry(string? clipPathAttr, IReadOnlyDictionary<string, XElement>? idMap)
        {
            if (idMap == null || !TryExtractUrlId(clipPathAttr, out string clipId))
                return null;

            if (!idMap.TryGetValue(clipId, out XElement? clipEl) ||
                !string.Equals(clipEl.Name.LocalName, "clipPath", StringComparison.Ordinal))
            {
                return null;
            }

            var geometries = new List<Geometry>();
            CollectClipGeometries(clipEl, Matrix.Identity, idMap, geometries, depth: 0);
            if (geometries.Count == 0)
                return null;

            if (geometries.Count == 1)
                return geometries[0];

            string? clipRule = clipEl.Attribute("clip-rule")?.Value ?? GetStyleValue(clipEl, "clip-rule");
            var group = new GeometryGroup
            {
                FillRule = string.Equals(clipRule, "evenodd", StringComparison.OrdinalIgnoreCase)
                    ? FillRule.EvenOdd
                    : FillRule.NonZero
            };
            foreach (Geometry g in geometries)
                group.Children.Add(g);
            return group;
        }

        private static void CollectClipGeometries(
            XElement parent,
            Matrix currentTransform,
            IReadOnlyDictionary<string, XElement> idMap,
            List<Geometry> output,
            int depth)
        {
            foreach (XElement el in parent.Elements())
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
                    elTransform = ParseTransform(transformAttr) * elTransform;

                if (localName == "g")
                {
                    CollectClipGeometries(el, elTransform, idMap, output, depth);
                    continue;
                }

                Geometry? geom = null;
                if (localName == "use")
                {
                    if (depth >= MaxUseDepth)
                        continue;

                    string? href = el.Attribute("href")?.Value ?? el.Attribute(XlinkNs + "href")?.Value;
                    if (string.IsNullOrWhiteSpace(href))
                        continue;

                    string targetId = href.TrimStart('#').Trim();
                    if (!string.IsNullOrEmpty(targetId) &&
                        idMap.TryGetValue(targetId, out XElement? targetEl) &&
                        !ReferenceEquals(targetEl, el))
                    {
                        geom = CreateGeometry(targetEl, targetEl.Name.LocalName);
                    }
                }
                else
                {
                    geom = CreateGeometry(el, localName);
                }

                if (geom != null)
                {
                    if (elTransform != Matrix.Identity)
                        geom.Transform = new MatrixTransform(elTransform);
                    output.Add(geom);
                }
            }
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
