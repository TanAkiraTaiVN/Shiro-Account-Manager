#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Shiro.Services.Recovery
{
    /// <summary>
    /// Pure, deterministic parser for NTFS Master File Table (MFT) records and data runs.
    /// Completely separated from Win32 P/Invoke disk handles for testability.
    /// </summary>
    internal static class NtfsMftRecordParser
    {
        /// <summary>
        /// Validates USA (Update Sequence Array) fixup of an NTFS FILE record in memory
        /// and restores the actual end-of-sector bytes replaced by USA signatures.
        /// </summary>
        public static bool ApplyUsaFixup(Span<byte> record, int sectorSize)
        {
            if (record.Length < 48 || sectorSize <= 2)
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

        /// <summary>
        /// Parses the data run list of an NTFS non-resident $DATA (0x80) attribute
        /// and converts them into physical (ByteOffset, ByteLength) extents.
        /// Handles variable length byte counts, signed delta LCNs, and sparse files.
        /// </summary>
        public static List<(long ByteOffset, long ByteLength)> ParseDataRunExtents(ReadOnlySpan<byte> record, uint bytesPerCluster)
        {
            var extents = new List<(long, long)>();
            if (record.Length < 24 || bytesPerCluster == 0)
                return extents;

            ushort attrOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(20, 2));

            while (attrOffset + 24 <= record.Length)
            {
                uint attrType = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(attrOffset, 4));
                if (attrType == 0xFFFFFFFF || attrType == 0)
                    break;

                uint attrLen = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(attrOffset + 4, 4));
                if (attrLen < 24 || attrOffset + attrLen > record.Length)
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
        }
    }
}
