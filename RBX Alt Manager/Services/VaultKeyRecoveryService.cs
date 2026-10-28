#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Shiro.Classes;

namespace Shiro.Services
{
    internal static partial class VaultKeyRecoveryService
    {
        private static readonly byte[] DpapiKeyHeader = Encoding.ASCII.GetBytes("SHIRO_DPAPI_KEY_V1");
        private static readonly byte[] DpapiKeyEntropy = Encoding.ASCII.GetBytes("Shiro.VaultKey.v1");
        private static readonly byte[] VaultKeyUtf16 = Encoding.Unicode.GetBytes("vault.key");

        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint FILE_SHARE_DELETE = 0x00000004;
        private const uint OPEN_EXISTING = 3;
        private const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;

        [ExcludeFromCodeCoverage]
        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [ExcludeFromCodeCoverage]
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            Span<byte> lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [ExcludeFromCodeCoverage]
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetFilePointerEx(
            SafeFileHandle hFile,
            long liDistanceToMove,
            out long lpNewFilePointer,
            uint dwMoveMethod);

        [ExcludeFromCodeCoverage]
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ReadFile(
            SafeFileHandle hFile,
            Span<byte> lpBuffer,
            uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead,
            IntPtr lpOverlapped);

        private static void TraceLog(string message)
        {
            try
            {
                string logFile = Path.Combine(StoragePaths.DataDirectory, "recovery_trace.log");
                File.AppendAllText(logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (Exception) { }
        }

        public static byte[]? TryRecoverInProcess(byte[] encryptedAccountData)
        {
            if (encryptedAccountData == null || !Cryptography.IsEncrypted(encryptedAccountData))
                return null;

            foreach (string candidateFile in GetCandidateKeyFiles())
            {
                try
                {
                    if (!File.Exists(candidateFile))
                        continue;

                    byte[] raw = File.ReadAllBytes(candidateFile);
                    byte[]? unwrapped = TryExtractKeyFromFileBytes(raw);
                    if (unwrapped != null && CanDecrypt(encryptedAccountData, unwrapped))
                    {
                        PersistRecoveredVaultKey(unwrapped);
                        TraceLog($"Recovered valid vault key from '{candidateFile}'.");
                        return unwrapped;
                    }
                }
                catch (Exception ex)
                {
                    TraceLog($"Candidate key file '{candidateFile}' check failed: {ex.Message}");
                }
            }

            if (Program.Elevated && OperatingSystem.IsWindows())
            {
                byte[]? mftRecovered = TryScanNtfsForVaultKey(encryptedAccountData);
                if (mftRecovered != null)
                {
                    PersistRecoveredVaultKey(mftRecovered);
                    return mftRecovered;
                }
            }

            return null;
        }

        [ExcludeFromCodeCoverage]
        public static bool RunElevatedRecovery()
        {
            if (!OperatingSystem.IsWindows())
                return false;

            try
            {
                byte[]? encrypted = GetTargetEncryptedPayload();
                if (encrypted == null || !Cryptography.IsEncrypted(encrypted))
                    return false;

                if (Program.Elevated)
                {
                    byte[]? recovered = TryRecoverInProcess(encrypted);
                    return recovered != null;
                }

                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                    return false;

                var psi = new ProcessStartInfo(exePath)
                {
                    Arguments = "--recover-vault-key",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using Process? proc = Process.Start(psi);
                if (proc == null)
                    return false;

                proc.WaitForExit(120_000);
                if (proc.ExitCode == 0)
                {
                    StoragePaths.ReloadVaultKeyCache();
                    return true;
                }
            }
            catch (Exception ex)
            {
                TraceLog($"RunElevatedRecovery error: {ex.Message}");
            }

            return false;
        }

        private static byte[]? GetTargetEncryptedPayload()
        {
            // Prefer the largest encrypted backup (e.g. the 105-account locked backup) if present
            string dataDir = StoragePaths.DataDirectory;
            var candidates = new List<string>();
            if (Directory.Exists(dataDir))
            {
                candidates.AddRange(Directory.GetFiles(dataDir, "AccountData.json.locked-*.bak"));
            }
            string backupsDir = StoragePaths.BackupsDirectory;
            if (Directory.Exists(backupsDir))
            {
                candidates.AddRange(Directory.GetFiles(backupsDir, "AccountData_*.json.bak"));
            }
            candidates.Add(StoragePaths.AccountDataFile);
            candidates.Add(StoragePaths.AccountDataFile + ".backup");
            candidates.Add(StoragePaths.AccountDataFile + ".bak");

            byte[]? best = null;
            foreach (string file in candidates)
            {
                try
                {
                    if (!File.Exists(file))
                        continue;
                    byte[] bytes = File.ReadAllBytes(file);
                    if (Cryptography.IsEncrypted(bytes) && (best == null || bytes.Length > best.Length))
                    {
                        best = bytes;
                    }
                }
                catch (Exception ex)
                {
                    TraceLog($"Failed reading payload candidate: {ex.Message}");
                }
            }
            return best;
        }

        public static int ExecuteRecoveryCli()
        {
            try
            {
                StoragePaths.EnsureInitialized();
                TraceLog($"ExecuteRecoveryCli started (Elevated={Program.Elevated}).");

                byte[]? encrypted = GetTargetEncryptedPayload();
                if (encrypted == null || !Cryptography.IsEncrypted(encrypted))
                {
                    TraceLog("No encrypted AccountData payload found.");
                    return 2;
                }

                TraceLog($"Target encrypted payload size: {encrypted.Length} bytes.");

                byte[]? recovered = TryRecoverInProcess(encrypted);
                if (recovered == null && OperatingSystem.IsWindows())
                {
                    recovered = TryScanNtfsForVaultKey(encrypted);
                    if (recovered != null)
                        PersistRecoveredVaultKey(recovered);
                }

                if (recovered != null)
                {
                    // Also restore the 105-account encrypted file as primary AccountData.json!
                    StoragePaths.AtomicWriteBytes(StoragePaths.AccountDataFile, encrypted, durable: true);
                    TraceLog("SUCCESS: Vault key recovered and primary AccountData.json restored!");
                    return 0;
                }

                TraceLog("ExecuteRecoveryCli finished: key not found in NTFS metadata.");
                return 1;
            }
            catch (Exception ex)
            {
                TraceLog($"ExecuteRecoveryCli exception: {ex}");
                return 4;
            }
        }

        private static readonly byte[] LegacyDpapiEntropy = new byte[]
        {
            0x52, 0x4f, 0x42, 0x4c, 0x4f, 0x58, 0x20, 0x41, 0x43, 0x43, 0x4f, 0x55, 0x4e, 0x54,
            0x20, 0x4d, 0x41, 0x4e, 0x41, 0x47, 0x45, 0x52, 0x20, 0x7c, 0x20, 0x3a, 0x29, 0x20,
            0x7c, 0x20, 0x42, 0x52, 0x4f, 0x55, 0x47, 0x48, 0x54, 0x20, 0x54, 0x4f, 0x20, 0x59,
            0x4f, 0x55, 0x20, 0x42, 0x55, 0x59, 0x20, 0x69, 0x63, 0x33, 0x77, 0x30, 0x6c, 0x66
        };

        private static IEnumerable<string> GetCandidateKeyFiles()
        {
            string keyFile = StoragePaths.VaultKeyFile;
            yield return keyFile + ".bak";
            yield return keyFile + ".previous";
            yield return keyFile + ".backup";
            yield return Path.Combine(StoragePaths.BaseDirectory, "vault.key");
            yield return Path.Combine(StoragePaths.BaseDirectory, "vault.key.bak");
        }

        internal static byte[]? TryExtractKeyFromFileBytes(byte[] raw)
        {
            if (raw == null || raw.Length == 0)
                return null;

            if (raw.Length > DpapiKeyHeader.Length &&
                raw.AsSpan(0, DpapiKeyHeader.Length).SequenceEqual(DpapiKeyHeader))
            {
                try
                {
                    byte[] blob = raw.AsSpan(DpapiKeyHeader.Length).ToArray();
                    byte[] key = ProtectedData.Unprotect(blob, DpapiKeyEntropy, DataProtectionScope.CurrentUser);
                    if (key != null && key.Length == 32)
                        return key;
                }
                catch (Exception ex)
                {
                    TraceLog($"DPAPI unprotect candidate key failed: {ex.Message}");
                }
            }
            else if (raw.Length == 32)
            {
                return raw;
            }

            return null;
        }

        internal static bool CanDecrypt(byte[] encryptedAccountData, byte[] candidateKey)
        {
            if (candidateKey == null || candidateKey.Length != 32)
                return false;

            try
            {
                byte[] plain = Cryptography.Decrypt(encryptedAccountData, candidateKey);
                return plain != null && plain.Length > 2;
            }
            catch
            {
                return false;
            }
        }

        private static void PersistRecoveredVaultKey(byte[] key)
        {
            StoragePaths.SaveRecoveredVaultKey(key);
        }
    }
}
