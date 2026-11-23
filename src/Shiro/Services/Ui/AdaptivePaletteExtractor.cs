#nullable enable
using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Shiro.Services
{
    /// <summary>
    /// Extracts a perceptual dominant accent color from BGRA32 game thumbnail pixel buffers
    /// and Avalonia Bitmap instances using strided OKLCH hue-bucket clustering and safe chroma normalization.
    /// </summary>
    public static class AdaptivePaletteExtractor
    {
        private const int MaxSamples = 4096;
        private const int BucketCount = 36;
        private const int MaxBitmapByteCount = 64 * 1024 * 1024;
        private const double MinAlpha = 128;
        private const double MinChroma = 0.04;
        private const double MinLightness = 0.12;
        private const double MaxLightness = 0.94;
        private const double NormalizedLightness = 0.64;
        private const double NormalizedChroma = 0.13;

        public static Color? ExtractAccentFromBitmap(Bitmap? bitmap)
        {
            if (bitmap == null)
                return null;

            byte[]? bgra32 = TryCopyBgra32Pixels(bitmap, out int width, out int height);
            if (bgra32 == null)
                return null;

            return ExtractAccentFromPixels(bgra32, width, height);
        }

        internal static byte[]? TryCopyBgra32Pixels(Bitmap? bitmap, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (bitmap == null)
                return null;

            PixelSize pixelSize;
            Vector dpi;
            try
            {
                pixelSize = bitmap.PixelSize;
                dpi = bitmap.Dpi;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }

            if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
                return null;

            long requiredLong = (long)pixelSize.Width * pixelSize.Height * 4L;
            if (requiredLong <= 0 || requiredLong > MaxBitmapByteCount)
                return null;

            int byteCount = (int)requiredLong;
            int tightStride = pixelSize.Width * 4;
            byte[] buffer = new byte[byteCount];
            Vector safeDpi = dpi.X > 0.0 && dpi.Y > 0.0 ? dpi : new Vector(96.0, 96.0);

            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = handle.AddrOfPinnedObject();
                try
                {
                    using var target = new LockedFramebuffer(
                        ptr,
                        pixelSize,
                        tightStride,
                        safeDpi,
                        PixelFormats.Bgra8888,
                        AlphaFormat.Unpremul,
                        static () => { });
                    bitmap.CopyPixels(target);
                    width = pixelSize.Width;
                    height = pixelSize.Height;
                    return buffer;
                }
                catch (NotSupportedException)
                {
                    if (TryCopyViaLockOrRawFallback(bitmap, pixelSize, buffer))
                    {
                        width = pixelSize.Width;
                        height = pixelSize.Height;
                        return buffer;
                    }
                    return null;
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                AppServices.Logger?.Debug($"[AdaptivePaletteExtractor] Failed copying bitmap pixels: {ex.Message}");
                return null;
            }
            finally
            {
                if (handle.IsAllocated)
                    handle.Free();
            }
        }

        private static bool TryCopyViaLockOrRawFallback(Bitmap bitmap, PixelSize pixelSize, byte[] destinationBgra32)
        {
            if (bitmap is WriteableBitmap writeable)
            {
                using ILockedFramebuffer locked = writeable.Lock();
                return TryNormalizeFramebufferToBgra32(
                    locked.Address,
                    locked.Size.Width,
                    locked.Size.Height,
                    locked.RowBytes,
                    locked.Format,
                    locked.AlphaFormat,
                    destinationBgra32);
            }

            PixelFormat format = bitmap.Format ?? PixelFormats.Bgra8888;
            AlphaFormat alphaFormat = bitmap.AlphaFormat ?? AlphaFormat.Premul;
            int bytesPerPixel = Math.Max(1, (format.BitsPerPixel + 7) / 8);
            long rawByteCountLong = (long)pixelSize.Width * pixelSize.Height * bytesPerPixel;
            if (rawByteCountLong <= 0 || rawByteCountLong > MaxBitmapByteCount)
                return false;

            int rawStride = pixelSize.Width * bytesPerPixel;
            byte[] rawBuffer = new byte[(int)rawByteCountLong];
            var rawHandle = GCHandle.Alloc(rawBuffer, GCHandleType.Pinned);
            try
            {
                IntPtr rawPtr = rawHandle.AddrOfPinnedObject();
                bitmap.CopyPixels(new PixelRect(0, 0, pixelSize.Width, pixelSize.Height), rawPtr, rawBuffer.Length, rawStride);
                return TryNormalizeFramebufferToBgra32(
                    rawPtr,
                    pixelSize.Width,
                    pixelSize.Height,
                    rawStride,
                    format,
                    alphaFormat,
                    destinationBgra32);
            }
            finally
            {
                if (rawHandle.IsAllocated)
                    rawHandle.Free();
            }
        }

        internal static bool TryNormalizeFramebufferToBgra32(
            IntPtr sourceAddress,
            int width,
            int height,
            int sourceRowBytes,
            PixelFormat format,
            AlphaFormat alphaFormat,
            byte[] destinationBgra32)
        {
            if (sourceAddress == IntPtr.Zero || width <= 0 || height <= 0)
                return false;

            int bytesPerPixel = format.BitsPerPixel switch
            {
                32 => 4,
                24 => 3,
                _ => 0
            };
            if (bytesPerPixel == 0)
                return false;

            long minRowBytesLong = (long)width * bytesPerPixel;
            long destRequiredBytes = (long)width * height * 4L;
            if (minRowBytesLong > int.MaxValue || sourceRowBytes < minRowBytesLong || destinationBgra32.Length < destRequiredBytes)
                return false;

            int minRowBytes = (int)minRowBytesLong;

            bool swapRedBlue = format == PixelFormats.Rgba8888 || format == PixelFormats.Rgb32 || format == PixelFormats.Rgb24;
            bool forceOpaque = alphaFormat == AlphaFormat.Opaque || bytesPerPixel == 3 || format == PixelFormats.Bgr32 || format == PixelFormats.Rgb32;
            bool unpremul = !forceOpaque && alphaFormat == AlphaFormat.Premul;

            byte[] rowScratch = new byte[minRowBytes];
            for (int y = 0; y < height; y++)
            {
                IntPtr rowPtr = IntPtr.Add(sourceAddress, y * sourceRowBytes);
                Marshal.Copy(rowPtr, rowScratch, 0, minRowBytes);

                int dstRowOffset = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int srcIdx = x * bytesPerPixel;
                    int dstIdx = dstRowOffset + (x * 4);

                    byte b = swapRedBlue ? rowScratch[srcIdx + 2] : rowScratch[srcIdx];
                    byte g = rowScratch[srcIdx + 1];
                    byte r = swapRedBlue ? rowScratch[srcIdx] : rowScratch[srcIdx + 2];
                    byte a = forceOpaque ? (byte)255 : rowScratch[srcIdx + 3];

                    if (unpremul && a > 0 && a < 255)
                    {
                        b = (byte)Math.Min(255, (b * 255 + (a / 2)) / a);
                        g = (byte)Math.Min(255, (g * 255 + (a / 2)) / a);
                        r = (byte)Math.Min(255, (r * 255 + (a / 2)) / a);
                    }

                    destinationBgra32[dstIdx] = b;
                    destinationBgra32[dstIdx + 1] = g;
                    destinationBgra32[dstIdx + 2] = r;
                    destinationBgra32[dstIdx + 3] = a;
                }
            }

            return true;
        }

        public static Color? ExtractAccentFromPixels(ReadOnlySpan<byte> bgra32, int width, int height)
        {
            if (width <= 0 || height <= 0 || bgra32.IsEmpty)
                return null;

            long totalPixelsLong = (long)width * height;
            long requiredBytes = totalPixelsLong * 4L;
            if (totalPixelsLong > int.MaxValue || bgra32.Length < requiredBytes)
                return null;

            int colStep = 1;
            int rowStep = 1;
            if (totalPixelsLong > MaxSamples)
            {
                double scale = Math.Sqrt((double)MaxSamples / totalPixelsLong);
                int targetCols = Math.Clamp((int)Math.Round(width * scale), 1, width);
                int targetRows = Math.Clamp((int)Math.Round(height * scale), 1, height);
                colStep = Math.Max(1, (width + targetCols - 1) / targetCols);
                rowStep = Math.Max(1, (height + targetRows - 1) / targetRows);
            }

            Span<double> bucketWeights = stackalloc double[BucketCount];
            Span<double> bucketSin = stackalloc double[BucketCount];
            Span<double> bucketCos = stackalloc double[BucketCount];

            for (int y = 0, rowIndex = 0; y < height; y += rowStep, rowIndex++)
            {
                // Stagger starting column phase per sampled row to prevent power-of-2 vertical aliasing.
                int xStart = colStep > 1 ? rowIndex % colStep : 0;
                int rowByteOffset = y * width * 4;
                for (int x = xStart; x < width; x += colStep)
                {
                    int offset = rowByteOffset + (x * 4);
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
