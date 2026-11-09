#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Shiro.Services;

namespace Shiro.Services
{
    internal partial class AccountBrowser
    {
        private static string[]? _cachedSystemBrowserExecutables;

        public static string? FindSystemBrowserExecutable()
        {
            var executables = FindSystemBrowserExecutables();
            return executables.Length > 0 ? executables[0] : null;
        }

        public static string[] FindSystemBrowserExecutables()
        {
            var cached = _cachedSystemBrowserExecutables;
            if (cached != null && cached.Length > 0 && File.Exists(cached[0]))
                return cached;

            string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // A 32-bit build sees the 32-bit Program Files directory through
            // SpecialFolder.ProgramFiles. ProgramW6432 is needed to find a
            // normal 64-bit Chrome installation from that build.
            string ProgramFiles = Environment.GetEnvironmentVariable("ProgramFiles") ??
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string? ProgramFiles64 = Environment.GetEnvironmentVariable("ProgramW6432");
            string ProgramFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ??
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            string[] Candidates =
            [
                Path.Combine(LocalAppData, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(ProgramFiles64 ?? string.Empty, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(ProgramFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(ProgramFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(ProgramFiles64 ?? string.Empty, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(ProgramFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(ProgramFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(LocalAppData, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(ProgramFiles64 ?? string.Empty, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(ProgramFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(LocalAppData, "Programs", "Opera", "opera.exe"),
                Path.Combine(LocalAppData, "Programs", "Opera GX", "opera.exe"),
                Path.Combine(ProgramFiles, "Vivaldi", "Application", "vivaldi.exe"),
                Path.Combine(LocalAppData, "Vivaldi", "Application", "vivaldi.exe")
            ];

            string[] found = Candidates
                .Concat(FindRegisteredBrowserExecutables())
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (found.Length > 0)
                _cachedSystemBrowserExecutables = found;
            return found;
        }

        /// <summary>
        /// Resolves Chrome/Edge through the Windows "App Paths" registration so
        /// non-default install locations (per-machine policies, custom drives)
        /// are still found. Chrome stays ahead of Edge to keep the existing
        /// browser preference.
        /// </summary>
        private static IEnumerable<string> FindRegisteredBrowserExecutables()
        {
            if (!OperatingSystem.IsWindows())
                yield break;

            foreach (string exeName in new[] { "chrome.exe", "msedge.exe" })
            {
                foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
                {
                    string? path = null;
                    try
                    {
                        using var key = hive.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
                        path = (key?.GetValue(null) as string)?.Trim('"');
                    }
                    catch (Exception ex)
                    {
                        AppServices.Logger?.Debug($"App Paths lookup for {exeName} failed: {ex.Message}");
                    }

                    if (!string.IsNullOrWhiteSpace(path))
                        yield return path;
                }
            }
        }

        /// <summary>
        /// Returns a Chromium build that an older Shiro version already
        /// downloaded into the cache. Shiro no longer downloads Chromium
        /// itself (Edge ships with Windows 10/11), but an existing cached copy
        /// is still honoured so machines without Chrome/Edge keep working.
        /// </summary>
        public static string? FindCachedChromiumExecutable()
        {
            try
            {
                return Fetcher.GetInstalledBrowsers()
                    .Select(b => b.GetExecutablePath())
                    .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
            }
            catch (Exception ex)
            {
                AppServices.Logger?.Debug($"Cached Chromium lookup failed: {ex.Message}");
                return null;
            }
        }

        public static string BrowserNotFoundMessage =>
            Shiro.Services.LocalizationService.Get("AccountBrowser.BrowserNotFound");

        public static void CreateGrid(Vector2 Size)
        {
            ScreenGrid = new Dictionary<int, Vector2>();

            var (screenWidth, screenHeight) = GetVirtualScreen();

            for (int x = 0; x < screenWidth; x += (int)Size.X)
                for (int y = 0; y < screenHeight - (Size.Y / 2); y += (int)Size.Y)
                    ScreenGrid.Add(ScreenGrid.Count, new Vector2(x, y));
        }

        [LibraryImport("user32.dll")]
        private static partial int GetSystemMetrics(int nIndex);

        private static (int width, int height) GetWorkArea()
        {
            if (OperatingSystem.IsWindows())
            {
                var (_, _, width, height) = RobloxWindowService.GetTargetWorkArea();
                if (width > 0 && height > 0)
                    return (width, height);
            }
            return (1920, 1080);
        }

        private static (int width, int height) GetVirtualScreen()
        {
            if (OperatingSystem.IsWindows())
            {
                int w = GetSystemMetrics(78); // SM_CXVIRTUALSCREEN
                int h = GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
                if (w > 0 && h > 0) return (w, h);
            }
            return (1920, 1080);
        }
    }

    internal class BrowserConfig
    {
        public Dictionary<string, string>? PreNavigateActions = null;

        public List<string>? PreNavigateScripts = null;
        public List<string>? PostNavigateScripts = null;

        public List<string>? CustomArguments = null;
    }
}
