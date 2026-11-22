#nullable enable
using System;
using System.IO;
using System.IO.Compression;

namespace Shiro.Services
{
    public static partial class FlagIconService
    {
        private const string EmbeddedFlagsArchiveResourceName = "Shiro.Resources.Flags.zip";
        private static readonly Lazy<byte[]?> _embeddedFlagsArchiveBytes = new(LoadEmbeddedFlagsArchiveBytes);

        internal static Stream? TryOpenFromEmbeddedArchive(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;

            byte[]? archiveBytes = _embeddedFlagsArchiveBytes.Value;
            if (archiveBytes == null || archiveBytes.Length == 0)
                return null;

            string entryName = $"{code.Trim().ToLowerInvariant()}.svg";
            MemoryStream? output = null;
            try
            {
                using var zipStream = new MemoryStream(archiveBytes, writable: false);
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                ZipArchiveEntry? entry = archive.GetEntry(entryName);
                if (entry == null)
                    return null;

                output = new MemoryStream();
                using (Stream entryStream = entry.Open())
                    entryStream.CopyTo(output);
                output.Position = 0;
                MemoryStream result = output;
                output = null;
                return result;
            }
            catch (Exception ex)
            {
                output?.Dispose();
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
    }
}
