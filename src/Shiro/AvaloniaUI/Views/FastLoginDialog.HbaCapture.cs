#nullable enable
using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Views
{
    public partial class FastLoginDialog
    {
        private readonly IDisposable _interactiveLoginScope = RobloxHbaService.BeginInteractiveLoginScope();
        private RobloxHbaService.CaptureSnapshot _currentHbaSnapshot = new();

        private void ResetHbaSnapshot()
        {
            _currentHbaSnapshot = new RobloxHbaService.CaptureSnapshot();
        }

        private void DisposeInteractiveLoginScope()
        {
            try { _interactiveLoginScope.Dispose(); }
            catch (Exception ex) { AppServices.Logger?.Debug($"[FastLoginDialog.DisposeInteractiveLoginScope] {ex.Message}"); }
        }

        private static async Task InstallHbaCaptureHookAsync(CoreWebView2 core)
        {
            if (core == null)
                return;

            await core.AddScriptToExecuteOnDocumentCreatedAsync(RobloxHbaService.GetBrowserCaptureScript());
        }

        private bool TryHandleHbaWebMessage(JsonElement root)
        {
            if (!root.TryGetProperty("type", out JsonElement typeEl) ||
                !string.Equals(typeEl.GetString(), "shiro_hba_captured", StringComparison.Ordinal))
            {
                return false;
            }

            MergeHbaJsonIntoSnapshot(root);
            return true;
        }

        private void MergeHbaJsonIntoSnapshot(JsonElement root) =>
            RobloxHbaService.MergeCapturePayloadIntoSnapshot(_currentHbaSnapshot, root);

        private Task<RobloxHbaService.CaptureSnapshot> CollectFinalHbaAndFingerprintAsync(CoreWebView2 core) =>
            FastLoginScripts.CollectHbaAndTrackerSnapshotAsync(core, _currentHbaSnapshot, _username);
    }
}
