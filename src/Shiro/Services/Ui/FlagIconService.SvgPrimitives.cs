#nullable enable
using System;
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

        private static IPen? CreatePen(string? stroke, string? strokeWidth, double opacity)
        {
            if (string.IsNullOrWhiteSpace(stroke) ||
                string.Equals(stroke, "none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!Color.TryParse(stroke.Trim(), out Color strokeColor))
                return null;

            double thickness = 1.0;
            if (!string.IsNullOrWhiteSpace(strokeWidth))
            {
                string numeric = strokeWidth.TrimEnd('p', 'x', 't', '%');
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
