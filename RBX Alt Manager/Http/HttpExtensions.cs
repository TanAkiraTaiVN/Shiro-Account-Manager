#nullable enable
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RBX_Alt_Manager.Http
{
    public static class HttpExtensions
    {
        // https://stackoverflow.com/a/46497896
        public static async Task DownloadAsync(
            this HttpClient client,
            string requestUri,
            Stream destination,
            IProgress<float>? progress = null,
            CancellationToken cancellationToken = default)
        {
            using var response = await client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;

            using var download = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            if (progress == null || !contentLength.HasValue || contentLength.Value <= 0)
            {
                await download.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                return;
            }

            var relativeProgress = new Progress<long>(totalBytes => progress.Report((float)totalBytes / contentLength.Value));
            await CopyToAsync(download, destination, 81920, relativeProgress, cancellationToken).ConfigureAwait(false);
            progress.Report(1);
        }

        public static async Task CopyToAsync(
            Stream source,
            Stream destination,
            int bufferSize,
            IProgress<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (!source.CanRead)
                throw new ArgumentException("Has to be readable", nameof(source));
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (!destination.CanWrite)
                throw new ArgumentException("Has to be writable", nameof(destination));
            if (bufferSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(bufferSize));

            byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(bufferSize);
            try
            {
                long totalBytesRead = 0;
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(rented.AsMemory(0, bufferSize), cancellationToken).ConfigureAwait(false)) != 0)
                {
                    await destination.WriteAsync(rented.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                    totalBytesRead += bytesRead;
                    progress?.Report(totalBytesRead);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
