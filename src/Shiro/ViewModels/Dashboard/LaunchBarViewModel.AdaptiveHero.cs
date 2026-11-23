#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public sealed partial class LaunchBarViewModel
    {
        private void SubscribeAdaptiveTintEvents()
        {
            _visualPreferencesChangedHandler = WeakEventSubscription.CreateAction<LaunchBarViewModel>(
                this,
                static vm => vm.OnVisualPreferencesChanged(),
                static h => VisualPreferencesService.Instance.PreferencesChanged -= h);
            VisualPreferencesService.Instance.PreferencesChanged += _visualPreferencesChangedHandler;
        }

        private void UnsubscribeAdaptiveTintEvents()
        {
            if (_visualPreferencesChangedHandler != null)
            {
                VisualPreferencesService.Instance.PreferencesChanged -= _visualPreferencesChangedHandler;
                _visualPreferencesChangedHandler = null;
            }
        }

        private void OnVisualPreferencesChanged()
        {
            AppDispatcher.Post(() =>
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;

                if (!VisualPreferencesService.Instance.EnableAdaptiveGameTint)
                {
                    Interlocked.Increment(ref _adaptiveColorGeneration);
                    ServerListOperationHelper.ReplaceCts(ref _adaptiveTintCts, createNew: false, "LaunchBar.PreferencesDisabled");
                    ClearAdaptiveGameColor(forceResetThemeService: true);
                    return;
                }

                Bitmap? thumb = _currentGameThumbnail;
                if (thumb != null && (!ReferenceEquals(_lastExtractedThumbnail, thumb) || !_adaptiveGameColor.HasValue))
                {
                    long placeId = AccountLauncherService.ParsePlaceId(_placeId);
                    long lookupGen = Volatile.Read(ref _gameLookupGeneration);
                    long tintGen = Interlocked.Increment(ref _adaptiveColorGeneration);
                    var cts = ServerListOperationHelper.ReplaceCts(ref _adaptiveTintCts, createNew: true, "LaunchBar.PreferencesEnabled")!;
                    CancellationToken ct = cts.Token;
                    Task extractTask = Task.Run(() =>
                    {
                        try
                        {
                            ExtractAndPostAdaptiveColor(thumb, placeId, lookupGen, tintGen, ct);
                        }
                        catch (OperationCanceledException) { /* extraction canceled */ }
                        catch (Exception ex) { _logger.Debug($"[OnVisualPreferencesChanged] Adaptive color extraction failed: {ex.Message}"); }
                        finally
                        {
                            ServerListOperationHelper.ReleaseFinishedCts(ref _adaptiveTintCts, cts, "LaunchBar.PreferencesEnabled");
                        }
                    }, ct);
                    Volatile.Write(ref _pendingAdaptiveColorTask, extractTask);
                }
            });
        }

        internal Task ApplyThumbnailAndUpdateAdaptiveColorAsync(Bitmap? thumbnail)
        {
            CurrentGameThumbnail = thumbnail;
            long lookupGen = Volatile.Read(ref _gameLookupGeneration);
            long tintGen = Interlocked.Increment(ref _adaptiveColorGeneration);
            ServerListOperationHelper.ReplaceCts(ref _adaptiveTintCts, createNew: false, "LaunchBar.ApplyThumbnail.Tint");

            if (thumbnail == null || !VisualPreferencesService.Instance.EnableAdaptiveGameTint)
            {
                ServerListOperationHelper.ReplaceCts(ref _gameLookupCts, createNew: false, "LaunchBar.ApplyThumbnail");
                ClearAdaptiveGameColor(forceResetThemeService: true);
                return Task.CompletedTask;
            }

            long placeId = AccountLauncherService.ParsePlaceId(_placeId);
            var cts = ServerListOperationHelper.ReplaceCts(ref _gameLookupCts, createNew: true, "LaunchBar.ApplyThumbnail")!;
            CancellationToken ct = cts.Token;

            Task extractTask = Task.Run(() =>
            {
                try
                {
                    ExtractAndPostAdaptiveColor(thumbnail, placeId, lookupGen, tintGen, ct);
                }
                catch (OperationCanceledException) { /* extraction canceled */ }
                catch (Exception ex) { _logger.Debug($"[ApplyThumbnailAndUpdateAdaptiveColorAsync] {ex.Message}"); }
                finally
                {
                    ServerListOperationHelper.ReleaseFinishedCts(ref _gameLookupCts, cts, "LaunchBar.ApplyThumbnail");
                }
            }, ct);

            Volatile.Write(ref _pendingAdaptiveColorTask, extractTask);
            return extractTask;
        }

        private void ExtractAndPostAdaptiveColor(
            Bitmap thumbnail,
            long placeId,
            long lookupGen,
            long tintGen,
            CancellationToken ct)
        {
            if (ct.IsCancellationRequested ||
                Volatile.Read(ref _disposed) != 0 ||
                Volatile.Read(ref _gameLookupGeneration) != lookupGen ||
                Volatile.Read(ref _adaptiveColorGeneration) != tintGen ||
                !VisualPreferencesService.Instance.EnableAdaptiveGameTint)
            {
                return;
            }

            Color? extracted = AdaptivePaletteExtractor.ExtractAccentFromBitmap(thumbnail);
            if (ct.IsCancellationRequested ||
                Volatile.Read(ref _disposed) != 0 ||
                Volatile.Read(ref _gameLookupGeneration) != lookupGen ||
                Volatile.Read(ref _adaptiveColorGeneration) != tintGen)
            {
                return;
            }

            AppDispatcher.Post(() =>
            {
                if (Volatile.Read(ref _disposed) != 0 ||
                    ct.IsCancellationRequested ||
                    Volatile.Read(ref _gameLookupGeneration) != lookupGen ||
                    Volatile.Read(ref _adaptiveColorGeneration) != tintGen ||
                    AccountLauncherService.ParsePlaceId(PlaceId) != placeId ||
                    !ReferenceEquals(CurrentGameThumbnail, thumbnail))
                {
                    return;
                }

                ApplyOrClearAdaptiveColorOnUi(thumbnail, extracted, tintGen);
            });
        }

        internal long AdaptiveColorGenerationForTest => Volatile.Read(ref _adaptiveColorGeneration);

        internal void ApplyOrClearAdaptiveColorOnUi(Bitmap thumbnail, Color? extractedColor, long expectedTintGen)
        {
            if (!VisualPreferencesService.Instance.EnableAdaptiveGameTint)
            {
                ClearAdaptiveGameColor(forceResetThemeService: true);
                return;
            }

            if (Volatile.Read(ref _adaptiveColorGeneration) != expectedTintGen)
                return;

            _lastExtractedThumbnail = thumbnail;
            _heroAuraBrush = BuildHeroAuraBrush(extractedColor);
            AdaptiveGameColor = extractedColor;

            if (extractedColor.HasValue)
            {
                Volatile.Write(ref _ownsAdaptiveAccent, 1);
                AccentThemeService.SetAdaptiveGameColor(extractedColor);
            }
            else if (Interlocked.Exchange(ref _ownsAdaptiveAccent, 0) != 0)
            {
                AccentThemeService.SetAdaptiveGameColor(null);
            }
        }

        private void ClearAdaptiveGameColor(bool forceResetThemeService)
        {
            _lastExtractedThumbnail = null;
            bool hadAura = !ReferenceEquals(_heroAuraBrush, Brushes.Transparent);
            _heroAuraBrush = Brushes.Transparent;
            bool hadColor = _adaptiveGameColor.HasValue;
            AdaptiveGameColor = null;
            if (!hadColor && hadAura)
                OnPropertyChanged(nameof(HeroAuraBrush));

            if (Interlocked.Exchange(ref _ownsAdaptiveAccent, 0) != 0 ||
                (forceResetThemeService && AccentThemeService.AdaptiveGameColor.HasValue))
            {
                AccentThemeService.SetAdaptiveGameColor(null);
            }
        }

        private static IBrush BuildHeroAuraBrush(Color? adaptiveColor)
        {
            if (!adaptiveColor.HasValue)
                return Brushes.Transparent;

            var palette = OklchColorMath.DeriveAdaptiveAccentPalette(adaptiveColor.Value);
            var start = new RelativePoint(0.0, 0.0, RelativeUnit.Relative);
            var end = new RelativePoint(1.0, 1.0, RelativeUnit.Relative);
            return new ImmutableLinearGradientBrush(
                [
                    new ImmutableGradientStop(0.0, Color.FromArgb(56, palette.Primary.R, palette.Primary.G, palette.Primary.B)),
                    new ImmutableGradientStop(0.55, Color.FromArgb(24, palette.Hover.R, palette.Hover.G, palette.Hover.B)),
                    new ImmutableGradientStop(1.0, Color.FromArgb(0, palette.Primary.R, palette.Primary.G, palette.Primary.B))
                ],
                startPoint: start,
                endPoint: end);
        }
    }
}
