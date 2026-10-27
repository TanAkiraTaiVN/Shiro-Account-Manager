using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using RBX_Alt_Manager.Classes;

namespace RBX_Alt_Manager.Services
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

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

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

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetFilePointerEx(
            SafeFileHandle hFile,
            long liDistanceToMove,
            out long lpNewFilePointer,
            uint dwMoveMethod);

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
            catch { }
        }

        public static byte[] TryRecoverInProcess(byte[] encryptedAccountData)
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
                    byte[] unwrapped = TryExtractKeyFromFileBytes(raw);
                    if (unwrapped != null && CanDecrypt(encryptedAccountData, unwrapped))
                    {
                        PersistRecoveredVaultKey(unwrapped);
                        TraceLog($"Recovered valid vault key from '{candidateFile}'.");
                        return unwrapped;
                    }
                }
                catch { }
            }

            if (Program.Elevated && OperatingSystem.IsWindows())
            {
                byte[] mftRecovered = TryScanNtfsForVaultKey(encryptedAccountData);
                if (mftRecovered != null)
                {
                    PersistRecoveredVaultKey(mftRecovered);
                    return mftRecovered;
                }
            }

            return null;
        }

        public static bool RunElevatedRecovery()
        {
            if (!OperatingSystem.IsWindows())
                return false;

            try
            {
                byte[] encrypted = GetTargetEncryptedPayload();
                if (encrypted == null || !Cryptography.IsEncrypted(encrypted))
                    return false;

                if (Program.Elevated)
                {
                    byte[] recovered = TryRecoverInProcess(encrypted);
                    return recovered != null;
                }

                string exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                    return false;

                var psi = new ProcessStartInfo(exePath)
                {
                    Arguments = "--recover-vault-key",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                using Process proc = Process.Start(psi);
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

        private static byte[] GetTargetEncryptedPayload()
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

            byte[] best = null;
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
                catch { }
            }
            return best;
        }

        public static int ExecuteRecoveryCli()
        {
            try
            {
                StoragePaths.EnsureInitialized();
                TraceLog($"ExecuteRecoveryCli started (Elevated={Program.Elevated}).");

                byte[] encrypted = GetTargetEncryptedPayload();
                if (encrypted == null || !Cryptography.IsEncrypted(encrypted))
                {
                    TraceLog("No encrypted AccountData payload found.");
                    return 2;
                }

                TraceLog($"Target encrypted payload size: {encrypted.Length} bytes.");

                byte[] recovered = TryRecoverInProcess(encrypted);
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

        public static int MergeAllKnownAccountsCli()
        {
            try
            {
                StoragePaths.EnsureInitialized();
                byte[] vaultKey = StoragePaths.GetOrCreateVaultKey();
                var byUsername = new Dictionary<string, Account>(StringComparer.OrdinalIgnoreCase);

                void MergeList(IEnumerable<Account> list)
                {
                    if (list == null) return;
                    foreach (var acc in list)
                    {
                        if (acc == null || string.IsNullOrWhiteSpace(acc.Username))
                            continue;

                        string u = acc.Username.Trim();
                        if (!byUsername.TryGetValue(u, out var existing))
                        {
                            byUsername[u] = acc;
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(existing.SecurityToken) && !string.IsNullOrWhiteSpace(acc.SecurityToken))
                            {
                                existing.SecurityToken = acc.SecurityToken;
                                existing.Valid = acc.Valid;
                            }
                            if (string.IsNullOrWhiteSpace(existing.Password) && !string.IsNullOrWhiteSpace(acc.Password))
                                existing.Password = acc.Password;
                            if (existing.UserID <= 0 && acc.UserID > 0)
                                existing.UserID = acc.UserID;
                            if (string.IsNullOrWhiteSpace(existing.Alias) && !string.IsNullOrWhiteSpace(acc.Alias))
                                existing.Alias = acc.Alias;
                        }
                    }
                }

                // 1. Load current AccountData.json
                if (File.Exists(StoragePaths.AccountDataFile))
                {
                    try
                    {
                        byte[] raw = File.ReadAllBytes(StoragePaths.AccountDataFile);
                        if (Cryptography.IsEncrypted(raw))
                        {
                            byte[] dec = Cryptography.Decrypt(raw, vaultKey);
                            MergeList(AccountDataSerializer.Deserialize(dec, out _));
                        }
                    }
                    catch { }
                }

                // 2. Load DPAPI backups
                string[] dpapiFiles =
                {
                    StoragePaths.AccountDataFile + ".previous",
                    StoragePaths.AccountDataFile + ".backup.previous",
                    @"D:\$RECYCLE.BIN\S-1-5-21-3175979420-2019744931-1257509645-1001\$RHJYIIZ.2\AccountData.json",
                    @"D:\$RECYCLE.BIN\S-1-5-21-3175979420-2019744931-1257509645-1001\$RHJYIIZ.2\AccountData.json.backup"
                };

                foreach (string df in dpapiFiles)
                {
                    try
                    {
                        if (!File.Exists(df)) continue;
                        byte[] raw = File.ReadAllBytes(df);
                        byte[] dec = ProtectedData.Unprotect(raw, LegacyDpapiEntropy, DataProtectionScope.CurrentUser);
                        MergeList(AccountDataSerializer.Deserialize(dec, out _));
                    }
                    catch { }
                }

                // 3. Load passwords from Recycle Bin text file if present
                var passwordMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string txtFile = @"D:\$RECYCLE.BIN\S-1-5-21-3175979420-2019744931-1257509645-1001\$RAZH0RO.txt";
                if (File.Exists(txtFile))
                {
                    foreach (string line in File.ReadLines(txtFile))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;
                        ReadOnlySpan<char> trimmed = line.AsSpan().Trim();
                        if (trimmed.IsEmpty || !trimmed.Contains(':'))
                            continue;
                        // Skip lines that have space annotations like "roblox khoá acc"
                        int spaceIdx = trimmed.IndexOf(' ');
                        ReadOnlySpan<char> token = spaceIdx >= 0 ? trimmed.Slice(0, spaceIdx) : trimmed;
                        int colon = token.IndexOf(':');
                        if (colon > 0 && colon < token.Length - 1)
                        {
                            string u = token.Slice(0, colon).Trim().ToString();
                            string p = token.Slice(colon + 1).Trim().ToString();
                            passwordMap[u] = p;
                        }
                    }
                }

                // 4. Resolve all cached avatar UserIDs via Roblox Users API
                string avatarDir = Path.Combine(StoragePaths.CacheDirectory, "Avatars");
                if (Directory.Exists(avatarDir))
                {
                    var userIds = new List<long>();
                    foreach (string png in Directory.EnumerateFiles(avatarDir, "*.png"))
                    {
                        ReadOnlySpan<char> name = Path.GetFileNameWithoutExtension(png.AsSpan());
                        if (long.TryParse(name, out long uid) && uid > 0)
                            userIds.Add(uid);
                    }

                    if (userIds.Count > 0)
                    {
                        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                        foreach (long[] batch in System.Linq.Enumerable.Chunk(userIds, 50))
                        {
                            string reqJson = "{\"userIds\":[" + string.Join(",", batch) + "],\"excludeBannedUsers\":false}";
                            using var content = new System.Net.Http.StringContent(reqJson, Encoding.UTF8, "application/json");
                            using var resp = http.PostAsync("https://users.roblox.com/v1/users", content).GetAwaiter().GetResult();
                            if (resp.IsSuccessStatusCode)
                            {
                                string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                                using var doc = System.Text.Json.JsonDocument.Parse(body);
                                if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    foreach (var item in dataArr.EnumerateArray())
                                    {
                                        long id = item.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : 0;
                                        string uname = item.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;
                                        if (id > 0 && !string.IsNullOrWhiteSpace(uname))
                                        {
                                            passwordMap.TryGetValue(uname, out string pw);
                                            if (string.IsNullOrEmpty(pw) && uname.StartsWith("AD_Jackpot", StringComparison.OrdinalIgnoreCase))
                                                pw = "0346172240a";

                                            MergeList(new[]
                                            {
                                                new Account
                                                {
                                                    Username = uname,
                                                    UserID = id,
                                                    Password = pw ?? string.Empty,
                                                    Group = "Default",
                                                    Valid = false
                                                }
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // Also add any remaining AD_Jackpot accounts from passwordMap
                foreach (var kvp in passwordMap)
                {
                    if (!byUsername.ContainsKey(kvp.Key))
                    {
                        byUsername[kvp.Key] = new Account
                        {
                            Username = kvp.Key,
                            Password = kvp.Value,
                            Group = "Default",
                            Valid = false
                        };
                    }
                }

                var finalList = new List<Account>(byUsername.Values);
                byte[] serialized = AccountDataSerializer.Serialize(finalList, includePasswords: true);
                byte[] encrypted = Cryptography.Encrypt(serialized, vaultKey);
                StoragePaths.AtomicWriteBytes(StoragePaths.AccountDataFile, encrypted, durable: true);
                StoragePaths.AtomicWriteBytes(StoragePaths.AccountDataFile + ".backup", encrypted, durable: true);
                TraceLog($"Merged and saved {finalList.Count} accounts successfully ({encrypted.Length} bytes).");
                return 0;
            }
            catch (Exception ex)
            {
                TraceLog($"Merge error: {ex}");
                return 1;
            }
        }

        private static IEnumerable<string> GetCandidateKeyFiles()
        {
            string keyFile = StoragePaths.VaultKeyFile;
            yield return keyFile + ".bak";
            yield return keyFile + ".previous";
            yield return keyFile + ".backup";
            yield return Path.Combine(StoragePaths.BaseDirectory, "vault.key");
            yield return Path.Combine(StoragePaths.BaseDirectory, "vault.key.bak");
        }

        internal static byte[] TryExtractKeyFromFileBytes(byte[] raw)
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
                catch { }
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

        private static int ReadAligned(SafeFileHandle handle, long byteOffset, byte[] buffer, int count)
        {
            if (!SetFilePointerEx(handle, byteOffset, out _, 0))
                return 0;

            if (!ReadFile(handle, buffer.AsSpan(0, count), (uint)count, out uint bytesRead, IntPtr.Zero))
                return 0;

            return (int)bytesRead;
        }

        private static byte[] TryScanNtfsForVaultKey(byte[] encryptedAccountData)
        {
            try
            {
                string root = Path.GetPathRoot(StoragePaths.DataDirectory);
                if (string.IsNullOrWhiteSpace(root))
                    return null;

                string volumeLetter = root.TrimEnd('\\', '/');
                string devicePath = @"\\.\" + volumeLetter;

                using SafeFileHandle handle = CreateFile(
                    devicePath,
                    GENERIC_READ,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    IntPtr.Zero,
                    OPEN_EXISTING,
                    0,
                    IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    TraceLog($"Could not open {devicePath} (Win32 error {Marshal.GetLastWin32Error()}).");
                    return null;
                }

                Span<byte> volData = stackalloc byte[256];
                if (!DeviceIoControl(handle, FSCTL_GET_NTFS_VOLUME_DATA, IntPtr.Zero, 0, volData, (uint)volData.Length, out uint volBytes, IntPtr.Zero) && volBytes < 96)
                {
                    TraceLog($"FSCTL_GET_NTFS_VOLUME_DATA failed (Win32 error {Marshal.GetLastWin32Error()}, bytes={volBytes}).");
                    return null;
                }

                uint bytesPerCluster = BinaryPrimitives.ReadUInt32LittleEndian(volData.Slice(44, 4));
                uint recordSize = BinaryPrimitives.ReadUInt32LittleEndian(volData.Slice(48, 4));
                long mftValidLength = BinaryPrimitives.ReadInt64LittleEndian(volData.Slice(56, 8));
                long mftStartLcn = BinaryPrimitives.ReadInt64LittleEndian(volData.Slice(64, 8));

                TraceLog($"NTFS Volume: BytesPerCluster={bytesPerCluster}, RecordSize={recordSize}, MftValidLength={mftValidLength}, MftStartLcn={mftStartLcn}");

                if (bytesPerCluster == 0 || recordSize == 0 || recordSize > 8192 || mftStartLcn <= 0)
                    return null;

                long mftByteOffset = mftStartLcn * bytesPerCluster;
                int firstReadSize = (int)Math.Max(bytesPerCluster, recordSize * 4);
                byte[] firstRecords = new byte[firstReadSize];
                int firstRead = ReadAligned(handle, mftByteOffset, firstRecords, firstReadSize);
                if (firstRead < (int)(recordSize * 3))
                {
                    TraceLog($"Failed to read MFT record 0 at offset {mftByteOffset} (read={firstRead}, err={Marshal.GetLastWin32Error()}).");
                    return null;
                }

                var mftRecord0 = new byte[recordSize];
                Buffer.BlockCopy(firstRecords, 0, mftRecord0, 0, (int)recordSize);
                if (!ApplyUsaFixup(mftRecord0, 512))
                {
                    TraceLog("ApplyUsaFixup failed on MFT Record 0.");
                    return null;
                }

                var mftExtents = ParseDataRunExtents(mftRecord0, bytesPerCluster);
                TraceLog($"Parsed {mftExtents.Count} MFT extent(s).");
                if (mftExtents.Count == 0)
                    return null;

                var highPriorityCandidates = new ConcurrentBag<byte[]>();
                var exactDateCandidates = new ConcurrentBag<byte[]>();
                var secondaryCandidates = new ConcurrentBag<byte[]>();
                var seenHex = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

                void AddCandidate(ReadOnlySpan<byte> keySpan, int priority)
                {
                    if (keySpan.Length != 32)
                        return;

                    int zeroBytes = 0;
                    for (int i = 0; i < 32; i++)
                    {
                        if (keySpan[i] == 0)
                            zeroBytes++;
                    }
                    if (zeroBytes > 4)
                        return;

                    byte[] copy = keySpan.ToArray();
                    string hex = Convert.ToHexString(copy);
                    if (seenHex.TryAdd(hex, 0))
                    {
                        if (priority == 0)
                            highPriorityCandidates.Add(copy);
                        else if (priority == 1)
                            exactDateCandidates.Add(copy);
                        else
                            secondaryCandidates.Add(copy);
                    }
                }

                // Target window for original vault.key creation: 2026-10-19 to 2026-10-20
                long oct19Start = new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
                long oct20End = new DateTime(2026, 10, 20, 23, 59, 59, DateTimeKind.Utc).ToFileTimeUtc();
                long oct01Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

                byte[] chunkBuffer = new byte[1024 * 1024]; // 1 MB aligned buffer
                foreach (var (offset, length) in mftExtents)
                {
                    if (offset <= 0 || length <= 0)
                        continue;

                    long currentOffset = offset;
                    long remaining = length;
                    while (remaining >= recordSize)
                    {
                        int toRead = (int)Math.Min(chunkBuffer.Length, remaining);
                        toRead -= (int)(toRead % bytesPerCluster);
                        if (toRead <= 0)
                            break;

                        int read = ReadAligned(handle, currentOffset, chunkBuffer, toRead);
                        if (read < recordSize)
                            break;

                        currentOffset += read;
                        remaining -= read;

                        for (int pos = 0; pos + recordSize <= read; pos += (int)recordSize)
                        {
                            Span<byte> rec = chunkBuffer.AsSpan(pos, (int)recordSize);
                            if (rec[0] != (byte)'F' || rec[1] != (byte)'I' || rec[2] != (byte)'L' || rec[3] != (byte)'E')
                                continue;

                            if (!ApplyUsaFixup(rec, 512))
                                continue;

                            // 1. Check for SHIRO_DPAPI_KEY_V1 header anywhere in the MFT record
                            int dpapiIdx = rec.IndexOf(DpapiKeyHeader);
                            if (dpapiIdx >= 0 && dpapiIdx + DpapiKeyHeader.Length + 32 <= rec.Length)
                            {
                                // Try unprotecting with exact 262-byte blob length or remaining lengths
                                for (int blobLen = 200; blobLen <= 300 && dpapiIdx + DpapiKeyHeader.Length + blobLen <= rec.Length; blobLen += 2)
                                {
                                    try
                                    {
                                        byte[] blob = rec.Slice(dpapiIdx + DpapiKeyHeader.Length, blobLen).ToArray();
                                        byte[] unwrapped = ProtectedData.Unprotect(blob, DpapiKeyEntropy, DataProtectionScope.CurrentUser);
                                        if (unwrapped != null && unwrapped.Length == 32)
                                        {
                                            AddCandidate(unwrapped, priority: 0);
                                            break;
                                        }
                                    }
                                    catch { }
                                }
                            }

                            bool hasVaultKeyName = rec.IndexOf(VaultKeyUtf16) >= 0;

                            // 2. Walk attributes for resident $DATA (0x80) of length 32 or DPAPI header
                            ushort attrOffset = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(20, 2));
                            long cTime = 0;
                            long wTime = 0;

                            while (attrOffset + 24 <= rec.Length)
                            {
                                uint attrType = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(attrOffset, 4));
                                if (attrType == 0xFFFFFFFF || attrType == 0)
                                    break;

                                uint attrLen = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(attrOffset + 4, 4));
                                if (attrLen < 24 || attrOffset + attrLen > rec.Length)
                                    break;

                                byte nonResident = rec[attrOffset + 8];
                                if (nonResident == 0)
                                {
                                    uint valLen = BinaryPrimitives.ReadUInt32LittleEndian(rec.Slice(attrOffset + 16, 4));
                                    ushort valOff = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(attrOffset + 20, 2));

                                    if (attrType == 0x10 && attrOffset + valOff + 16 <= rec.Length)
                                    {
                                        cTime = BinaryPrimitives.ReadInt64LittleEndian(rec.Slice(attrOffset + valOff, 8));
                                        wTime = BinaryPrimitives.ReadInt64LittleEndian(rec.Slice(attrOffset + valOff + 8, 8));
                                    }
                                    else if (attrType == 0x80 && attrOffset + valOff + valLen <= rec.Length)
                                    {
                                        ReadOnlySpan<byte> dataSlice = rec.Slice(attrOffset + valOff, (int)valLen);
                                        if (valLen == 32)
                                        {
                                            if (hasVaultKeyName)
                                                AddCandidate(dataSlice, priority: 0);
                                            else if ((cTime >= oct19Start && cTime <= oct20End) || (wTime >= oct19Start && wTime <= oct20End))
                                                AddCandidate(dataSlice, priority: 1);
                                            else if (cTime >= oct01Start || wTime >= oct01Start)
                                                AddCandidate(dataSlice, priority: 2);
                                        }
                                        else if (valLen > DpapiKeyHeader.Length && dataSlice.Slice(0, DpapiKeyHeader.Length).SequenceEqual(DpapiKeyHeader))
                                        {
                                            try
                                            {
                                                byte[] blob = dataSlice.Slice(DpapiKeyHeader.Length).ToArray();
                                                byte[] unwrapped = ProtectedData.Unprotect(blob, DpapiKeyEntropy, DataProtectionScope.CurrentUser);
                                                if (unwrapped != null && unwrapped.Length == 32)
                                                    AddCandidate(unwrapped, priority: 0);
                                            }
                                            catch { }
                                        }
                                    }
                                }

                                attrOffset += (ushort)attrLen;
                            }
                        }
                    }
                }

                // Also scan $LogFile (Record 2) for SHIRO_DPAPI_KEY_V1
                var logRecord2 = new byte[recordSize];
                Buffer.BlockCopy(firstRecords, (int)(recordSize * 2), logRecord2, 0, (int)recordSize);
                if (ApplyUsaFixup(logRecord2, 512))
                {
                    var logExtents = ParseDataRunExtents(logRecord2, bytesPerCluster);
                    TraceLog($"Parsed {logExtents.Count} $LogFile extent(s).");
                    foreach (var (offset, length) in logExtents)
                    {
                        if (offset <= 0 || length <= 0)
                            continue;

                        long currentOffset = offset;
                        long remaining = length;
                        while (remaining >= 4096)
                        {
                            int toRead = (int)Math.Min(chunkBuffer.Length, remaining);
                            toRead -= (toRead % 4096);
                            if (toRead <= 0)
                                break;

                            int read = ReadAligned(handle, currentOffset, chunkBuffer, toRead);
                            if (read < 4096)
                                break;

                            currentOffset += read;
                            remaining -= read;

                            for (int pos = 0; pos + 4096 <= read; pos += 4096)
                            {
                                Span<byte> page = chunkBuffer.AsSpan(pos, 4096);
                                if (page[0] == (byte)'R' && page[1] == (byte)'C' && page[2] == (byte)'R' && page[3] == (byte)'D')
                                    ApplyUsaFixup(page, 512);

                                int idx = 0;
                                while (idx + 280 <= page.Length)
                                {
                                    int rel = page.Slice(idx).IndexOf(DpapiKeyHeader);
                                    if (rel < 0)
                                        break;

                                    int matchPos = idx + rel;
                                    if (matchPos + 280 <= page.Length)
                                    {
                                        try
                                        {
                                            byte[] blob = page.Slice(matchPos + DpapiKeyHeader.Length, 280 - DpapiKeyHeader.Length).ToArray();
                                            byte[] unwrapped = ProtectedData.Unprotect(blob, DpapiKeyEntropy, DataProtectionScope.CurrentUser);
                                            if (unwrapped != null && unwrapped.Length == 32)
                                                AddCandidate(unwrapped, priority: 0);
                                        }
                                        catch { }
                                    }
                                    idx = matchPos + DpapiKeyHeader.Length;
                                }
                            }
                        }
                    }
                }

                TraceLog($"Scanned NTFS metadata: Priority0={highPriorityCandidates.Count}, Priority1(Oct19-20)={exactDateCandidates.Count}, Priority2={secondaryCandidates.Count}");

                byte[] matchedKey = TestCandidatesParallel(encryptedAccountData, highPriorityCandidates)
                                 ?? TestCandidatesParallel(encryptedAccountData, exactDateCandidates)
                                 ?? TestCandidatesParallel(encryptedAccountData, secondaryCandidates);

                return matchedKey;
            }
            catch (Exception ex)
            {
                TraceLog($"TryScanNtfsForVaultKey exception: {ex}");
                return null;
            }
        }

        private static byte[] TestCandidatesParallel(byte[] encryptedAccountData, IEnumerable<byte[]> candidates)
        {
            byte[] found = null;
            Parallel.ForEach(candidates, (cand, state) =>
            {
                if (CanDecrypt(encryptedAccountData, cand))
                {
                    found = cand;
                    state.Stop();
                }
            });
            return found;
        }

        private static bool ApplyUsaFixup(Span<byte> record, int sectorSize)
        {
            if (record.Length < 48)
                return false;

            ushort usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(4, 2));
            ushort usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(6, 2));

            if (usaOffset < 40 || usaCount < 2 || usaOffset + usaCount * 2 > record.Length)
                return false;

            for (int s = 1; s < usaCount && s * sectorSize <= record.Length; s++)
            {
                int sectorEnd = s * sectorSize - 2;
                record[sectorEnd] = record[usaOffset + s * 2];
                record[sectorEnd + 1] = record[usaOffset + s * 2 + 1];
            }

            return true;
        }

        private static List<(long ByteOffset, long ByteLength)> ParseDataRunExtents(ReadOnlySpan<byte> record, uint bytesPerCluster)
        {
            var extents = new List<(long, long)>();
            ushort attrOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(20, 2));

            while (attrOffset + 24 <= record.Length)
            {
                uint attrType = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(attrOffset, 4));
                if (attrType == 0xFFFFFFFF || attrType == 0)
                    break;

                uint attrLen = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(attrOffset + 4, 4));
                if (attrLen < 24 || attrOffset + attrLen > recLength(record))
                    break;

                byte nonResident = record[attrOffset + 8];
                if (attrType == 0x80 && nonResident == 1 && attrOffset + 34 <= record.Length)
                {
                    ushort runOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(attrOffset + 32, 2));
                    int pos = attrOffset + runOffset;
                    long currentLcn = 0;

                    while (pos < attrOffset + attrLen && pos < record.Length && record[pos] != 0)
                    {
                        byte header = record[pos++];
                        int lenBytes = header & 0x0F;
                        int offBytes = (header >> 4) & 0x0F;

                        if (lenBytes == 0 || lenBytes > 8 || offBytes > 8 || pos + lenBytes + offBytes > record.Length)
                            break;

                        long clusterCount = 0;
                        for (int i = 0; i < lenBytes; i++)
                            clusterCount |= ((long)record[pos + i]) << (i * 8);
                        pos += lenBytes;

                        long lcnDelta = 0;
                        if (offBytes > 0)
                        {
                            for (int i = 0; i < offBytes; i++)
                                lcnDelta |= ((long)record[pos + i]) << (i * 8);

                            if ((record[pos + offBytes - 1] & 0x80) != 0)
                            {
                                for (int i = offBytes; i < 8; i++)
                                    lcnDelta |= 0xFFL << (i * 8);
                            }
                            pos += offBytes;

                            currentLcn += lcnDelta;
                            if (currentLcn > 0 && clusterCount > 0)
                            {
                                extents.Add((currentLcn * bytesPerCluster, clusterCount * bytesPerCluster));
                            }
                        }
                    }
                    break;
                }

                attrOffset += (ushort)attrLen;
            }

            return extents;

            static int recLength(ReadOnlySpan<byte> r) => r.Length;
        }
    }
}
