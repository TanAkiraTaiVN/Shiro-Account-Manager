#nullable enable

namespace Shiro.Services
{
    public static partial class RobloxHbaService
    {
        /// <summary>
        /// JavaScript injected at <c>document_start</c> in WebView2 and Chrome sign-in containers
        /// to export Roblox's HBA ECDSA P-256 keypair and calibrate the live BAT signing template.
        /// </summary>
        public static string GetBrowserCaptureScript() =>
            HbaBrowserScripts.BuildCollectionScript();

        /// <summary>
        /// JavaScript injected at <c>document_start</c> when restoring a persisted HBA keypair
        /// into a moderation or recovery WebView2 session.
        /// </summary>
        public static string GetPersistedKeyInjectionScript(
            string? privateKeyPkcs8Base64,
            string? publicKeySpkiBase64,
            string? tokenTemplate) =>
            HbaBrowserScripts.BuildRestoreModerationScript(privateKeyPkcs8Base64, publicKeySpkiBase64, tokenTemplate);
    }
}
