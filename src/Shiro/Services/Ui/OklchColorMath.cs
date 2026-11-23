#nullable enable
using System;
using Avalonia.Media;

namespace Shiro.Services
{
    public enum SurfacePreset
    {
        LiquidObsidian = 0,
        MidnightGlass = 1,
        AmoledVoid = 2,
        WarmGraphite = 3
    }

    public readonly record struct SurfacePalette(
        Color SurfaceCanvas,
        Color SurfaceChrome,
        Color SurfacePanel,
        Color SurfaceCard,
        Color SurfaceCardHover,
        Color SurfaceElevated,
        Color SurfaceInput,
        Color SurfaceInputFocus,
        Color SurfaceSubtleChip,
        Color SurfaceCardGradientTop,
        Color SurfaceCardGradientBottom,
        Color BorderHairline,
        Color BorderSubtle,
        Color BorderHover,
        Color BorderStrong,
        Color TextPrimary,
        Color TextSecondary,
        Color TextMuted,
        Color TextDisabled);

    /// <summary>
    /// Perceptual OKLCH color conversion (Ottosson OKLab), APCA W3 0.0.98G-4g contrast scoring,
    /// contrast-gated foreground selection, and surface ladder derivation.
    /// </summary>
    public static class OklchColorMath
    {
        private static readonly Color DarkForeground = Color.FromRgb(9, 9, 11);
        private static readonly Color LightForeground = Color.FromRgb(245, 245, 248);

        public static Color ToColor(double l, double c, double h, byte alpha = 255)
        {
            double clampedL = Math.Clamp(l, 0.0, 1.0);
            if (clampedL <= 0.0)
                return Color.FromArgb(alpha, 0, 0, 0);
            if (clampedL >= 1.0 && c <= 1e-4)
                return Color.FromArgb(alpha, 255, 255, 255);

            double nonNegC = Math.Max(0.0, c);
            double normH = NormalizeHue(h);
            double hRad = normH * (Math.PI / 180.0);
            double cosH = Math.Cos(hRad);
            double sinH = Math.Sin(hRad);

            var (rLin, gLin, bLin) = OklabToLinearSrgb(clampedL, nonNegC * cosH, nonNegC * sinH);
            if (!IsInLinearSrgbGamut(rLin, gLin, bLin, 1e-4))
            {
                // Bisect chroma inside OKLCH to stay inside sRGB gamut while preserving L and H.
                double low = 0.0;
                double high = nonNegC;
                for (int i = 0; i < 16; i++)
                {
                    double mid = 0.5 * (low + high);
                    var probe = OklabToLinearSrgb(clampedL, mid * cosH, mid * sinH);
                    if (IsInLinearSrgbGamut(probe.R, probe.G, probe.B, 0.0))
                        low = mid;
                    else
                        high = mid;
                }
                (rLin, gLin, bLin) = OklabToLinearSrgb(clampedL, low * cosH, low * sinH);
            }

            return Color.FromArgb(
                alpha,
                LinearToSrgbByte(rLin),
                LinearToSrgbByte(gLin),
                LinearToSrgbByte(bLin));
        }

        public static (double L, double C, double H) FromColor(Color srgb)
        {
            double r = SrgbByteToLinear(srgb.R);
            double g = SrgbByteToLinear(srgb.G);
            double b = SrgbByteToLinear(srgb.B);

            double lmsL = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
            double lmsM = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
            double lmsS = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b;

            double l_ = Math.Cbrt(lmsL);
            double m_ = Math.Cbrt(lmsM);
            double s_ = Math.Cbrt(lmsS);

            double labL = 0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_;
            double labA = 1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_;
            double labB = 0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_;

            double chroma = Math.Sqrt(labA * labA + labB * labB);
            double hue = chroma < 1e-7 ? 0.0 : NormalizeHue(Math.Atan2(labB, labA) * (180.0 / Math.PI));
            return (labL, chroma, hue);
        }

        public static double CalculateApcaContrast(Color text, Color background)
        {
            Color effectiveText = text.A < 255 ? AlphaBlendOver(text, background) : text;
            double txtY = SoftClampApcaLuminance(ApcaLuminance(effectiveText));
            double bgY = SoftClampApcaLuminance(ApcaLuminance(background));

            if (Math.Abs(bgY - txtY) < 0.0005)
                return 0.0;

            double sapc;
            double outputContrast;
            if (bgY > txtY)
            {
                sapc = (Math.Pow(bgY, 0.56) - Math.Pow(txtY, 0.57)) * 1.14;
                outputContrast = sapc < 0.1 ? 0.0 : sapc - 0.027;
            }
            else
            {
                sapc = (Math.Pow(bgY, 0.65) - Math.Pow(txtY, 0.62)) * 1.14;
                outputContrast = sapc > -0.1 ? 0.0 : sapc + 0.027;
            }

            return outputContrast * 100.0;
        }

