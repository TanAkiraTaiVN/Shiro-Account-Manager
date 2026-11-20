#nullable enable
using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;

namespace Shiro.Services
{
    public static partial class FlagIconService
    {
        private static IBrush? CreateGradientBrush(
            XElement gradEl,
            double opacity,
            IReadOnlyDictionary<string, XElement> idMap)
        {
            string localName = gradEl.Name.LocalName;
            if (localName != "linearGradient" && localName != "radialGradient")
                return null;

            var chain = new List<XElement>(4);
            var visited = new HashSet<XElement>();
            XElement? cursor = gradEl;
            while (cursor != null && chain.Count < MaxUseDepth && visited.Add(cursor))
            {
                chain.Add(cursor);
                string? href = cursor.Attribute("href")?.Value ?? cursor.Attribute(XlinkNs + "href")?.Value;
                if (string.IsNullOrWhiteSpace(href))
                    break;
                string parentId = href.TrimStart('#').Trim();
                if (string.IsNullOrEmpty(parentId) || !idMap.TryGetValue(parentId, out cursor))
                    break;
            }

            GradientStops? stops = BuildGradientStops(chain, opacity);
            if (stops == null || stops.Count == 0)
                return null;

            bool isUserSpace = string.Equals(
                GetGradientAttr(chain, "gradientUnits")?.Trim(),
                "userSpaceOnUse",
                StringComparison.OrdinalIgnoreCase);
            RelativeUnit unit = isUserSpace ? RelativeUnit.Absolute : RelativeUnit.Relative;

            GradientSpreadMethod spread = GetGradientAttr(chain, "spreadMethod")?.Trim().ToLowerInvariant() switch
            {
                "reflect" => GradientSpreadMethod.Reflect,
                "repeat" => GradientSpreadMethod.Repeat,
                _ => GradientSpreadMethod.Pad
            };

            GradientBrush brush;
            if (localName == "linearGradient")
            {
                double x1 = ParseGradientCoord(GetGradientAttr(chain, "x1"), 0.0);
                double y1 = ParseGradientCoord(GetGradientAttr(chain, "y1"), 0.0);
                double x2 = ParseGradientCoord(GetGradientAttr(chain, "x2"), 1.0);
                double y2 = ParseGradientCoord(GetGradientAttr(chain, "y2"), 0.0);
                brush = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(x1, y1, unit),
                    EndPoint = new RelativePoint(x2, y2, unit),
                    SpreadMethod = spread,
                    GradientStops = stops
                };
            }
            else
            {
                double cx = ParseGradientCoord(GetGradientAttr(chain, "cx"), 0.5);
                double cy = ParseGradientCoord(GetGradientAttr(chain, "cy"), 0.5);
                double r = ParseGradientCoord(GetGradientAttr(chain, "r"), 0.5);
                double fx = ParseGradientCoord(GetGradientAttr(chain, "fx"), cx);
                double fy = ParseGradientCoord(GetGradientAttr(chain, "fy"), cy);
                brush = new RadialGradientBrush
                {
                    Center = new RelativePoint(cx, cy, unit),
                    GradientOrigin = new RelativePoint(fx, fy, unit),
                    RadiusX = new RelativeScalar(r, unit),
                    RadiusY = new RelativeScalar(r, unit),
                    SpreadMethod = spread,
                    GradientStops = stops
                };
            }

            string? gradTransformAttr = GetGradientAttr(chain, "gradientTransform");
            if (!string.IsNullOrWhiteSpace(gradTransformAttr))
            {
                Matrix m = ParseTransform(gradTransformAttr);
                if (m != Matrix.Identity)
                {
                    brush.Transform = new MatrixTransform(m);
                    brush.TransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
                }
            }

            return brush;
        }

        private static string? GetGradientAttr(List<XElement> chain, string attrName)
        {
            foreach (XElement el in chain)
            {
                string? val = el.Attribute(attrName)?.Value;
                if (!string.IsNullOrWhiteSpace(val))
                    return val;
            }
            return null;
        }

        private static GradientStops? BuildGradientStops(List<XElement> chain, double opacity)
        {
            XElement? stopsOwner = null;
            foreach (XElement candidate in chain)
            {
                foreach (XElement child in candidate.Elements())
                {
                    if (string.Equals(child.Name.LocalName, "stop", StringComparison.Ordinal))
                    {
                        stopsOwner = candidate;
                        break;
                    }
                }
                if (stopsOwner != null)
                    break;
            }

            if (stopsOwner == null)
                return null;

            var stops = new GradientStops();
            foreach (XElement stopEl in stopsOwner.Elements())
            {
                if (!string.Equals(stopEl.Name.LocalName, "stop", StringComparison.Ordinal))
                    continue;

                double offset = Math.Clamp(ParseGradientCoord(stopEl.Attribute("offset")?.Value, 0.0), 0.0, 1.0);
                string? stopColorAttr = stopEl.Attribute("stop-color")?.Value ?? GetStyleValue(stopEl, "stop-color");
                Color stopColor = Colors.Black;
                if (!string.IsNullOrWhiteSpace(stopColorAttr) &&
                    !string.Equals(stopColorAttr.Trim(), "currentColor", StringComparison.OrdinalIgnoreCase) &&
                    Color.TryParse(stopColorAttr.Trim(), out Color parsedColor))
                {
                    stopColor = parsedColor;
                }

                double stopOpacity = 1.0;
                string? stopOpacityAttr = stopEl.Attribute("stop-opacity")?.Value ?? GetStyleValue(stopEl, "stop-opacity");
                if (TryParseDouble(stopOpacityAttr, out double parsedOp))
                    stopOpacity = Math.Clamp(parsedOp, 0.0, 1.0);

                double combinedAlpha = Math.Clamp(stopOpacity * opacity, 0.0, 1.0);
                byte alpha = (byte)Math.Clamp((int)Math.Round(stopColor.A * combinedAlpha), 0, 255);
                stops.Add(new GradientStop(Color.FromArgb(alpha, stopColor.R, stopColor.G, stopColor.B), offset));
            }

            return stops;
        }

        private static double ParseGradientCoord(string? raw, double defaultValue)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return defaultValue;

            string trimmed = raw.Trim();
            if (trimmed.EndsWith('%'))
                return TryParseDouble(trimmed[..^1], out double pct) ? pct / 100.0 : defaultValue;

            return TryParseDouble(trimmed, out double val) ? val : defaultValue;
        }
    }
}
