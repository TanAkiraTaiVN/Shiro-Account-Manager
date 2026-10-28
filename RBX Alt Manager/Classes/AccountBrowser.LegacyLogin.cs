#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PuppeteerSharp;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager.Classes
{
    internal partial class AccountBrowser
    {
        [GeneratedRegex(@"/users/[0-9]+/two-step-verification/login")]
        private static partial Regex TwoStepLoginRegex();

        public async Task Login(string Username = "", string Password = "", string[]? Arguments = null)
        {
            // Use a normal browser for the interactive Roblox sign-in so CAPTCHA,
            // passkeys and 2-step verification remain under the user's control.
            if (string.IsNullOrEmpty(Username) && string.IsNullOrEmpty(Password))
            {
                // Keep this legacy public entry point on the same browser
                // boundary as the dashboard and bulk import flows.
                await new BrowserService().SignInAsync();
                return;
            }

            // Preserve the existing bulk username/password import flow.
            await LaunchBrowser("https://roblox.com/login", Arguments: Arguments,
                PostNavigation: async (page) => await LoginTask(page, Username, Password),
                UseStealth: false);
        }

        public async Task LoginTask(Page page, string Username = "", string Password = "")
        {
            page.RequestFinished += Page_RequestFinished;
            page.RequestFailed += Page_RequestFailed;

            await page.EvaluateExpressionAsync(@"document.body.classList.remove(""light-theme"");document.body.classList.add(""dark-theme"");");

            try
            {
                if (!string.IsNullOrEmpty(Username) && await page.WaitForSelectorAsync("#login-username", new WaitForSelectorOptions() { Timeout = 5000 }) != null)
                    await page.TypeAsync("#login-username", Username);
                if (!string.IsNullOrEmpty(Password) && await page.WaitForSelectorAsync("#login-password", new WaitForSelectorOptions() { Timeout = 5000 }) != null)
                    await page.TypeAsync("#login-password", Password);

                if (!string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password)) await page.ClickAsync("#login-button");
            }
            catch (Exception ex) { Program.Logger.Error($"An exception was caught while trying to automatically log in: {ex}"); }
        }

        private void Page_RequestFinished(object? sender, RequestEventArgs e)
        {
            Observe(HandlePageRequestFinishedAsync(sender, e));
        }

        private async Task HandlePageRequestFinishedAsync(object? sender, RequestEventArgs e)
        {
            try
            {
                if (e?.Request == null || !Uri.TryCreate(e.Request.Url, UriKind.Absolute, out Uri? Url) || Url == null) return;
                var Response = e.Request.Response;
                if (Response == null) return;

                if (IsCaptchaProviderHost(Url.Host) && (int)Response.Status >= 400)
                    Program.Logger.Warn($"Roblox verification request to {Url.Host} returned HTTP {(int)Response.Status}.");

                if (Response.Status == HttpStatusCode.OK && e.Request.Method == HttpMethod.Post && Url.Host == "auth.roblox.com")
                {
                    if ((Url.AbsolutePath == "/v2/login" || Url.AbsolutePath == "/v2/signup") && e.Request.PostData != null)
                    {
                        try
                        {
                            using var loginDoc = JsonDocument.Parse((string)e.Request.PostData);
                            var root = loginDoc.RootElement;
                            string? password = root.TryGetProperty("password", out var pw) ? pw.GetString() : null;
                            string? loginType = root.TryGetProperty("ctype", out var ct) ? ct.GetString() : null;
                            if (!string.IsNullOrEmpty(password) && string.Equals(loginType, "username", StringComparison.OrdinalIgnoreCase))
                                Password = password;
                        }
                        catch (Exception ex)
                        {
                            Program.Logger?.Debug($"Parse login PostData failed: {ex.Message}");
                        }

                        if (page != null && !page.IsClosed)
                        {
                            var cookies = await page.GetCookiesAsync("https://roblox.com/");
                            if (cookies?.FirstOrDefault(Cookie => Cookie.Name == ".ROBLOSECURITY") is CookieParam Cookie)
                                await AddAccount(Cookie);
                        }
                    }
                    else if (TwoStepLoginRegex().IsMatch(Url.AbsolutePath) && page != null && !page.IsClosed)
                    {
                        var cookies = await page.GetCookiesAsync("https://roblox.com/");
                        if (cookies?.FirstOrDefault(Cookie => Cookie.Name == ".ROBLOSECURITY") is CookieParam Cookie)
                            await AddAccount(Cookie);
                    }
                }
            }
            catch (Exception ex) when (IsTransientBrowserException(ex))
            {
                // Benign transient navigation or destroyed context
            }
            catch (Exception ex)
            {
                Program.Logger.Debug($"HandlePageRequestFinishedAsync error: {ex.Message}");
            }
        }

        private static bool IsTransientBrowserException(Exception ex)
            => ChromeLaunchHelper.IsDetachedOrTransientBrowserException(ex);

        private void Page_RequestFailed(object? sender, RequestEventArgs e)
        {
            if (e?.Request != null && Uri.TryCreate(e.Request.Url, UriKind.Absolute, out Uri? Url) && Url != null && IsCaptchaProviderHost(Url.Host))
                Program.Logger.Warn($"Roblox verification request to {Url.Host} failed: {e.Request.FailureText ?? "unknown network error"}.");
        }

        private static bool IsCaptchaProviderHost(string Host) =>
            Host.EndsWith(".arkoselabs.com", StringComparison.OrdinalIgnoreCase) ||
            Host.Equals("arkoselabs.com", StringComparison.OrdinalIgnoreCase) ||
            Host.EndsWith(".funcaptcha.com", StringComparison.OrdinalIgnoreCase) ||
            Host.Equals("funcaptcha.com", StringComparison.OrdinalIgnoreCase);

        private async Task AddAccount(CookieParam SecurityToken)
        {
            if (Proxy == null)
                await AccountManager.AddAccountAsync(SecurityToken.Value, Password ?? string.Empty).ConfigureAwait(false);
            else
            {
                if (page != null)
                {
                    await page.WaitForNavigationAsync().ConfigureAwait(false);
                    string accountJson = await page.EvaluateFunctionAsync<string>("() => { return fetch('/my/account/json').then(x=>x.text()); }").ConfigureAwait(false);
                    await AccountManager.AddAccountAsync(SecurityToken.Value, Password ?? string.Empty, accountJson).ConfigureAwait(false);
                }
            }

            Password = null;

            if (browser != null)
                await browser.DisposeAsync();
        }
    }
}
