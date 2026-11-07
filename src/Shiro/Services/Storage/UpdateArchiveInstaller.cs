#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Shiro.Services;

public static class UpdateArchiveInstaller
{
    public static bool IsSafeArchiveEntryPath(string root, string entry, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(entry))
            return false;

        if (Path.IsPathRooted(entry))
            return false;

        string fullRoot = Path.GetFullPath(root);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
            fullRoot += Path.DirectorySeparatorChar;

        string candidate = Path.GetFullPath(Path.Combine(fullRoot, entry));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        resolved = candidate;
        return true;
    }

    public static int ComputeDownloadPercent(long read, long size)
    {
        if (size <= 0)
            return 30;

        int pct = 30 + (int)((read * 40L) / size);
        return Math.Clamp(pct, 30, 70);
    }

    public static bool VerifyFileSha256(string filePath, string? expectedHexSha256)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(expectedHexSha256))
            return false;

        ReadOnlySpan<char> trimmedHex = expectedHexSha256.AsSpan().Trim();
        if (trimmedHex.Length != 64)
            return false;

        foreach (char c in trimmedHex)
        {
            if (!char.IsAsciiHexDigit(c))
                return false;
        }

        if (!File.Exists(filePath))
            return false;

        try
        {
            byte[] expectedBytes = Convert.FromHexString(trimmedHex);
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] actualBytes = SHA256.HashData(stream);
            return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
        }
        catch (Exception ex)
        {
            AppServices.Logger.Warn($"[UpdateArchiveInstaller.VerifyFileSha256] {ex.Message}");
            return false;
        }
    }

    public static string? TryParseSha256Sums(string? sumsText, string assetFileName)
    {
        if (string.IsNullOrWhiteSpace(sumsText) || string.IsNullOrWhiteSpace(assetFileName))
            return null;

        string targetName = ExtractBaseName(assetFileName.Trim());
        if (string.IsNullOrEmpty(targetName))
            return null;

        string[] lines = sumsText.TrimStart('\uFEFF')
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length <= 65 || line[0] == '#')
                continue;

            int wsIndex = -1;
            for (int i = 0; i < line.Length; i++)
            {
                if (char.IsWhiteSpace(line[i]))
                {
                    wsIndex = i;
                    break;
                }
            }

            if (wsIndex != 64)
                continue;

            ReadOnlySpan<char> hashSpan = line.AsSpan(0, 64);
            bool validHex = true;
            foreach (char c in hashSpan)
            {
                if (!char.IsAsciiHexDigit(c))
                {
                    validHex = false;
                    break;
                }
            }

            if (!validHex)
                continue;

            string entryPart = line.Substring(wsIndex).TrimStart();
            if (entryPart.StartsWith('*'))
                entryPart = entryPart.Substring(1).TrimStart();

            string entryBaseName = ExtractBaseName(entryPart.Trim('"'));
            if (string.Equals(entryBaseName, targetName, StringComparison.OrdinalIgnoreCase))
            {
                return hashSpan.ToString().ToLowerInvariant();
            }
        }

        return null;
    }

    public static void Apply(string archive, string staging, string target, string currentExeName)
    {
        Directory.CreateDirectory(staging);

        using (ZipArchive zip = ZipFile.OpenRead(archive))
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                if (!IsSafeArchiveEntryPath(staging, entry.FullName, out string stagedFile))
                    throw new InvalidDataException($"Invalid archive entry path: {entry.FullName}");

                string? stagedParent = Path.GetDirectoryName(stagedFile);
                if (!string.IsNullOrEmpty(stagedParent))
                    Directory.CreateDirectory(stagedParent);

                entry.ExtractToFile(stagedFile, overwrite: true);
            }
        }

        foreach (string stagedFile in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(staging, stagedFile);
            if (string.Equals(relativePath, currentExeName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relativePath, "Auto Update.exe", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!IsSafeArchiveEntryPath(target, relativePath, out string finalDest))
                continue;

            string? destParent = Path.GetDirectoryName(finalDest);
            if (!string.IsNullOrEmpty(destParent))
                Directory.CreateDirectory(destParent);

            File.Copy(stagedFile, finalDest, overwrite: true);
        }

        SafeExecuteHelper.Try(() => File.Delete(archive), "UpdateArchiveInstaller.DeleteArchive");
        SafeExecuteHelper.Try(() => new DirectoryInfo(staging).RecursiveDelete(), "UpdateArchiveInstaller.DeleteStaging");
    }

    private static string ExtractBaseName(string rawPath)
    {
        int slashIdx = rawPath.LastIndexOfAny(new[] { '/', '\\' });
        return slashIdx >= 0 ? rawPath.Substring(slashIdx + 1) : rawPath;
    }
}