        public static Color SelectReadableForeground(Color background)
        {
            double darkLc = Math.Abs(CalculateApcaContrast(DarkForeground, background));
            double lightLc = Math.Abs(CalculateApcaContrast(LightForeground, background));
            return darkLc >= lightLc ? DarkForeground : LightForeground;
        }

        public static (Color Background, Color Foreground) EnforceApcaButtonContrast(
            Color background,
            double minAbsLc = 60.0)
        {
            Color initialFg = SelectReadableForeground(background);
            if (Math.Abs(CalculateApcaContrast(initialFg, background)) >= minAbsLc)
                return (background, initialFg);

            var (l, c, h) = FromColor(background);
            double targetLc = minAbsLc + 0.5;

            double lightCandidateL = FindMinLightnessForForeground(l, c, h, DarkForeground, targetLc);
            Color lightCandidate = ToColor(lightCandidateL, c, h, background.A);
            double lightDelta = Math.Abs(lightCandidateL - l);

            double darkCandidateL = FindMaxDarknessForForeground(l, c, h, LightForeground, targetLc);
            Color darkCandidate = ToColor(darkCandidateL, c, h, background.A);
            double darkDelta = Math.Abs(l - darkCandidateL);

            bool lightValid = Math.Abs(CalculateApcaContrast(DarkForeground, lightCandidate)) >= minAbsLc;
            bool darkValid = Math.Abs(CalculateApcaContrast(LightForeground, darkCandidate)) >= minAbsLc;

            Color chosen = lightValid && (!darkValid || lightDelta <= darkDelta)
                ? lightCandidate
                : darkCandidate;
            return (chosen, SelectReadableForeground(chosen));
        }

        public static SurfacePalette DeriveSurfacePalette(SurfacePreset preset)
        {
            var (h, c, lCanvas, lChrome, lPanel, lCard, lElevated) = preset switch
            {
                SurfacePreset.MidnightGlass => (255.0, 0.024, 0.11, 0.14, 0.17, 0.20, 0.23),
                SurfacePreset.AmoledVoid => (270.0, 0.005, 0.04, 0.07, 0.11, 0.15, 0.19),
                SurfacePreset.WarmGraphite => (65.0, 0.008, 0.12, 0.14, 0.17, 0.20, 0.23),
                _ => (270.0, 0.014, 0.11, 0.13, 0.16, 0.19, 0.22)
            };

            double borderFloor = preset == SurfacePreset.AmoledVoid ? 0.21 : lElevated + 0.02;
            double textC = Math.Min(c, 0.012);

            return new SurfacePalette(
                SurfaceCanvas: ToColor(lCanvas, c, h),
                SurfaceChrome: ToColor(lChrome, c, h),
                SurfacePanel: ToColor(lPanel, c, h),
                SurfaceCard: ToColor(lCard, c, h),
                SurfaceCardHover: ToColor(lCard + 0.025, c, h),
                SurfaceElevated: ToColor(lElevated, c, h),
                SurfaceInput: ToColor(lPanel + 0.008, c, h),
                SurfaceInputFocus: ToColor(lCard + 0.006, c, h),
                SurfaceSubtleChip: ToColor(lElevated + 0.015, c, h),
                SurfaceCardGradientTop: ToColor(lCard + 0.015, c, h),
                SurfaceCardGradientBottom: ToColor(lCard - 0.012, c, h),
                BorderHairline: ToColor(borderFloor, c * 1.1, h),
                BorderSubtle: ToColor(borderFloor + 0.035, c * 1.15, h),
                BorderHover: ToColor(borderFloor + 0.095, c * 1.2, h),
                BorderStrong: ToColor(borderFloor + 0.145, c * 1.25, h),
                TextPrimary: LightForeground,
                TextSecondary: ToColor(0.72, textC, h),
                TextMuted: ToColor(0.55, textC, h),
                TextDisabled: ToColor(0.42, textC * 0.85, h));
        }

