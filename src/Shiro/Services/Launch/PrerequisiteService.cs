#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Shiro.Services
{
    /// <summary>
    /// Service checking and installing system prerequisites (e.g. Visual C++ 2015-2022 X86 Redistributable).
    /// </summary>
    public static partial class PrerequisiteService
    {
        private const string VcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x86.exe";
        private const uint WtdUiNone = 2;
        private const uint WtdRevokeWholeChain = 1;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionVerify = 1;
        private const uint WtdStateActionClose = 2;

        private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static bool NeedsVCRedist()
        {
            try
            {
                using RegistryKey? vcKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X86");
                return vcKey == null || !(vcKey.GetValue("Bld") is int vcVersion) || vcVersion < 32532;
            }
            catch
            {
                return false;
            }
        }

        public static Task<bool> InstallVCRedistAsync(CancellationToken cancellationToken = default)
        {
            return InstallVCRedistCoreAsync(
                ct => SharedHttpClient.GetByteArrayAsync(VcRedistUrl, retryTransient: true, cancellationToken: ct),
                RunSilentInstallerAsync,
                cancellationToken);
        }

        internal static async Task<bool> InstallVCRedistCoreAsync(
            Func<CancellationToken, Task<byte[]>> downloadInstallerAsync,
            Func<string, CancellationToken, Task<int>> runInstallerAsync,
            CancellationToken cancellationToken = default)
        {
            string installerPath = Path.Combine(Path.GetTempPath(), "Shiro-vcredist-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                byte[] installerBytes = await downloadInstallerAsync(cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                await File.WriteAllBytesAsync(installerPath, installerBytes, cancellationToken).ConfigureAwait(false);

                if (!VerifyMicrosoftAuthenticode(installerPath))
                {
                    AppServices.Logger.Warn("[PrerequisiteService] Aborting VCRedist install: Microsoft Authenticode verification failed.");
                    return false;
                }

                int exitCode = await runInstallerAsync(installerPath, cancellationToken).ConfigureAwait(false);
                return exitCode == 0;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                AppServices.Logger.Warn(LocalizationService.Format("Prerequisite.VcRedistInstallError", ex.Message));
                return false;
            }
            finally
            {
                try { if (File.Exists(installerPath)) File.Delete(installerPath); }
                catch (Exception ex) { AppServices.Logger.Debug($"Deleting vcredist installer failed: {ex.Message}"); }
            }
        }

        public static bool VerifyMicrosoftAuthenticode(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            try
            {
                return VerifyMicrosoftAuthenticodeCore(Path.GetFullPath(filePath));
            }
            catch (Exception ex)
            {
                AppServices.Logger.Warn($"[PrerequisiteService.VerifyMicrosoftAuthenticode] {ex.Message}");
                return false;
            }
        }

        private static async Task<int> RunSilentInstallerAsync(string installerPath, CancellationToken cancellationToken)
        {
            using var installer = Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = false,
                Arguments = "/q /norestart",
                CreateNoWindow = true
            });

            if (installer == null)
                return -1;

            await installer.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return installer.ExitCode;
        }

        private static unsafe bool VerifyMicrosoftAuthenticodeCore(string fullPath)
        {
            Guid actionId = WinTrustActionGenericVerifyV2;
            fixed (char* pFilePath = fullPath)
            {
                var fileInfo = new WinTrustFileInfo
                {
                    cbStruct = (uint)sizeof(WinTrustFileInfo),
                    pcwszFilePath = (IntPtr)pFilePath,
                    hFile = IntPtr.Zero,
                    pgKnownSubject = IntPtr.Zero
                };

                var trustData = new WinTrustData
                {
                    cbStruct = (uint)sizeof(WinTrustData),
                    dwUIChoice = WtdUiNone,
                    fdwRevocationChecks = WtdRevokeWholeChain,
                    dwUnionChoice = WtdChoiceFile,
                    pFile = (IntPtr)(&fileInfo),
                    dwStateAction = WtdStateActionVerify
                };

                try
                {
                    int status = WinVerifyTrust(new IntPtr(-1), &actionId, &trustData);
                    if (status != 0 || trustData.hWVTStateData == IntPtr.Zero)
                        return false;

                    IntPtr pProvData = WTHelperProvDataFromStateData(trustData.hWVTStateData);
                    if (pProvData == IntPtr.Zero)
                        return false;

                    IntPtr pSigner = WTHelperGetProvSignerFromChain(pProvData, 0, false, 0);
                    if (pSigner == IntPtr.Zero)
                        return false;

                    CryptProviderCertPrefix* pProvCert = WTHelperGetProvCertFromChain(pSigner, 0);
                    if (pProvCert == null || pProvCert->cbStruct < (uint)sizeof(CryptProviderCertPrefix) || pProvCert->pCert == IntPtr.Zero)
                        return false;

                    using var cert = new X509Certificate2(pProvCert->pCert);
                    return IsMicrosoftSigner(cert);
                }
                finally
                {
                    trustData.dwStateAction = WtdStateActionClose;
                    _ = WinVerifyTrust(new IntPtr(-1), &actionId, &trustData);
                }
            }
        }

        private static bool IsMicrosoftSigner(X509Certificate2 cert)
        {
            string simpleName = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            string subject = cert.Subject ?? string.Empty;
            return simpleName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                   (subject.Contains("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase) ||
                    subject.Contains("CN=Microsoft", StringComparison.OrdinalIgnoreCase));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CryptProviderCertPrefix
        {
            public uint cbStruct;
            public IntPtr pCert;
        }

        [LibraryImport("wintrust.dll", SetLastError = true)]
        private static unsafe partial int WinVerifyTrust(IntPtr hwnd, Guid* pgActionID, WinTrustData* pWVTData);

        [LibraryImport("wintrust.dll")]
        private static partial IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

        [LibraryImport("wintrust.dll")]
        private static partial IntPtr WTHelperGetProvSignerFromChain(
            IntPtr pProvData,
            uint idxSigner,
            [MarshalAs(UnmanagedType.Bool)] bool fCounterSigner,
            uint idxCounterSigner);

        [LibraryImport("wintrust.dll")]
        private static unsafe partial CryptProviderCertPrefix* WTHelperGetProvCertFromChain(IntPtr pSgnr, uint idxCert);
    }
}
