#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using Shiro.AvaloniaUI.Views;
using Shiro.AvaloniaUI.Views.Panels;
using Shiro.Services;
using Shiro.ViewModels;
using SkiaSharp;
using Xunit;
using NavigationPage = Shiro.ViewModels.NavigationPage;

namespace Shiro.Tests.Ui
{
    [Collection("SerialGlobalState")]
    public sealed class AdaptiveGameHeroTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly bool _origAdaptiveTint;
        private readonly Color? _origAdaptiveColor;
        private readonly AccentTheme _origTheme;
        private readonly SurfacePreset _origPreset;
        private readonly string _origPlaceId;
        private readonly AppLanguage _origLang;
        private readonly ILogSink? _origSink;

        public AdaptiveGameHeroTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "shiro-hero-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _origAdaptiveTint = VisualPreferencesService.Instance.EnableAdaptiveGameTint;
            _origAdaptiveColor = AccentThemeService.AdaptiveGameColor;
            _origTheme = AccentThemeService.CurrentTheme;
            _origPreset = AccentThemeService.CurrentSurfacePreset;
            _origPlaceId = AppServices.ServerBrowserState.CurrentPlaceId;
            _origLang = LocalizationService.CurrentLanguage;
            _origSink = Logger.Sink;
        }

        public void Dispose()
        {
            AccentThemeService.SetAdaptiveGameColor(_origAdaptiveColor);
            AccentThemeService.ApplySurfacePreset(_origPreset, save: false);
            AccentThemeService.ApplyTheme(_origTheme, save: false);
            VisualPreferencesService.Instance.EnableAdaptiveGameTint = _origAdaptiveTint;
            AppServices.ServerBrowserState.CurrentPlaceId = _origPlaceId;
            LocalizationService.SetLanguage(_origLang, saveSetting: false);
            Application.Current?.Resources.MergedDictionaries.Clear();
            Logger.Sink = _origSink;
            GameThumbnailService.ClearCache();
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        [AvaloniaFact]
        public async Task LaunchBarViewModel_ClearsAdaptiveGameColor_WhenPlaceIdClearedOrTintDisabled()
        {
            using var storageScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
            using var vm = new LaunchBarViewModel(settings: CreateInMemorySettings());
            VisualPreferencesService.Instance.EnableAdaptiveGameTint = true;
            AccentThemeService.SetAdaptiveGameColor(null);

            using var blueBitmap = CreatePixelBitmap(32, 32, r: 0, g: 112, b: 255, a: 255);
            vm.PlaceId = "2753915549";
            await vm.ApplyThumbnailAndUpdateAdaptiveColorAsync(blueBitmap);
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(vm.AdaptiveGameColor);
            Assert.Equal(vm.AdaptiveGameColor, AccentThemeService.AdaptiveGameColor);
            var (_, c1, h1) = OklchColorMath.FromColor(vm.AdaptiveGameColor!.Value);
            Assert.InRange(c1, 0.04, 0.20);
            Assert.InRange(h1, 225.0, 290.0);

            vm.PlaceId = string.Empty;
            Dispatcher.UIThread.RunJobs();

            Assert.Null(vm.AdaptiveGameColor);
            Assert.Null(AccentThemeService.AdaptiveGameColor);
            Assert.False(vm.HasAdaptiveGameColor);

            using var goldBitmap = CreatePixelBitmap(32, 32, r: 255, g: 188, b: 0, a: 255);
            vm.PlaceId = "2753915549";
            await vm.ApplyThumbnailAndUpdateAdaptiveColorAsync(goldBitmap);
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(vm.AdaptiveGameColor);
            var (_, _, hGold) = OklchColorMath.FromColor(vm.AdaptiveGameColor!.Value);
            Assert.InRange(hGold, 55.0, 115.0);

            VisualPreferencesService.Instance.EnableAdaptiveGameTint = false;
            Dispatcher.UIThread.RunJobs();

            Assert.Null(vm.AdaptiveGameColor);
            Assert.Null(AccentThemeService.AdaptiveGameColor);
            Assert.False(vm.HasAdaptiveGameColor);
        }

        [Fact]
        public void LaunchBarViewModel_OpenCurrentGameServersCommand_NavigatesToServerList()
        {
            string? syncedPlaceId = null;
            NavigationPage? navigatedPage = null;

            using var vm = new LaunchBarViewModel(
                settings: CreateInMemorySettings(),
                syncPlaceIdToServerList: pid => syncedPlaceId = pid,
                navigateToPage: page => navigatedPage = page);

            vm.PlaceId = "2753915549";
            syncedPlaceId = null;
            navigatedPage = null;

            vm.OpenCurrentGameServersCommand.Execute(null);

            Assert.Equal("2753915549", syncedPlaceId);
            Assert.Equal(NavigationPage.ServerList, navigatedPage);

            vm.PlaceId = "https://www.roblox.com/games/1818/Classic-Crossroads";
            syncedPlaceId = null;
            navigatedPage = null;

            vm.OpenCurrentGameServersCommand.Execute(null);

            Assert.Equal("1818", vm.PlaceId);
            Assert.Equal("1818", syncedPlaceId);
            Assert.Equal(NavigationPage.ServerList, navigatedPage);
        }

        [AvaloniaFact]
        public void AdaptivePaletteExtractor_ExtractAccentFromBitmap_HandlesFormatsStridesAndNeutralPixels()
        {
            Assert.Null(AdaptivePaletteExtractor.ExtractAccentFromBitmap(null));

            using var emeraldBgra = CreatePixelBitmap(
                36, 36, r: 16, g: 185, b: 129, a: 255, PixelFormats.Bgra8888, AlphaFormat.Unpremul, rowPaddingBytes: 16);
            Color? emerald = AdaptivePaletteExtractor.ExtractAccentFromBitmap(emeraldBgra);
            Assert.NotNull(emerald);
            var (_, _, emeraldHue) = OklchColorMath.FromColor(emerald!.Value);
            Assert.InRange(emeraldHue, 140.0, 185.0);

            using var rgbaPremul = CreatePixelBitmap(
                36, 36, r: 236, g: 72, b: 153, a: 200, PixelFormats.Rgba8888, AlphaFormat.Premul, rowPaddingBytes: 8);
            Color? pink = AdaptivePaletteExtractor.ExtractAccentFromBitmap(rgbaPremul);
            Assert.NotNull(pink);
            var (_, _, pinkHue) = OklchColorMath.FromColor(pink!.Value);
            Assert.InRange(pinkHue, 325.0, 360.0);

            using var orangeOpaque = CreatePixelBitmap(
                48, 48, r: 255, g: 110, b: 20, a: 255, PixelFormats.Bgra8888, AlphaFormat.Opaque);
            Color? orange = AdaptivePaletteExtractor.ExtractAccentFromBitmap(orangeOpaque);
            Assert.NotNull(orange);
            var (_, _, orangeHue) = OklchColorMath.FromColor(orange!.Value);
            Assert.InRange(orangeHue, 25.0, 65.0);

            using var neutralGray = CreateSkiaImmutablePngBitmap(48, 48, new SKColor(120, 120, 120, 255));
            Assert.Null(AdaptivePaletteExtractor.ExtractAccentFromBitmap(neutralGray));
        }

        [AvaloniaFact]
        public async Task LaunchBarViewModel_RespectsTintPreferenceAndCancelsStaleExtraction_OnPlaceIdChange()
        {
            using var storageScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
            GameThumbnailService.ClearCache();
            const long placeId = 44556677L;
            GameInfoService.CachePlaceName(placeId, "Hero Aura Adventure");

            string? thumbPath = GameThumbnailService.GetDiskCachePath(placeId);
            Assert.NotNull(thumbPath);
            Directory.CreateDirectory(Path.GetDirectoryName(thumbPath!)!);
            SaveSolidPng(thumbPath!, 48, 48, new SKColor(37, 99, 235, 255));

            VisualPreferencesService.Instance.EnableAdaptiveGameTint = false;
            AccentThemeService.SetAdaptiveGameColor(null);

            using var vm = new LaunchBarViewModel(settings: CreateInMemorySettings());
            vm.PlaceId = placeId.ToString();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (vm.CurrentGameThumbnail == null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(30);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Equal("Hero Aura Adventure", vm.CurrentGameName);
            Assert.NotNull(vm.CurrentGameThumbnail);
            Assert.True(vm.HasCurrentGameInfo);
            Assert.Null(vm.AdaptiveGameColor);
            Assert.Null(AccentThemeService.AdaptiveGameColor);

            using var blueThumb = CreatePixelBitmap(48, 48, r: 37, g: 99, b: 235, a: 255);
            await vm.ApplyThumbnailAndUpdateAdaptiveColorAsync(blueThumb);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(vm.AdaptiveGameColor);

            // Re-enabling adaptive game tint extracts color from already-loaded thumbnail
            VisualPreferencesService.Instance.EnableAdaptiveGameTint = true;
            Dispatcher.UIThread.RunJobs();
            if (vm.PendingAdaptiveColorTask != null)
                await vm.PendingAdaptiveColorTask;
            Dispatcher.UIThread.RunJobs();

            Assert.NotNull(vm.AdaptiveGameColor);
            Assert.Equal(vm.AdaptiveGameColor, AccentThemeService.AdaptiveGameColor);
            var (_, _, blueHue) = OklchColorMath.FromColor(vm.AdaptiveGameColor!.Value);
            Assert.InRange(blueHue, 225.0, 285.0);

            // Clearing PlaceId cancels any pending work and resets adaptive color immediately
            using var staleThumb = CreatePixelBitmap(24, 24, 220, 38, 38, 255);
            var pendingTask = vm.ApplyThumbnailAndUpdateAdaptiveColorAsync(staleThumb);
            vm.PlaceId = string.Empty;
            try
            {
                await pendingTask;
            }
            catch (TaskCanceledException)
            {
                // Expected when PlaceId clear cancels _gameLookupCts before Task.Run starts
            }
            Dispatcher.UIThread.RunJobs();

            Assert.Null(vm.AdaptiveGameColor);
            Assert.Null(AccentThemeService.AdaptiveGameColor);
        }

        [AvaloniaFact]
        public async Task LaunchBarViewModel_StaleTintGenerationOrPreferenceToggleDuringLookup_NeitherClobbersNorDropsValidTint()
        {
            using var storageScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
            GameThumbnailService.ClearCache();
            VisualPreferencesService.Instance.EnableAdaptiveGameTint = true;
            AccentThemeService.SetAdaptiveGameColor(null);

            using var blueThumb = CreatePixelBitmap(32, 32, r: 0, g: 112, b: 255, a: 255);
            using var goldThumb = CreatePixelBitmap(32, 32, r: 255, g: 188, b: 0, a: 255);

            var lookupStartedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var lookupReleaseTcs = new TaskCompletionSource<(string? Name, Bitmap? Thumbnail)>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var vm = new LaunchBarViewModel(settings: CreateInMemorySettings())
            {
                ResolveGameInfoForTest = async (_, ct) =>
                {
                    lookupStartedTcs.TrySetResult();
                    return await lookupReleaseTcs.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            };

            // 1. Toggle true -> false -> true while async lookup is awaiting before thumbnail exists.
            vm.PlaceId = "778899";
            await lookupStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            long initialTintGen = vm.AdaptiveColorGenerationForTest;

            VisualPreferencesService.Instance.EnableAdaptiveGameTint = false;
            VisualPreferencesService.Instance.EnableAdaptiveGameTint = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.AdaptiveColorGenerationForTest > initialTintGen);

            lookupReleaseTcs.SetResult(("Toggle Survival Game", blueThumb));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (vm.AdaptiveGameColor == null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Equal("Toggle Survival Game", vm.CurrentGameName);
            Assert.NotNull(vm.AdaptiveGameColor);
            Color validBlue = vm.AdaptiveGameColor!.Value;
            var (_, _, blueHue) = OklchColorMath.FromColor(validBlue);
            Assert.InRange(blueHue, 225.0, 290.0);
            Assert.Equal(validBlue, AccentThemeService.AdaptiveGameColor);

            // 2. Late/stale generation callback must NOT clear newer valid tint.
            vm.ApplyOrClearAdaptiveColorOnUi(goldThumb, Colors.Red, initialTintGen);
            Assert.Equal(validBlue, vm.AdaptiveGameColor);
            Assert.Equal(validBlue, AccentThemeService.AdaptiveGameColor);

            // 3. Toggle true -> false mid-lookup leaves AdaptiveGameColor cleared when lookup resolves.
            var secondStartedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondReleaseTcs = new TaskCompletionSource<(string? Name, Bitmap? Thumbnail)>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.ResolveGameInfoForTest = async (_, ct) =>
            {
                secondStartedTcs.TrySetResult();
                return await secondReleaseTcs.Task.WaitAsync(ct).ConfigureAwait(false);
            };

            vm.PlaceId = "990011";
            await secondStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            VisualPreferencesService.Instance.EnableAdaptiveGameTint = false;
            Dispatcher.UIThread.RunJobs();

            secondReleaseTcs.SetResult(("Tint Disabled Mid-Flight", goldThumb));
            var secondDeadline = DateTime.UtcNow.AddSeconds(5);
            while (vm.CurrentGameName != "Tint Disabled Mid-Flight" && DateTime.UtcNow < secondDeadline)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Same(goldThumb, vm.CurrentGameThumbnail);
            Assert.Null(vm.AdaptiveGameColor);
            Assert.Null(AccentThemeService.AdaptiveGameColor);
        }

        [AvaloniaFact]
        public void DashboardView_RendersGameHeroChipAndQuickTileFlyout_WithZeroBindingErrors()
        {
            var sink = new BindingErrorSink();
            Logger.Sink = sink;
            LocalizationService.SetLanguage(AppLanguage.English, saveSetting: false);
            VisualPreferencesService.Instance.EnableAdaptiveGameTint = true;
            GameInfoService.CachePlaceName(2753915549L, "Blox Fruits");

            var repo = Substitute.For<IAccountRepository>();
            repo.GetAll().Returns(new List<Account>());
            var settings = CreateInMemorySettings();
            var browserState = Substitute.For<IServerBrowserStateService>();
            browserState.CurrentPlaceId.Returns("2753915549");

            using var repoScope = AppServices.Override<IAccountRepository>(repo);
            using var settingsScope = AppServices.Override<ISettingsService>(settings);
            using var browserScope = AppServices.Override<IServerBrowserStateService>(browserState);

            DashboardView? view = null;
            Window? window = null;
            try
            {
                view = new DashboardView();
                window = new Window { Width = 1260, Height = 760, Content = view };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Assert.NotNull(view.FindControl<TextBox>("TxtSearch"));
                Assert.NotNull(view.FindControl<TextBox>("TxtPlaceId"));
                Assert.NotNull(view.FindControl<TextBox>("TxtJobId"));
                Assert.NotNull(view.FindControl<ComboBox>("CmbGroups"));
                Assert.NotNull(view.FindControl<ItemsControl>("CardsItemsControl"));
                Assert.NotNull(view.FindControl<ItemsControl>("TableItemsControl"));

                var heroChip = view.GetVisualDescendants().OfType<DashboardGameHeroChip>().FirstOrDefault();
                Assert.NotNull(heroChip);
                var btnHeroServers = heroChip!.FindControl<Button>("BtnHeroViewServers");
                Assert.NotNull(btnHeroServers);
                Assert.Equal(NavigationPage.Accounts, view.ViewModel.CurrentPage);

                btnHeroServers!.Command!.Execute(btnHeroServers.CommandParameter);
                Assert.Equal(NavigationPage.ServerList, view.ViewModel.CurrentPage);

                var btnQuickTile = view.FindControl<Button>("BtnQuickWindowTile");
                Assert.NotNull(btnQuickTile);
                var tileFlyout = Assert.IsType<MenuFlyout>(btnQuickTile!.Flyout);
                tileFlyout.ShowAt(btnQuickTile);
                Dispatcher.UIThread.RunJobs();

                var items = tileFlyout.Items.OfType<MenuItem>().ToList();
                Assert.Equal(5, items.Count);
                Assert.Equal(
                    new[] { "Auto", "2x2", "3x2", "4x3", "Cascade" },
                    items.Select(i => i.CommandParameter as string).ToArray());
                Assert.All(items, i => Assert.Same(view.ViewModel.ToolsStatus.ArrangeRobloxWindowsCommand, i.Command));

                tileFlyout.Hide();
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(sink.Errors);

                view.ViewModel.Dispose();
                VisualPreferencesService.Instance.EnableAdaptiveGameTint = false;
                VisualPreferencesService.Instance.EnableAdaptiveGameTint = true;
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(sink.Errors);
            }
            finally
            {
                window?.Close();
                view?.ViewModel.Dispose();
            }
        }

        private static ISettingsService CreateInMemorySettings()
        {
            var store = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var settings = Substitute.For<ISettingsService>();
            settings.Get(Arg.Any<string>()).Returns(ci =>
                store.TryGetValue(ci.Arg<string>(), out string? v) ? v : null);
            settings.Exists(Arg.Any<string>()).Returns(ci => store.ContainsKey(ci.Arg<string>()));
            settings.When(s => s.Set(Arg.Any<string>(), Arg.Any<string?>()))
                .Do(ci =>
                {
                    string key = ci.ArgAt<string>(0);
                    string? val = ci.ArgAt<string?>(1);
                    if (val == null) store.Remove(key);
                    else store[key] = val;
                });
            return settings;
        }

        private static Bitmap CreatePixelBitmap(
            int width,
            int height,
            byte r,
            byte g,
            byte b,
            byte a,
            PixelFormat? format = null,
            AlphaFormat alphaFormat = AlphaFormat.Unpremul,
            int rowPaddingBytes = 0)
        {
            PixelFormat fmt = format ?? PixelFormats.Bgra8888;
            int stride = (width * 4) + rowPaddingBytes;
            byte[] pixels = new byte[stride * height];
            bool isRgba = fmt == PixelFormats.Rgba8888;
            byte effR = alphaFormat == AlphaFormat.Premul ? (byte)((r * a) / 255) : r;
            byte effG = alphaFormat == AlphaFormat.Premul ? (byte)((g * a) / 255) : g;
            byte effB = alphaFormat == AlphaFormat.Premul ? (byte)((b * a) / 255) : b;

            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int i = rowOffset + (x * 4);
                    pixels[i] = isRgba ? effR : effB;
                    pixels[i + 1] = effG;
                    pixels[i + 2] = isRgba ? effB : effR;
                    pixels[i + 3] = a;
                }
            }

            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                Assembly skiaAsm = typeof(ISkiaSharpApiLeaseFeature).Assembly;
                Type immType = skiaAsm.GetType("Avalonia.Skia.ImmutableBitmap")
                    ?? throw new InvalidOperationException("Avalonia.Skia.ImmutableBitmap type not found.");
                var impl = (IBitmapImpl)Activator.CreateInstance(
                    immType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new object[]
                    {
                        new PixelSize(width, height),
                        new Vector(96.0, 96.0),
                        stride,
                        fmt,
                        alphaFormat,
                        handle.AddrOfPinnedObject()
                    },
                    null)!;
                return WrapBitmapImpl(impl);
            }
            finally
            {
                if (handle.IsAllocated)
                    handle.Free();
            }
        }

        private static Bitmap CreateSkiaImmutablePngBitmap(int width, int height, SKColor color)
        {
            using var skBitmap = new SKBitmap(width, height);
            skBitmap.Erase(color);
            using var image = SKImage.FromBitmap(skBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var ms = new MemoryStream(data.ToArray());

            Assembly skiaAsm = typeof(ISkiaSharpApiLeaseFeature).Assembly;
            Type immType = skiaAsm.GetType("Avalonia.Skia.ImmutableBitmap")
                ?? throw new InvalidOperationException("Avalonia.Skia.ImmutableBitmap type not found.");
            var impl = (IBitmapImpl)Activator.CreateInstance(
                immType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new object[] { ms },
                null)!;
            return WrapBitmapImpl(impl);
        }

        private static Bitmap WrapBitmapImpl(IBitmapImpl impl)
        {
            ConstructorInfo ctor = typeof(Bitmap).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(IBitmapImpl) },
                null) ?? throw new InvalidOperationException("Bitmap(IBitmapImpl) constructor not found.");
            return (Bitmap)ctor.Invoke(new object[] { impl });
        }

        private static void SaveSolidPng(string path, int width, int height, SKColor color)
        {
            using var skBitmap = new SKBitmap(width, height);
            skBitmap.Erase(color);
            using var image = SKImage.FromBitmap(skBitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
    }
}