        public static AccentThemeService.AccentExtendedPalette DeriveAdaptiveAccentPalette(Color gameColor)
        {
            var (l, c, h) = FromColor(gameColor);
            double clampedL = Math.Clamp(l, 0.55, 0.75);
            double clampedC = c < 0.04 ? 0.08 : Math.Clamp(c, 0.06, 0.16);
            Color candidate = ToColor(clampedL, clampedC, h);
            var (primary, textOnAccent) = EnforceApcaButtonContrast(candidate, 60.0);

            var (pL, pC, pH) = FromColor(primary);
            Color hover = ToColor(Math.Clamp(pL + 0.045, 0.0, 0.96), pC, pH);
            Color subtle = Color.FromArgb(40, primary.R, primary.G, primary.B);
            Color borderCol = Color.FromArgb(120, primary.R, primary.G, primary.B);
            Color glow = Color.FromArgb(90, primary.R, primary.G, primary.B);
            Color softFill = Color.FromArgb(31, primary.R, primary.G, primary.B);
            Color rim = Color.FromArgb(115, hover.R, hover.G, hover.B);
            Color ambient = Color.FromArgb(28, primary.R, primary.G, primary.B);
            Color secondaryRgb = ToColor(0.58, Math.Min(pC, 0.12), NormalizeHue(pH + 35.0));
            Color ambientSecondary = Color.FromArgb(18, secondaryRgb.R, secondaryRgb.G, secondaryRgb.B);

            return new AccentThemeService.AccentExtendedPalette(
                primary,
                hover,
                textOnAccent,
                subtle,
                borderCol,
                glow,
                softFill,
                rim,
                ambient,
                ambientSecondary);
        }

        private static double FindMinLightnessForForeground(
            double startL, double c, double h, Color fg, double targetLc)
        {
            double low = Math.Clamp(startL, 0.0, 0.98);
            double high = 0.98;
            for (int i = 0; i < 16; i++)
            {
                double mid = 0.5 * (low + high);
                Color probe = ToColor(mid, c, h);
                if (Math.Abs(CalculateApcaContrast(fg, probe)) >= targetLc)
                    high = mid;
                else
                    low = mid;
            }
            return high;
        }

        private static double FindMaxDarknessForForeground(
            double startL, double c, double h, Color fg, double targetLc)
        {
            double low = 0.15;
            double high = Math.Clamp(startL, 0.15, 1.0);
            for (int i = 0; i < 16; i++)
            {
                double mid = 0.5 * (low + high);
                Color probe = ToColor(mid, c, h);
                if (Math.Abs(CalculateApcaContrast(fg, probe)) >= targetLc)
                    low = mid;
                else
                    high = mid;
            }
            return low;
        }

        private static (double R, double G, double B) OklabToLinearSrgb(double l, double a, double b)
        {
            double l_ = l + 0.3963377774 * a + 0.2158037573 * b;
            double m_ = l - 0.1055613458 * a - 0.0638541728 * b;
            double s_ = l - 0.0894841775 * a - 1.2914855480 * b;

            double l3 = l_ * l_ * l_;
            double m3 = m_ * m_ * m_;
            double s3 = s_ * s_ * s_;

            double rLin = +4.0767416621 * l3 - 3.3077115913 * m3 + 0.2309699292 * s3;
            double gLin = -1.2684380046 * l3 + 2.6097574011 * m3 - 0.3413193965 * s3;
            double bLin = -0.0041960863 * l3 - 0.7034186147 * m3 + 1.7076147010 * s3;
            return (rLin, gLin, bLin);
        }

        private static bool IsInLinearSrgbGamut(double r, double g, double b, double tolerance) =>
            r >= -tolerance && r <= 1.0 + tolerance &&
            g >= -tolerance && g <= 1.0 + tolerance &&
            b >= -tolerance && b <= 1.0 + tolerance;

        private static double SrgbByteToLinear(byte ch)
        {
            double s = ch / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        private static byte LinearToSrgbByte(double linear)
        {
            double clamped = Math.Clamp(linear, 0.0, 1.0);
            double srgb = clamped <= 0.0031308
                ? 12.92 * clamped
                : 1.055 * Math.Pow(clamped, 1.0 / 2.4) - 0.055;
            return (byte)Math.Clamp((int)Math.Round(srgb * 255.0), 0, 255);
        }

        private static double ApcaLuminance(Color c) =>
            0.2126729 * Math.Pow(c.R / 255.0, 2.4) +
            0.7151522 * Math.Pow(c.G / 255.0, 2.4) +
            0.0721750 * Math.Pow(c.B / 255.0, 2.4);

        private static double SoftClampApcaLuminance(double y)
        {
            if (y <= 0.0) return Math.Pow(0.022, 1.414);
            return y > 0.022 ? y : y + Math.Pow(0.022 - y, 1.414);
        }

        private static Color AlphaBlendOver(Color fg, Color bg)
        {
            double a = fg.A / 255.0;
            double inv = 1.0 - a;
            return Color.FromRgb(
                (byte)Math.Clamp((int)Math.Round(fg.R * a + bg.R * inv), 0, 255),
                (byte)Math.Clamp((int)Math.Round(fg.G * a + bg.G * inv), 0, 255),
                (byte)Math.Clamp((int)Math.Round(fg.B * a + bg.B * inv), 0, 255));
        }

        private static double NormalizeHue(double h)
        {
            double rem = h % 360.0;
            return rem < 0.0 ? rem + 360.0 : rem;
        }
    }
}
