#nullable enable
using System;
using Avalonia.Media;

namespace Shiro.Services
{
    /// <summary>
    /// Extracts a perceptual dominant accent color from BGRA32 game thumbnail pixel buffers
    /// using strided OKLCH hue-bucket clustering and safe chroma normalization.
    /// </summary>
    public static class AdaptivePaletteExtractor
    {
        private const int MaxSamples = 4096;
        private const int BucketCount = 36;
        private const double MinAlpha = 128;
        private const double MinChroma = 0.04;
        private const double MinLightness = 0.12;
        private const double MaxLightness = 0.94;
        private const double NormalizedLightness = 0.64;
        private const double NormalizedChroma = 0.13;

        public static Color? ExtractAccentFromPixels(ReadOnlySpan<byte> bgra32, int width, int height)
        {
            if (width <= 0 || height <= 0 || bgra32.IsEmpty)
                return null;

            long totalPixelsLong = (long)width * height;
            long requiredBytes = totalPixelsLong * 4L;
            if (totalPixelsLong > int.MaxValue || bgra32.Length < requiredBytes)
                return null;

            int totalPixels = (int)totalPixelsLong;
            int stride = Math.Max(1, (totalPixels + MaxSamples - 1) / MaxSamples);

            Span<double> bucketWeights = stackalloc double[BucketCount];
            Span<double> bucketSin = stackalloc double[BucketCount];
            Span<double> bucketCos = stackalloc double[BucketCount];

            for (int p = 0; p < totalPixels; p += stride)
            {
                int offset = p * 4;
                byte b = bgra32[offset];
                byte g = bgra32[offset + 1];
                byte r = bgra32[offset + 2];
                byte a = bgra32[offset + 3];

                if (a < MinAlpha)
                    continue;

                var (l, c, h) = OklchColorMath.FromColor(Color.FromRgb(r, g, b));
                if (c < MinChroma || l < MinLightness || l > MaxLightness)
                    continue;

                int bucket = (int)Math.Floor(h / 10.0) % BucketCount;
                if (bucket < 0)
                    bucket += BucketCount;

                double weight = c * (1.0 - Math.Abs(l - 0.58) * 0.5);
                double hRad = h * (Math.PI / 180.0);

                bucketWeights[bucket] += weight;
                bucketSin[bucket] += weight * Math.Sin(hRad);
                bucketCos[bucket] += weight * Math.Cos(hRad);
            }

            int bestBucket = -1;
            double bestWeight = 0.0;
            for (int i = 0; i < BucketCount; i++)
            {
                if (bucketWeights[i] > bestWeight)
                {
                    bestWeight = bucketWeights[i];
                    bestBucket = i;
                }
            }

            if (bestBucket < 0 || bestWeight <= 0.0)
                return null;

            double sinSum = bucketSin[bestBucket];
            double cosSum = bucketCos[bestBucket];
            int prevBucket = (bestBucket - 1 + BucketCount) % BucketCount;
            int nextBucket = (bestBucket + 1) % BucketCount;
            if (bucketWeights[prevBucket] >= bestWeight * 0.25)
            {
                sinSum += bucketSin[prevBucket];
                cosSum += bucketCos[prevBucket];
            }
            if (bucketWeights[nextBucket] >= bestWeight * 0.25)
            {
                sinSum += bucketSin[nextBucket];
                cosSum += bucketCos[nextBucket];
            }

            double winningHue = Math.Atan2(sinSum, cosSum) * (180.0 / Math.PI);
            if (winningHue < 0.0)
                winningHue += 360.0;

            return OklchColorMath.ToColor(NormalizedLightness, NormalizedChroma, winningHue);
        }
    }
}
