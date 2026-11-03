#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Shiro.Services
{
    /// <summary>
    /// Service checking and installing system prerequisites (e.g. Visual C++ 2015-2022 X86 Redistributable).
    /// </summary>
    public static class PrerequisiteService
    {
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

        public static async Task<bool> InstallVCRedistAsync(CancellationToken cancellationToken = default)
        {
            string installerPath = Path.Combine(Path.GetTempPath(), "Shiro-vcredist-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                byte[] installerBytes = await SharedHttpClient.GetByteArrayAsync(
                    "https://aka.ms/vs/17/release/vc_redist.x86.exe",
                    retryTransient: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                await File.WriteAllBytesAsync(installerPath, installerBytes, cancellationToken).ConfigureAwait(false);

                using var installer = Process.Start(new ProcessStartInfo(installerPath)
                {
                    UseShellExecute = false,
                    Arguments = "/q /norestart",
                    CreateNoWindow = true
                });

                if (installer == null) return false;

                await installer.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return installer.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Program.Logger?.Warn(Shiro.Services.LocalizationService.Format("Auto_PrerequisiteServ_249", ex.Message));
                return false;
            }
            finally
            {
                try { if (File.Exists(installerPath)) File.Delete(installerPath); }
                catch (Exception ex) { Program.Logger?.Debug($"Deleting vcredist installer failed: {ex.Message}"); }
            }
        }
    }
}
