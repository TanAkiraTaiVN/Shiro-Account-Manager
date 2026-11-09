#nullable enable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PuppeteerSharp;
using Shiro.Services;

namespace Shiro.Services
{
    internal static partial class RobloxBrowserSignup
    {
        private static async Task EnsureSignupPageAsync(IPage page)
        {
            if (page == null || page.IsClosed)
                throw new InvalidOperationException("The sign-up page was not created.");

            string currentUrl = string.Empty;
            try { currentUrl = page.Url ?? string.Empty; }
            catch (Exception ex) { AppServices.Logger?.Debug($"Get page.Url threw: {ex.Message}"); }

            if (!currentUrl.Contains("roblox.com/signup", StringComparison.OrdinalIgnoreCase))
            {
                await page.GoToAsync(SignupUrl, new NavigationOptions
                {
                    Timeout = NavigationTimeoutMilliseconds,
                    WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded }
                });
            }

            await WaitForSelectorWithNavigationRetryAsync(page, "input, select, button");
        }

        private static async Task FillSignupFormAsync(
            IPage page,
            string? username,
            string? password,
            SignupProfile? profile,
            bool submitCredentials)
        {
            profile ??= CreateRandomProfile();
            // Roblox currently renders native select controls, but its React
            // state updates asynchronously. Setting .value from JavaScript
            // can leave the day control showing "Day" after the month/year
            // change. Use Puppeteer's real select interaction and wait for
            // each controlled update before moving to the next field.
            try
            {
                await WaitForSelectorWithNavigationRetryAsync(page, "#MonthDropdown");
                await WaitForSelectorWithNavigationRetryAsync(page, "#DayDropdown");
                await WaitForSelectorWithNavigationRetryAsync(page, "#YearDropdown");

                string[] monthValues = { "Jan", "Feb", "Mar", "Apr", "May", "Jun",
                    "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
                string monthValue = monthValues[profile.BirthDate.Month - 1];
                string dayValue = profile.BirthDate.Day.ToString("00");
                string yearValue = profile.BirthDate.Year.ToString();

                bool birthdaySet = false;
                for (int attempt = 0; attempt < 5 && !birthdaySet; attempt++)
                {
                    try
                    {
                        await page.SelectAsync("#MonthDropdown", monthValue);
                        await Task.Delay(120);

                        await page.SelectAsync("#YearDropdown", yearValue);
                        await Task.Delay(180);

                        // Select the day last. Roblox may rebuild this select after a
                        // month/year change, so selecting it before the final update
                        // can be silently discarded.
                        await page.SelectAsync("#DayDropdown", dayValue);
                        await Task.Delay(180);
                    }
                    catch (Exception exception) when (IsTransientNavigationException(exception))
                    {
                        await WaitForNavigationToSettleAsync(page);
                        continue;
                    }

                    (bool Success, string[]? Value) birthdayRead = await TryEvaluateFunctionAsync<string[]>(page, @"() => [
                        document.querySelector('#MonthDropdown')?.value || '',
                        document.querySelector('#DayDropdown')?.value || '',
                        document.querySelector('#YearDropdown')?.value || ''
                    ]");
                    string[]? selectedBirthday = birthdayRead.Success ? birthdayRead.Value : null;
                    birthdaySet = selectedBirthday != null && selectedBirthday.Length >= 3 &&
                        string.Equals(selectedBirthday[0], monthValue, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(selectedBirthday[1], dayValue, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(selectedBirthday[2], yearValue, StringComparison.OrdinalIgnoreCase);
                }

                if (!birthdaySet)
                    AppServices.Logger.Warn("Roblox did not confirm the generated birthday values; complete the birthday fields manually if needed.");
            }
            catch (Exception ex)
            {
                AppServices.Logger.Warn("Could not prefill birthday fields; complete them manually if needed. " + ex.Message);
            }

            string genderSelector = string.Equals(profile.Gender, "Male", StringComparison.OrdinalIgnoreCase)
                ? "#MaleButton"
                : "#FemaleButton";
            try
            {
                await WaitForSelectorWithNavigationRetryAsync(page, genderSelector);
                await page.ClickAsync(genderSelector);
            }
            catch (Exception exception)
            {
                AppServices.Logger.Warn("Could not preselect the generated Roblox gender; choose it manually if needed. " + exception.Message);
            }

            if (!string.IsNullOrWhiteSpace(username))
            {
                if (!await FillTextInputAsync(page, "#signup-username", username.Trim()))
                {
                    AppServices.Logger.Warn("Roblox rebuilt the sign-up page while filling the username; leaving Chrome open for manual completion.");
                }
            }

            if (!string.IsNullOrWhiteSpace(password))
            {
                if (!await FillTextInputAsync(page, "#signup-password", password))
                {
                    AppServices.Logger.Warn("Roblox rebuilt the sign-up page while filling the password; leaving Chrome open for manual completion.");
                }
            }

            if (!submitCredentials)
            {
                AppServices.Logger.Info("Roblox sign-up fields prepared in an isolated browser; complete registration and verification in Chrome.");
                return;
            }

            await ClickSignupButtonAsync(page);
        }

        private static async Task<bool> FillTextInputAsync(IPage page, string selector, string value)
        {
            value ??= string.Empty;
            for (int attempt = 0; attempt < NavigationRetryCount; attempt++)
            {
                try
                {
                    await WaitForSelectorWithNavigationRetryAsync(page, selector);
                    await page.ClickAsync(selector);
                    await page.Keyboard.DownAsync("Control");
                    await page.Keyboard.PressAsync("A");
                    await page.Keyboard.UpAsync("Control");
                    await page.TypeAsync(selector, value);
                }
                catch (Exception exception) when (IsTransientNavigationException(exception))
                {
                    await WaitForNavigationToSettleAsync(page);
                    continue;
                }
                catch (Exception ex) when (!IsLikelySignupPage(page))
                {
                    AppServices.Logger.Debug("[RobloxBrowserSignup] Type value aborted because page is no longer sign-up", ex);
                    return false;
                }

                // Roblox validates the username after the first key and can
                // replace the controlled input while Puppeteer is still typing.
                // In that case only the first character remains in the visible
                // field. Verify the DOM value and use the native setter plus real
                // input/change events as a controlled-component fallback.
                (bool Success, bool Value) typedValueRead = await TryEvaluateFunctionAsync<bool>(page, @"data => {
                const element = document.querySelector(data.selector);
                return !!element && element.value === data.value;
            }", new { selector, value });

                if (typedValueRead.Success && typedValueRead.Value)
                    return true;

                bool fallbackOk = false;
                try
                {
                    fallbackOk = await ChromeLaunchHelper.SetReactControlledInputValueAsync(page, selector, value).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsTransientNavigationException(exception))
                {
                    await WaitForNavigationToSettleAsync(page);
                    continue;
                }

                if (fallbackOk)
                {
                    await Task.Delay(120);
                    return true;
                }

                await WaitForNavigationToSettleAsync(page);
            }

            return false;
        }

        private static async Task ClickSignupButtonAsync(IPage page)
        {
            try
            {
                await WaitForSelectorWithNavigationRetryAsync(page, "#signup-button");
            }
            catch (Exception exception) when (IsTransientNavigationException(exception))
            {
                AppServices.Logger.Warn("Roblox changed the sign-up document while loading the Sign Up button; leaving Chrome open for manual completion.");
                return;
            }

            long readyStartTick = Stopwatch.GetTimestamp();
            TimeSpan readyTimeout = TimeSpan.FromMilliseconds(SignupButtonReadyTimeoutMilliseconds);
            bool ready = false;
            while (Stopwatch.GetElapsedTime(readyStartTick) < readyTimeout)
            {
                (bool Success, bool Value) readyRead = await TryEvaluateFunctionAsync<bool>(page, @"() => {
                    const button = document.querySelector('#signup-button');
                    if (!button) return false;
                    const style = window.getComputedStyle(button);
                    const rect = button.getBoundingClientRect();
                    return !button.disabled &&
                        button.getAttribute('aria-disabled') !== 'true' &&
                        rect.width > 0 && rect.height > 0 &&
                        style.display !== 'none' && style.visibility !== 'hidden';
                }");
                if (!readyRead.Success)
                {
                    if (!IsLikelySignupPage(page))
                    {
                        AppServices.Logger.Info("Roblox navigated while preparing the Sign Up button; leaving Chrome open for the next step.");
                        return;
                    }

                    await Task.Delay(150);
                    continue;
                }

                ready = readyRead.Value;

                if (ready)
                    break;

                await Task.Delay(150);
            }

            if (!ready)
            {
                AppServices.Logger.Warn("Roblox kept the Sign Up button disabled; leaving the form open for manual completion.");
                return;
            }

            // Give the last input/change events and Roblox's challenge bundle
            // time to settle so Arkose receives the same valid form state as a
            // manual click. A fresh Chrome profile can load this bundle a bit
            // later than the visible sign-up controls.
            await Task.Delay(1200);
            try
            {
                await page.ClickAsync("#signup-button");
            }
            catch (Exception exception) when (IsTransientNavigationException(exception))
            {
                // A normal form submission can destroy the old execution
                // context at the exact moment the click is dispatched.  The
                // click has already reached Roblox in that case, so keep the
                // browser open for CAPTCHA/manual verification.
                AppServices.Logger.Info("Roblox navigated after the Sign Up click; leaving Chrome open for human verification.");
                await WaitForNavigationToSettleAsync(page);
                return;
            }

            // Keep the CDP session alive while the challenge iframe starts.
            // Disconnecting immediately after the click can interrupt a slow
            // challenge bootstrap on VPN-routed connections.
            try
            {
                await page.WaitForSelectorAsync("iframe", new WaitForSelectorOptions
                {
                    Timeout = 4000
                });
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"WaitForSelectorAsync iframe timed out: {ex.Message}");
            }
            await Task.Delay(1800);
        }

        private static async Task WaitForSelectorWithNavigationRetryAsync(IPage page, string selector)
        {
            long startTick = Stopwatch.GetTimestamp();
            TimeSpan timeout = TimeSpan.FromMilliseconds(SelectorTimeoutMilliseconds);
            Exception? lastNavigationException = null;

            while (Stopwatch.GetElapsedTime(startTick) < timeout)
            {
                if (page == null || page.IsClosed)
                    throw new InvalidOperationException("The sign-up browser page was closed.");

                try
                {
                    int remaining = Math.Max(250, (int)(timeout - Stopwatch.GetElapsedTime(startTick)).TotalMilliseconds);
                    IElementHandle? handle = await page.WaitForSelectorAsync(selector, new WaitForSelectorOptions
                    {
                        Timeout = Math.Min(remaining, 1500)
                    });
                    if (handle != null)
                        return;
                }
                catch (Exception exception) when (IsTransientNavigationException(exception))
                {
                    lastNavigationException = exception;
                    await WaitForNavigationToSettleAsync(page);
                }
            }

            if (lastNavigationException != null)
                throw lastNavigationException;

            throw new TimeoutException($"Waiting for selector `{selector}` failed.");
        }

        private static async Task<(bool Success, T? Value)> TryEvaluateFunctionAsync<T>(IPage? page, string expression, params object[] args)
        {
            Exception? lastNavigationException = null;
            for (int attempt = 0; attempt < NavigationRetryCount; attempt++)
            {
                if (page == null || page.IsClosed)
                    return (false, default);

                try
                {
                    return (true, await page.EvaluateFunctionAsync<T>(expression, args));
                }
                catch (Exception exception) when (IsTransientNavigationException(exception))
                {
                    lastNavigationException = exception;
                    await WaitForNavigationToSettleAsync(page);
                }
            }

            if (lastNavigationException != null)
            {
                AppServices.Logger.Warn("Roblox changed the sign-up page while it was being prepared; continuing with the browser open. " +
                    lastNavigationException.Message);
            }

            return (false, default);
        }

        private static async Task WaitForNavigationToSettleAsync(IPage? page)
        {
            if (page == null || page.IsClosed)
                return;

            // Do not evaluate document.readyState here: that is the operation
            // that is invalid during a navigation.  A short CDP-free pause
            // lets Chrome create the new execution context before the retry.
            await Task.Delay(NavigationRetryDelayMilliseconds);
        }

        private static bool IsLikelySignupPage(IPage? page)
        {
            try
            {
                string url = page?.Url ?? string.Empty;
                return string.IsNullOrWhiteSpace(url) ||
                    url.Contains("roblox.com/signup", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("roblox.com/create", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"IsLikelySignupPage threw: {ex.Message}");
                return true;
            }
        }

        private static bool IsTransientNavigationException(Exception exception)
            => ChromeLaunchHelper.IsDetachedOrTransientBrowserException(exception);

        private static Task WaitForDevToolsPortAsync(int port, CancellationToken cancellationToken = default)
            => ChromeLaunchHelper.WaitForDevToolsPortAsync(port, DevToolsStartupTimeoutSeconds, DevToolsPollMilliseconds, cancellationToken);

        private static string GetCenteredBrowserPosition()
            => ChromeLaunchHelper.GetInteractiveBrowserPosition(InteractiveBrowserWidth, InteractiveBrowserHeight);

        private static bool IsDetachedPageException(PuppeteerException exception)
            => ChromeLaunchHelper.IsDetachedOrTransientBrowserException(exception);

        private static Task<IPage?> FindActiveRobloxPageAsync(IBrowser connectedBrowser)
            => ChromeLaunchHelper.FindActiveRobloxPageAsync(connectedBrowser);

        private static Task<string?> ReadAccountJsonAsync(IPage page)
            => ChromeLaunchHelper.ReadAuthenticatedRobloxAccountJsonAsync(page, maxAttempts: 1);

        private static int GetFreeLoopbackPort()
            => ChromeLaunchHelper.GetFreeLoopbackPort();

        private static void CloseBrowser(Process browser)
            => ChromeLaunchHelper.CloseBrowserProcess(browser, dispose: true);

        private static string QuoteArgument(string value)
            => ChromeLaunchHelper.QuoteArgument(value);

        private static Task TryDeleteTemporaryDirectoryAsync(string path)
            => ChromeLaunchHelper.TryDeleteSessionDirectoryAsync(path);

        private static Task<CookieParam?> ExtractRoblosecurityCookieAsync(IPage page)
            => ChromeLaunchHelper.ExtractRoblosecurityCookieAsync(page);
    }
}
