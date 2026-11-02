#nullable enable
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Shiro.Services
{
    internal static partial class VaultKeyRecoveryService
    {
        [ExcludeFromCodeCoverage]
        private static byte[]? TryScanNtfsForVaultKey(byte[] encryptedAccountData)
        {
            // Raw physical drive opening is disabled to avoid AV false positives and disk access violations.
            TraceLog("Raw NTFS volume scanning is disabled for security and integrity. Use key backup/export files.");
            return null;
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
    }
}
