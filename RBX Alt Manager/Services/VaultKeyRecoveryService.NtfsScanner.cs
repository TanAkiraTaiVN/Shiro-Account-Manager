#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace RBX_Alt_Manager.Services
{
    internal static partial class VaultKeyRecoveryService
    {
        [ExcludeFromCodeCoverage]
        private static int ReadAligned(SafeFileHandle handle, long byteOffset, byte[] buffer, int count)
        {
            if (!SetFilePointerEx(handle, byteOffset, out _, 0))
                return 0;

            if (!ReadFile(handle, buffer.AsSpan(0, count), (uint)count, out uint bytesRead, IntPtr.Zero))
                return 0;

            return (int)bytesRead;
        }

        [ExcludeFromCodeCoverage]
        private static byte[]? TryScanNtfsForVaultKey(byte[] encryptedAccountData)
        {
            try
            {
                string? root = Path.GetPathRoot(StoragePaths.DataDirectory);
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
                                    catch (CryptographicException)
                                    {
                                        // Expected during sector scan
                                    }
                                    catch (Exception ex)
                                    {
                                        TraceLog($"Sector DPAPI scan error: {ex.Message}");
                                    }
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
                                            catch (CryptographicException)
                                            {
                                                // Expected during sector scan
                                            }
                                            catch (Exception ex)
                                            {
                                                TraceLog($"Sector DPAPI scan error: {ex.Message}");
                                            }
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
                                        catch (CryptographicException)
                                        {
                                            // Expected during sector scan
                                        }
                                        catch (Exception ex)
                                        {
                                            TraceLog($"Sector DPAPI scan error: {ex.Message}");
                                        }
                                    }
                                    idx = matchPos + DpapiKeyHeader.Length;
                                }
                            }
                        }
                    }
                }

                TraceLog($"Scanned NTFS metadata: Priority0={highPriorityCandidates.Count}, Priority1(Oct19-20)={exactDateCandidates.Count}, Priority2={secondaryCandidates.Count}");

                byte[]? matchedKey = TestCandidatesParallel(encryptedAccountData, highPriorityCandidates)
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

        internal static byte[]? TestCandidatesParallel(byte[] encryptedAccountData, IEnumerable<byte[]> candidates)
        {
            byte[]? found = null;
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

        internal static bool ApplyUsaFixup(Span<byte> record, int sectorSize) =>
            Recovery.NtfsMftRecordParser.ApplyUsaFixup(record, sectorSize);

        internal static List<(long ByteOffset, long ByteLength)> ParseDataRunExtents(ReadOnlySpan<byte> record, uint bytesPerCluster) =>
            Recovery.NtfsMftRecordParser.ParseDataRunExtents(record, bytesPerCluster);
    }
}
