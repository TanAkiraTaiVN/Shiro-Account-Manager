#nullable enable
using System;
using System.IO;
using System.IO.Compression;

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
}
