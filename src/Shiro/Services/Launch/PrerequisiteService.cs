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
    /// Service checking and installing system prerequisites (e.g. Visual C++ 2015-2022/v18 X64 Redistributable).
    /// </summary>
    public static partial class PrerequisiteService
    {
        internal const string VcRedistUrl = "https://aka.ms/vs/18/release/vc_redist.x64.exe";
        private const uint WtdUiNone = 2;
        private const uint WtdRevokeWholeChain = 1;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionVerify = 1;
        private const uint WtdStateActionClose = 2;

        private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static bool NeedsVCRedist()
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var vcKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64")
                    ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\X64")
                    ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64");

                if (vcKey != null)
                {
                    object? installed = vcKey.GetValue("Installed");
                    object? major = vcKey.GetValue("Major");
                    if (IsVcRuntimeSatisfied(installed, major))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug("[PrerequisiteService] Check NeedsVCRedist failed", ex);
                return false;
            }
        }

        internal static bool IsVcRuntimeSatisfied(object? installedValue, object? majorValue = null)
        {
            if (!TryGetInt(installedValue, out int installed) || installed != 1)
            {
                return false;
            }

            if (majorValue == null)
            {
                return true;
            }

            return TryGetInt(majorValue, out int major) && major >= 14;
        }

        private static bool TryGetInt(object? value, out int result)
        {
            switch (value)
            {
                case int i:
                    result = i;
                    return true;
                case long l:
                    result = (int)l;
                    return true;
                case string s when int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed):
                    result = parsed;
                    return true;
                default:
                    result = 0;
                    return false;
            }
        }

        public static async Task<bool> PromptAndInstallVCRedistAsync(
            ISettingsService promptsSettings,
            Func<string, string, Task<bool>> confirmAsync,
            Func<bool>? needsVcRedistOverride = null,
            Func<CancellationToken, Task<bool>>? installOverride = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(promptsSettings);
            ArgumentNullException.ThrowIfNull(confirmAsync);

            if (cancellationToken.IsCancellationRequested || promptsSettings.Exists("VCPrompted"))
                return false;

            Func<bool> needsVcRedist = needsVcRedistOverride ?? NeedsVCRedist;
            if (!needsVcRedist())
                return false;

            bool confirmed = await confirmAsync(
                LocalizationService.Get("Prerequisite.VcRedistPromptMessage"),
                LocalizationService.Get("Prerequisite.VcRedistPromptTitle")).ConfigureAwait(false);

            promptsSettings.Set("VCPrompted", "true");
            if (!confirmed)
                return false;

            Func<CancellationToken, Task<bool>> install = installOverride ?? InstallVCRedistAsync;
            return await install(cancellationToken).ConfigureAwait(false);
        }

        public static Task<bool> InstallVCRedistAsync(CancellationToken cancellationToken = default)
        {
            return InstallVCRedistCoreAsync(
                async ct =>
                {
                    using var client = SharedHttpClient.CreateDedicatedClient(TimeSpan.FromMinutes(3));
                    return await client.GetByteArrayAsync(VcRedistUrl, ct).ConfigureAwait(false);
                },
                RunSilentInstallerAsync,
                cancellationToken);
        }

        internal static async Task<bool> InstallVCRedistCoreAsync(
            Func<CancellationToken, Task<byte[]>> downloadInstallerAsync,
            Func<string, CancellationToken, Task<int>> runInstallerAsync,
            CancellationToken cancellationToken = default,
            Func<string, bool>? verifyAuthenticodeOverride = null)
        {
            string installerPath = Path.Combine(Path.GetTempPath(), "Shiro-vcredist-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                byte[] installerBytes = await downloadInstallerAsync(cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                await File.WriteAllBytesAsync(installerPath, installerBytes, cancellationToken).ConfigureAwait(false);

                bool authenticodeValid = verifyAuthenticodeOverride != null
                    ? verifyAuthenticodeOverride(installerPath)
                    : VerifyMicrosoftAuthenticode(installerPath);
                if (!authenticodeValid)
                {
                    AppServices.Logger.Warn("[PrerequisiteService] Aborting VCRedist install: Microsoft Authenticode verification failed.");
                    return false;
                }

                int exitCode = await runInstallerAsync(installerPath, cancellationToken).ConfigureAwait(false);
                if (exitCode == 0 || exitCode == 3010)
                {
                    if (exitCode == 3010)
                        AppServices.Logger.Info("[PrerequisiteService] VCRedist installed; system restart recommended (exit code 3010).");
                    return true;
                }

                return false;
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
            var startInfo = new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Arguments = "/q /norestart",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (!RobloxDirectLauncher.IsCurrentProcessElevated())
                startInfo.Verb = "runas";

            using var installer = Process.Start(startInfo);
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
