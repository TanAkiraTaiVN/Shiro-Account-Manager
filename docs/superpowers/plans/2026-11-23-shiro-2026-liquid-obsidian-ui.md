# Shiro 2026 — Liquid Obsidian UI/UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upgrade Shiro Account Manager to a flagship 2026 desktop experience ("Liquid Obsidian") combining GPU SkSL shaders, localized frosted glass, OKLCH/APCA color science, render-thread spring motion, collapsible sidebar rail, Command Palette 2.0 live preview, adaptive game-tinted Launch Dock, marquee selection, non-modal slide-over Account Inspector drawer, actionable Notification Center with Undo, density modes, and an Appearance Studio — with zero new NuGet dependencies and zero idle GPU/CPU burn.

**Architecture:** Build an autonomous GPU rendering and perceptual color layer (`OklchColorMath`, `VisualPreferencesService`, `SkiaGlassSurface`, `SkslAmbientMesh`, `SpringCompositionHelper`) directly atop Avalonia 12.1.6 Composition APIs and SkiaSharp 3.119.4, preserving `RedirectionSurface` and zero-allocation virtualized list rows. Decompose new UI surfaces (`CommandPaletteOverlayPanel`, `DashboardGameHeroChip`, `DashboardAccountDrawer`, `NotificationCenterPanel`, `SettingsAppearancePanel`, `ServerListWatcherPanel`) into dedicated `UserControl` panels and partial ViewModels so every file stays strictly inside `ArchitectureTests` line budgets.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.19041.0`, `win-x64`), C# latest, Avalonia `12.1.6`, `Avalonia.Skia 12.1.6`, `SkiaSharp 3.119.4`, `Froststrap.LucideAvalonia 2.0.1`, `Avalonia.Fonts.Inter`, `xunit.v3 3.2.2`, `NSubstitute 6.2.0`.

**Spec:** `docs/superpowers/specs/2026-11-23-shiro-2026-liquid-obsidian-ui-design.md`

**Baseline (`main`):** `01e2443`

## Global Constraints

- Zero new NuGet packages (`RestorePackagesWithLockFile=true` in `src/Shiro/Shiro.csproj` and `tests/Shiro.Tests/Shiro.Tests.csproj`).
- Every `.cs` file in `src/Shiro/` MUST stay `<= 485` lines; partial classes MUST total `<= 1150` lines; every `.cs` file in `tests/Shiro.Tests/` MUST stay `<= 550` lines and live in a subfolder (`Ui/`, `Architecture/`, etc.).
- Every `.axaml` file in `src/Shiro/` MUST stay `<= 400` lines and every single line MUST be `<= 200` characters.
- Every non-localization `{DynamicResource Key}` in `.axaml` MUST end with `Brush` or `Color`, or contain `Theme` or `Font`; static `CornerRadius` and `BoxShadows` MUST use `{StaticResource ...}`.
- Zero Vietnamese diacritics in C# comments (`// ...`) and zero hardcoded Vietnamese string literals in `.cs` files; every UI string key added to `en.json` MUST be added symmetrically to `vi.json` with identical `{N}` placeholders.
- Zero `async void` methods outside `RelayCommand.cs`; zero `+= async`, `Post(async ...)`, `new Action(async ...)`, `.Register(async ...)`; zero empty `catch {}` or untyped `catch (Exception) {}`.
- `TreatWarningsAsErrors=true`, `Nullable=enable`, `x:CompileBindings="True"`, and zero Avalonia binding errors (`BindingErrorSink` must stay empty).
- Never modify `Program.cs` to enable `WinUIComposition` (`RedirectionSurface` is locked by `PerformanceAndStaleJobIdRegressionTests`).
- Preserve `Transitions="{x:Null}"` on virtualized row/card buttons (`FastIconBtn`, `FastPillBtn`), checkboxes, and progress bars, and keep frozen `ImmutableSolidColorBrush`es in `AccountViewModel.Styling.cs`.
- Preserve all tested `x:Name` controls (`CardRoot` as `Border`, `BtnDelete`, `ChkSelect`, `TxtSearch`, `TxtPlaceId`, `CmbGroups`, `CardsItemsControl`, `TableItemsControl`, `TxtJobId`, `ViewDashboard..About`, `BtnNav*`, `IconMaximize`, `IconRestore`, `TxtCommandPalette`, `ViewOverlays`, and all 8 dialog `x:Name`s), all 5 required panels in `Views/Panels/`, `v5.0.0` in `AboutView.axaml`, and `29` items in `CommandPaletteCatalog.GetBuiltinCommands()`.
- Never use `Icon="Trash2"` (`IconSource == null` in `Froststrap.LucideAvalonia 2.0.1`); use `Icon="Trash"`.
- In Windows PowerShell 5.1, separate commands with `;`, never `&&`.

## File Structure & Line-Budget Guardrail Map

Measured baseline line counts show several files already close to budgets; respect these splits strictly:
- `SettingsViewModel.cs` (`483/485` lines) -> add Task 12 properties/commands ONLY in new partial `src/Shiro/ViewModels/Dialogs/SettingsViewModel.Appearance.cs`.
- `MainWindow.axaml.cs` (`460`) + `MainWindow.Navigation.cs` (`475`) = `935/1150` lines -> trim superseded transition helpers in `MainWindow.Navigation.cs` when delegating to `SpringCompositionHelper`, and place Task 4/9 chrome, sidebar rail, notification bell, and `Esc` routing in `src/Shiro/AvaloniaUI/Views/MainWindow.Chrome2026.cs` (`<= 170` lines).
- `MainWindow.axaml` (`387/400` lines) -> extract Command Palette modal into `Panels/CommandPaletteOverlayPanel.axaml` while keeping `MainWindow.FindControl<TextBox>("TxtCommandPalette")` working or keeping `CommandPaletteOverlayPanel` inside `MainWindow` scope.
- `SettingsView.axaml` (`393/400` lines) -> extract Appearance Studio into `Panels/SettingsAppearancePanel.axaml`.
- `ServerListView.axaml` (`375/400` lines) -> extract Watcher tab into `Panels/ServerListWatcherPanel.axaml`.
- `DashboardView.axaml` (`359/400` lines) -> keep `TxtSearch`, `TxtPlaceId`, `TxtJobId`, `CmbGroups`, `CardsItemsControl`, `TableItemsControl` directly in `DashboardView.axaml`, and place new Hero preview chip / Drawer / Notification popover in `Panels/` (`DashboardGameHeroChip.axaml`, `DashboardAccountDrawer.axaml`, `NotificationCenterPanel.axaml`).

## Review Focus

- **Headless & Software Renderer Fallback (pinned to Task 2 & Task 3 tests):** `SkiaGlassSurface` and `SkslAmbientMesh` fall back cleanly to immutable gradient brushes without exceptions when `ISkiaSharpApiLeaseFeature` or `SKRuntimeEffect` is absent.
- **HiDPI Shader & Marquee Coordinate Mapping (pinned to Task 2, Task 3 & Task 7 tests):** Shader pixel coordinates (`RenderScaling` 1.0x vs 1.25x) and marquee intersection bounds remain exact across scaling factors.
- **Esc Layer Ordering & Overlay Collision (pinned to Task 9 & Task 10 tests):** `Esc` closes only the single topmost overlay (`CommandPalette -> NotificationCenter -> AccountDrawer -> Selection`), and toasts shift left of `DashboardAccountDrawer` (`Width=340`) while clearing centered `FastLoginDialog` (`Width=500`).
- **Marquee Selection vs. Virtualized Row Gestures (pinned to Task 7 tests):** Dragging `< 6px` or starting on interactive controls (`Button`, `CheckBox`, `TextBox`, `ComboBox`, `ScrollBar`, `Thumb`) preserves click/Shift+Click/double-tap/context-menu behavior; `ApplySelectionBatch` fires one count notification across 200+ accounts.
- **Skia & Composition Native Resource Disposal (pinned to Task 2, Task 3 & Task 13 tests):** Repeated attach/detach cycles dispose `SKShader`, `SKImageFilter`, `SKRuntimeEffect`, `SKPaint`, and `CompositionCustomVisual` cleanly with zero timer leaks and zero idle redraw loops.

---

## Phase 1 — OKLCH Color System, Adaptive Palette & GPU Rendering Engine

### Task 1: OKLCH Color Math, APCA Contrast Gate, Adaptive Palette Extractor & `VisualPreferencesService`

**Files:**
- Create: `src/Shiro/Services/Ui/OklchColorMath.cs`
- Create: `src/Shiro/Services/Ui/AdaptivePaletteExtractor.cs`
- Create: `src/Shiro/Services/Ui/VisualPreferencesService.cs`
- Modify: `src/Shiro/Services/Storage/AppSettingsKeys.cs:64-66`
- Modify: `src/Shiro/Services/Ui/AccentThemeService.cs:11-238`
- Modify: `src/Shiro/AvaloniaUI/Styles/ThemeTokens.axaml:96-107`
- Test: `tests/Shiro.Tests/Ui/OklchAndVisualPreferencesTests.cs`

**Interfaces:**
- Consumes: `ISettingsService`, `AppServices.GeneralSettings`, `AppServices.HostUiBridge.QueueDeferredSaveSettings()`
- Produces:
  - `public enum SurfacePreset { LiquidObsidian = 0, MidnightGlass = 1, AmoledVoid = 2, WarmGraphite = 3 }`
  - `public enum UiDensityMode { Compact = 0, Balanced = 1, Comfortable = 2 }`
  - `public static class OklchColorMath` (`ToColor(double l, double c, double h, byte alpha = 255)`, `FromColor(Color srgb)`, `CalculateApcaContrast(Color text, Color background)`, `SelectReadableForeground(Color background)`, `DeriveSurfacePalette(SurfacePreset preset)`)
  - `public static class AdaptivePaletteExtractor` (`ExtractAccentFromPixels(ReadOnlySpan<byte> bgra32, int width, int height) : Color?`)
  - `public sealed class VisualPreferencesService` (singleton `Instance`, `SurfacePreset`, `DensityMode`, `EnableGlassEffects`, `EnableAdaptiveGameTint`, `ReducedMotion`, `SidebarCollapsed`, `Load()`, `ApplyAndSave(...)`, `event Action? PreferencesChanged`)
  - `AccentThemeService.ApplySurfacePreset(SurfacePreset preset, bool save = true)` and `AccentThemeService.SetAdaptiveGameColor(Color? gameColor)` updating all `Surface*Brush`, `Border*Brush`, and `Accent*Brush` keys inside `CreateThemeResourceDictionary`
  - Static concentric radii in `ThemeTokens.axaml`: `RadiusOuter16`, `RadiusMid12`, `RadiusInner8`, `RadiusChip6`

- [ ] **Step 1: Write the failing test**

Create `tests/Shiro.Tests/Ui/OklchAndVisualPreferencesTests.cs` with `[Collection("SerialGlobalState")]`:
- `OklchRoundTrip_PreservesSrgbChannelWithinTolerance`: converts sample sRGB colors (`#0B0B0F`, `#7C3AED`, `#06B6D4`, `#10B981`, `#DB2777`) through `FromColor` -> `ToColor` and asserts per-channel delta `<= 2`.
- `ApcaContrast_AllPresetsAndAccents_MeetMinimumButtonAndTextContrast`: iterates all 4 `SurfacePreset` values and 5 `AccentTheme` values, asserts `Math.Abs(OklchColorMath.CalculateApcaContrast(palette.TextOnAccent, palette.Primary)) >= 60.0` and `>= 75.0` for `TextPrimary` on `SurfaceCard`.
- `AdaptivePaletteExtractor_HandlesSolidAndNeutralBitmaps_AndClampsChroma`: verifies `ExtractAccentFromPixels` returns `null` for neutral grey/black/empty spans and returns an OKLCH-clamped color (`L in [0.55, 0.75]`) for vivid blue/gold pixels.
- `VisualPreferencesService_PersistsAndRestoresPreferences_ViaISettingsService`: overrides `ISettingsService` with NSubstitute mock, changes all 6 preferences, verifies `AppSettingsKeys.General.*` keys are written and parsed cleanly on `Load()`.
- `AccentThemeService_ApplySurfacePreset_UpdatesSurfaceBrushesAndAdaptiveOverride`: initializes Avalonia app resources, switches `ApplySurfacePreset(SurfacePreset.AmoledVoid, save: false)`, asserts `SurfaceCanvasBrush` luminance is darker than `WarmGraphite`, then calls `SetAdaptiveGameColor(Color.FromRgb(255, 140, 0))` and `SetAdaptiveGameColor(null)` and verifies `AccentAmbientBrush` updates and restores cleanly.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\tests\Shiro.Tests\Shiro.Tests.csproj" --no-restore --filter "FullyQualifiedName~OklchAndVisualPreferencesTests"`
Expected: FAIL with compile errors (`OklchColorMath`, `AdaptivePaletteExtractor`, `VisualPreferencesService`, `SurfacePreset`, `UiDensityMode` do not exist).

- [ ] **Step 3: Implement OKLCH/APCA math, palette extractor, visual preferences, and dynamic surface presets**

- Add constants in `AppSettingsKeys.General`: `SurfacePreset`, `UiDensityMode`, `EnableGlassEffects`, `EnableAdaptiveGameTint`, `ReducedMotion`, `SidebarCollapsed`.
- Implement `OklchColorMath.cs` using exact Ottosson OKLab/OKLCH matrices and APCA W3 0.0.98G-4g luminance constants (`SelectReadableForeground` evaluates `#09090B` vs `#F5F5F8` and picks higher `|Lc|`).
- Implement `AdaptivePaletteExtractor.cs` striding pixels (`<= 4096` samples) in hue buckets, filtering low-chroma/alpha pixels, and normalizing winning hue to `L=0.64, C=0.13` via `OklchColorMath.ToColor`.
- Implement `VisualPreferencesService.cs` with `Lock`, safe defaults (`LiquidObsidian`, `Balanced`, `EnableGlassEffects=true`, `EnableAdaptiveGameTint=true`, `ReducedMotion=false`, `SidebarCollapsed=false`), and `PreferencesChanged`.
- Extend `AccentThemeService.cs` (`ApplySurfacePreset`, `SetAdaptiveGameColor`, keeping `CreateThemeResourceDictionary` generating `Surface*Brush` + `Border*Brush` + `Text*Brush` + `Accent*Brush` as `ImmutableSolidColorBrush`/`ImmutableLinearGradientBrush`/`ImmutableRadialGradientBrush` while staying `<= 450` lines — split color math helpers into `OklchColorMath.cs`).
- Add `RadiusOuter16` (`16`), `RadiusMid12` (`12`), `RadiusInner8` (`8`), `RadiusChip6` (`6`) to `ThemeTokens.axaml`.

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet build "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore -warnaserror; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore --no-build --filter "FullyQualifiedName~OklchAndVisualPreferencesTests|FullyQualifiedName~AccentThemeServiceTests|FullyQualifiedName~ArchitectureTests"`
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Ui/OklchColorMath.cs src/Shiro/Services/Ui/AdaptivePaletteExtractor.cs src/Shiro/Services/Ui/VisualPreferencesService.cs src/Shiro/Services/Storage/AppSettingsKeys.cs src/Shiro/Services/Ui/AccentThemeService.cs src/Shiro/AvaloniaUI/Styles/ThemeTokens.axaml tests/Shiro.Tests/Ui/OklchAndVisualPreferencesTests.cs; git commit -m "feat(ui): add OKLCH/APCA color engine, surface presets, and visual preferences"
```

---

### Task 2: `SkiaGlassSurface` — Localized GPU Frosted Glass, Micro-Noise Grain & Specular Bevel

**Files:**
- Create: `src/Shiro/AvaloniaUI/Controls/SkiaGlassSurface.cs`
- Test: `tests/Shiro.Tests/Ui/SkiaGlassSurfaceTests.cs`

**Interfaces:**
- Consumes: `VisualPreferencesService.Instance.EnableGlassEffects`, `VisualPreferencesService.PreferencesChanged`, `ISkiaSharpApiLeaseFeature`
- Produces:
  - `public class SkiaGlassSurface : Border` in `Shiro.AvaloniaUI.Controls`
  - `StyledProperty<double> BlurSigmaProperty` (`defaultValue: 18.0`, coerced `[0, 48]`)
  - `StyledProperty<IBrush?> TintBrushProperty`
  - `StyledProperty<double> NoiseOpacityProperty` (`defaultValue: 0.024`, coerced `[0, 0.15]`)
  - `StyledProperty<double> SpecularHighlightOpacityProperty` (`defaultValue: 0.20`, coerced `[0, 1]`)
  - `StyledProperty<bool> EnablePointerSpecularProperty` (`defaultValue: true`)
  - `internal bool LastRenderUsedFallback { get; private set; }` (observable flag for unit tests verifying headless/disabled matte fallback)

- [ ] **Step 1: Write the failing test**

Create `tests/Shiro.Tests/Ui/SkiaGlassSurfaceTests.cs`:
- `SkiaGlassSurface_AttachDetachAndPointerCycle_DoesNotThrow_AndTracksFallback`: mounts `SkiaGlassSurface` inside a headless `Window`, sets `BlurSigma = 20`, raises `PointerMoved`/`PointerExited`, toggles `VisualPreferencesService.Instance.EnableGlassEffects = false` and back to `true`, renders via `RenderTargetBitmap`, asserts `LastRenderUsedFallback == true` when `EnableGlassEffects == false` (and on headless stub without Skia lease), and detaches cleanly without exceptions.
- `SkiaGlassSurface_CoercesPropertyBounds_AndHandlesZeroSize`: asserts `BlurSigma = -5` coerces to `0`, `NoiseOpacity = 2.0` coerces to `0.15`, and `Render` on `0x0` bounds exits safely.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\tests\Shiro.Tests\Shiro.Tests.csproj" --no-restore --filter "FullyQualifiedName~SkiaGlassSurfaceTests"`
Expected: FAIL (`SkiaGlassSurface` missing).

- [ ] **Step 3: Implement `SkiaGlassSurface.cs` (`<= 340` lines)**

- Implement an internal `ICustomDrawOperation` (`GlassDrawOperation`) executing during `context.Custom(...)` inside `Render(DrawingContext)`.
- Lease `ISkiaSharpApiLeaseFeature` via `context.TryGetFeature<ISkiaSharpApiLeaseFeature>()`. When `EnableGlassEffects == false` or `lease == null` or `SkCanvas == null`: set `LastRenderUsedFallback = true` and paint clean rounded-rect fallback tint + top specular line.
- When GPU Skia canvas/surface is available: clip to `SKRoundRect` matching `CornerRadius`, sample downscaled (`50%`) backdrop via `lease.SkSurface?.Snapshot()` (if `SkSurface != null`) or `SaveLayer` with cached `SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp)`, overlay static shared `SKShader.CreatePerlinNoiseFractalNoise(0.65f, 0.65f, 2, 0)` at `NoiseOpacity`, and draw a `1px` specular bevel highlight shifted gently toward pointer X when `EnablePointerSpecular` is true.
- Dispose cached `SKImageFilter` whenever `BlurSigma` changes or control detaches (`OnDetachedFromVisualTree`), and unsubscribe `VisualPreferencesService.PreferencesChanged`.

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet build "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore -warnaserror; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore --no-build --filter "FullyQualifiedName~SkiaGlassSurfaceTests|FullyQualifiedName~ArchitectureTests"`
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/AvaloniaUI/Controls/SkiaGlassSurface.cs tests/Shiro.Tests/Ui/SkiaGlassSurfaceTests.cs; git commit -m "feat(ui): add SkiaGlassSurface GPU frosted glass control with matte fallback"
```

---

### Task 3: `SkslAmbientMesh` (Render-Thread Shader) & `SpringCompositionHelper` (120Hz Physics)

**Files:**
- Create: `src/Shiro/AvaloniaUI/Controls/SkslAmbientMesh.cs`
- Create: `src/Shiro/AvaloniaUI/Controls/SpringCompositionHelper.cs`
- Modify: `src/Shiro/AvaloniaUI/Controls/AmbientBackdropLayer.cs:1-235`
- Test: `tests/Shiro.Tests/Ui/CompositionAndShaderTests.cs`

**Interfaces:**
- Consumes: `AccentThemeService.ThemeChanged`, `AccentThemeService.GetExtendedColors`, `VisualPreferencesService.Instance`
- Produces:
  - `public class SkslAmbientMesh : Control` (`IsHitTestVisible = false`, hosts `CompositionCustomVisualHandler` executing a compiled `SKRuntimeEffect` 3-orb mesh + triangular dithering SkSL fragment shader; runs a `450ms` cubic color-lerp transition on Theme/Game color change and stops calling `RegisterForNextAnimationFrameUpdate()` once `progress >= 1.0`, guaranteeing **0% idle GPU**; disposes `SKRuntimeEffect`/`SKShader`/`SKPaint` cleanly on detach/dispose message)
  - `public static class SpringCompositionHelper`:
    - `void AttachImplicitOffsetAndOpacity(Visual target, double durationMs = 240)`
    - `void RunSpringEntrance(Visual target, double offsetX, double offsetY, double fromScale = 0.96, double durationMs = 260)` (creates `Vector3KeyFrameAnimation` / `ScalarKeyFrameAnimation` via `ElementComposition.GetElementVisual(target)?.Compositor`, or snaps immediately to target values when `VisualPreferencesService.Instance.ReducedMotion` is true or `ElementComposition` is unavailable in headless)
  - Update `AmbientBackdropLayer` so it delegates smoothly to `SkslAmbientMesh` when GPU shaders are enabled while keeping all existing `StyledProperty` signatures (`PrimaryOrbOpacityScale`, `SecondaryOrbOpacityScale`, `VignetteOpacity`) intact.

- [ ] **Step 1: Write the failing test**

Create `tests/Shiro.Tests/Ui/CompositionAndShaderTests.cs`:
- `SkslAmbientMesh_ShaderCompilesCleanly_WithoutErrors`: invokes `SkslAmbientMesh.TryCompileShader(out string? error)` and asserts `error` is `null` and returned `SKRuntimeEffect` is non-null (and disposes it).
- `SkslAmbientMesh_ColorTransition_SettlesToIdleAfter450Ms`: drives `SkslAmbientMeshHandler` tick progression from `t=0` to `t=500ms` after a theme change and asserts `IsAnimating == false` after settling (verifying 0% idle render loop).
- `SpringCompositionHelper_RespectsReducedMotion_AndNullVisualSafe`: passes `Visual` under `ReducedMotion = true` and `ReducedMotion = false` (plus unattached control), asserts zero exceptions and final `Opacity == 1.0`.
- `AmbientBackdropLayer_AttachDetach_UnsubscribesThemeChanged`: preserves existing subscriber leak test guarantee (`1 -> 0` subscribers on detach).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\tests\Shiro.Tests\Shiro.Tests.csproj" --no-restore --filter "FullyQualifiedName~CompositionAndShaderTests"`
Expected: FAIL (`SkslAmbientMesh` and `SpringCompositionHelper` missing).

- [ ] **Step 3: Implement `SkslAmbientMesh.cs`, `SpringCompositionHelper.cs`, and wire `AmbientBackdropLayer.cs`**

Use this SkSL shader inside `SkslAmbientMesh.cs` (note: pass physical pixel resolution `size * scaling` or scale `fragCoord` by local canvas matrix so DPI 100% / 125% / 150% stay invariant):
```sksl
uniform float2 u_resolution;
uniform float4 u_orb1;
uniform float4 u_orb2;
uniform float4 u_base;
uniform float  u_phase;

vec4 main(vec2 fragCoord) {
    vec2 uv = fragCoord / max(u_resolution, vec2(1.0));
    float d1 = length((uv - vec2(0.18 + 0.03 * sin(u_phase * 3.14159), 0.14)) * vec2(1.0, 1.3));
    float d2 = length((uv - vec2(0.84, 0.82 + 0.03 * cos(u_phase * 3.14159))) * vec2(1.1, 1.2));
    float w1 = exp(-d1 * d1 * 4.2);
    float w2 = exp(-d2 * d2 * 3.6);
    vec3 col = u_base.rgb + u_orb1.rgb * (u_orb1.a * w1) + u_orb2.rgb * (u_orb2.a * w2);
    float dither = (fract(sin(dot(fragCoord, vec2(12.9898, 78.233))) * 43758.5453) - 0.5) / 255.0;
    return vec4(clamp(col + dither, 0.0, 1.0), 1.0);
}
```
Implement `CompositionCustomVisualHandler.OnMessage` handling `MeshTransitionMessage`, `MeshSizeMessage`, and `MeshDisposeMessage`. Ensure `SkslAmbientMesh` falls back to `DrawingContext` gradient fills if `SKRuntimeEffect` or `ISkiaSharpApiLeaseFeature` is unavailable (or `EnableGlassEffects == false`).

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet build "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore -warnaserror; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore --no-build --filter "FullyQualifiedName~CompositionAndShaderTests|FullyQualifiedName~ArchitectureTests"`
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/AvaloniaUI/Controls/SkslAmbientMesh.cs src/Shiro/AvaloniaUI/Controls/SpringCompositionHelper.cs src/Shiro/AvaloniaUI/Controls/AmbientBackdropLayer.cs tests/Shiro.Tests/Ui/CompositionAndShaderTests.cs; git commit -m "feat(ui): add SkslAmbientMesh render-thread shader and SpringCompositionHelper"
```

---

## Phase 2 — Collapsible Rail Shell & Command Palette 2.0

### Task 4: Collapsible Icon Rail Sidebar (`215px <-> 64px`, `Ctrl+B`) & Chrome Telemetry

**Files:**
- Create: `src/Shiro/AvaloniaUI/Views/MainWindow.Chrome2026.cs` (`<= 170` lines)
- Modify: `src/Shiro/ViewModels/MainViewModel.cs:25-110` (`IsNotificationCenterOpen`, `UnreadNotificationCount`, `HasUnreadNotifications` shell state properties wired to `NotificationHistoryService` in Task 10 so Phases 2–4 compile standalone without forward references)
- Modify: `src/Shiro/AvaloniaUI/Views/MainWindow.axaml:1-387` (slim under 310 lines by delegating Command Palette overlay in Task 5, or prep rail structure cleanly)
- Modify: `src/Shiro/AvaloniaUI/Views/MainWindow.axaml.cs:1-85`
- Modify: `src/Shiro/AvaloniaUI/Views/MainWindow.Navigation.cs:1-150`
- Modify: `src/Shiro/Resources/Strings/en.json`, `src/Shiro/Resources/Strings/vi.json`
- Test: `tests/Shiro.Tests/Ui/SidebarRailAndNavigationTests.cs`

**Interfaces:**
- Consumes: `VisualPreferencesService.SidebarCollapsed`, `SpringCompositionHelper`
- Produces:
  - `MainViewModel.IsNotificationCenterOpen` (`bool`), `UnreadNotificationCount` (`int`), `HasUnreadNotifications` (`bool`)
  - Sidebar toggle button `BtnToggleSidebar` (`Ctrl+B` shortcut) collapsing `SidebarColumn.Width` between `215` and `64` (`GridLength`), hiding `.NavLabel` text blocks and shrinking `NavPill` padding cleanly while preserving `BtnNavDashboard`, `BtnNavServerList`, `BtnNavRecentGames`, `BtnNavSettings`, `BtnNavAbout` in `MainWindow` NameScope.
  - Titlebar bell button `BtnNotificationBell` toggling `IsNotificationCenterOpen` and displaying `UnreadNotificationCount` badge when `HasUnreadNotifications == true`.
  - Localization keys in both `en.json` and `vi.json`: `Nav_ToggleRailTip`, `Nav_NotificationsTip`.

- [ ] **Step 1: Write the failing test**

Create `tests/Shiro.Tests/Ui/SidebarRailAndNavigationTests.cs` (`[AvaloniaFact]`):
- `MainWindow_ToggleSidebarRail_UpdatesWidthPersistence_AndPreservesNavButtons`: constructs `MainWindow`, verifies all 5 `BtnNav*` buttons exist, executes `ToggleSidebarRail()`, asserts sidebar width switches `215 -> 64` and `VisualPreferencesService.Instance.SidebarCollapsed == true`, toggles back to `215`, and verifies `Ctrl+B` `KeyDown` triggers the toggle.
- `MainWindow_Navigation_StillUpdatesIsVisibleSynchronously_WithSpringMotion`: navigates through `ServerList -> RecentGames -> Settings -> About -> Accounts` and asserts `View*.IsVisible` updates synchronously on every step with zero `BindingErrorSink` errors.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\tests\Shiro.Tests\Shiro.Tests.csproj" --no-restore --filter "FullyQualifiedName~SidebarRailAndNavigationTests"`
Expected: FAIL (`ToggleSidebarRail` / `BtnToggleSidebar` not implemented).

- [ ] **Step 3: Implement `MainWindow.Chrome2026.cs`, update `MainWindow.axaml`, `MainWindow.Navigation.cs`, and localization JSONs**

- Move redundant page transition/chrome animation code out of `MainWindow.Navigation.cs` into `SpringCompositionHelper` calls inside `MainWindow.Chrome2026.cs` so `MainWindow.axaml.cs + MainWindow.Navigation.cs + MainWindow.Chrome2026.cs` stays `<= 1050` lines (comfortably under the `1150` partial budget!).
- Add `Nav_ToggleRailTip` and `Nav_NotificationsTip` to both `en.json` and `vi.json`.
- Wire `Ctrl+B` in `MainWindow.Chrome2026.cs` (called from `OnKeyDown`).

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet build "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore -warnaserror; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore --no-build --filter "FullyQualifiedName~SidebarRailAndNavigationTests|FullyQualifiedName~P0UiAndViewModelRegressionTests|FullyQualifiedName~DialogSmokeTests|FullyQualifiedName~ArchitectureTests|FullyQualifiedName~LocalizationIntegrityTests"`
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/AvaloniaUI/Views/MainWindow.axaml src/Shiro/AvaloniaUI/Views/MainWindow.axaml.cs src/Shiro/AvaloniaUI/Views/MainWindow.Navigation.cs src/Shiro/AvaloniaUI/Views/MainWindow.Chrome2026.cs src/Shiro/Resources/Strings/en.json src/Shiro/Resources/Strings/vi.json tests/Shiro.Tests/Ui/SidebarRailAndNavigationTests.cs; git commit -m "feat(ui): add collapsible icon rail sidebar and notification bell in MainWindow chrome"
```

---

### Task 5: Command Palette 2.0 (2-Column Live Preview Pane, Scope Filters & Recent History)

**Files:**
- Create: `src/Shiro/AvaloniaUI/Views/Panels/CommandPaletteOverlayPanel.axaml`
- Create: `src/Shiro/AvaloniaUI/Views/Panels/CommandPaletteOverlayPanel.axaml.cs`
- Modify: `src/Shiro/ViewModels/Dashboard/CommandPaletteViewModel.cs:1-127`
- Modify: `src/Shiro/ViewModels/CommandPaletteCatalog.cs:64-120`
- Modify: `src/Shiro/ViewModels/CommandPaletteItemViewModel.cs:100-132`
- Modify: `src/Shiro/AvaloniaUI/Views/MainWindow.axaml` (embed `<panels:CommandPaletteOverlayPanel x:Name="ViewCommandPalette" />`)
- Modify: `src/Shiro/Resources/Strings/en.json`, `src/Shiro/Resources/Strings/vi.json`
- Test: `tests/Shiro.Tests/Ui/CommandPalette2Tests.cs`

**Interfaces:**
- Consumes: `SkiaGlassSurface`, `SpringCompositionHelper`, `CommandPaletteCatalog.GetBuiltinCommands()` (preserve `29` items!)
- Produces:
  - `public enum CommandPaletteScope { All = 0, Accounts = 1, Commands = 2 }`
  - `CommandPaletteViewModel.ActiveScope`, `CommandPaletteViewModel.RecentCommandIds` (capped at `5` MRU items shown when query is empty), `CommandPaletteViewModel.HasPreviewItem`, `CommandPaletteViewModel.SetScopeCommand`
  - `CommandPaletteItemViewModel.CommandId`, `PrimaryHintText`, `SecondaryHintText`, `PreviewStatusText`, `PreviewRobuxText`, `PreviewGroupText`
  - `CommandPaletteOverlayPanel` (`Width="720"`): left column (`430px`) with scope filter chips + results list; right column (`260px`) **Live Preview Pane** rendering large `52×52` avatar / icon badge, status/RAM/Robux or command description + shortcut hints (`Enter`, `Shift+Enter`, `Esc`), and exposing `public TextBox? SearchTextBox => TxtCommandPalette;`.

- [ ] **Step 1: Write the failing test**

Create `tests/Shiro.Tests/Ui/CommandPalette2Tests.cs`:
- `CommandPalette2_ScopeFiltering_RestrictsItemsToAccountsOrCommands`: loads 3 accounts, sets `ActiveScope = CommandPaletteScope.Accounts` (asserts all items `IsAccount`) and `CommandPaletteScope.Commands` (asserts all items `IsCommand`).
- `CommandPalette2_RecentHistory_PromotesExecutedCommands_WithoutBreaking29BuiltinCount`: executes 2 commands via `ExecuteCommandPaletteSelection`, reopens palette with empty search, verifies recent commands appear first, and invokes `ArchitectureTests` reflection check confirming `GetBuiltinCommands()` still has count `29`.
- `CommandPalette2_LivePreview_UpdatesSynchronouslyWithArrowNavigation`: moves selection `MoveCommandPaletteSelection(1)` and asserts `SelectedCommandPaletteItem` updates preview metadata cleanly.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\tests\Shiro.Tests\Shiro.Tests.csproj" --no-restore --filter "FullyQualifiedName~CommandPalette2Tests"`
Expected: FAIL (`CommandPaletteScope` / `ActiveScope` / `RecentCommandIds` missing).

- [ ] **Step 3: Implement Command Palette 2.0 ViewModels & `CommandPaletteOverlayPanel.axaml`**

- Add `ActiveScope` + MRU `RecentCommandIds` (max 5) to `CommandPaletteViewModel.cs` and `CommandPaletteCatalog.cs`.
- Build `CommandPaletteOverlayPanel.axaml` wrapped in `<controls:SkiaGlassSurface CornerRadius="{StaticResource RadiusOuter16}" ...>` (`<= 240` lines, `<= 200` chars/line).
- Wire `ViewCommandPalette.SearchTextBox` in `MainWindow.axaml.cs` / `MainWindow.Navigation.cs`.

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet build "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore -warnaserror; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore --no-build --filter "FullyQualifiedName~CommandPalette2Tests|FullyQualifiedName~UiUxUpgradeTests|FullyQualifiedName~ArchitectureTests"`
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/AvaloniaUI/Views/Panels/CommandPaletteOverlayPanel.axaml src/Shiro/AvaloniaUI/Views/Panels/CommandPaletteOverlayPanel.axaml.cs src/Shiro/ViewModels/Dashboard/CommandPaletteViewModel.cs src/Shiro/ViewModels/CommandPaletteCatalog.cs src/Shiro/ViewModels/CommandPaletteItemViewModel.cs src/Shiro/AvaloniaUI/Views/MainWindow.axaml src/Shiro/AvaloniaUI/Views/MainWindow.axaml.cs src/Shiro/AvaloniaUI/Views/MainWindow.Navigation.cs src/Shiro/Resources/Strings/en.json src/Shiro/Resources/Strings/vi.json tests/Shiro.Tests/Ui/CommandPalette2Tests.cs; git commit -m "feat(ui): add Command Palette 2.0 with live preview pane, scope tabs, and recent history"
```

---

## Phase 3 — Dashboard 2026: Adaptive Game Hero, Marquee Selection & Density/Telemetry

### Task 6: Dynamic Game Hero Launch Dock (`AdaptivePaletteExtractor` + Quick Window Tiling)

**Files:**
- Create: `src/Shiro/AvaloniaUI/Views/Panels/DashboardGameHeroChip.axaml`
- Create: `src/Shiro/AvaloniaUI/Views/Panels/DashboardGameHeroChip.axaml.cs`
- Modify: `src/Shiro/ViewModels/Dashboard/LaunchBarViewModel.cs:145-375`
- Modify: `src/Shiro/AvaloniaUI/Views/DashboardView.axaml` (replace compact game badge with `DashboardGameHeroChip` + Quick Tile dropdown button next to `Launch Home`/`Join Server`, keeping `TxtPlaceId` and `TxtJobId` directly inside `DashboardView.axaml`)
- Modify: `src/Shiro/Resources/Strings/en.json`, `src/Shiro/Resources/Strings/vi.json`
- Test: `tests/Shiro.Tests/Ui/AdaptiveGameHeroTests.cs`

**Interfaces:**
- Consumes: `AdaptivePaletteExtractor`, `AccentThemeService.SetAdaptiveGameColor`, `VisualPreferencesService.EnableAdaptiveGameTint`, `ToolsStatusViewModel.ArrangeRobloxWindowsCommand`, existing `LaunchBarViewModel` callbacks (`_syncPlaceIdToServerList`, `_navigateToPage`)
- Produces:
  - `LaunchBarViewModel.OpenCurrentGameServersCommand` (invokes existing `_syncPlaceIdToServerList?.Invoke(PlaceId)` and `_navigateToPage(NavigationPage.ServerList)` with zero constructor signature breaks)
  - `LaunchBarViewModel.AdaptiveGameColor` (`Color?`) updated safely off the UI thread from `CurrentGameThumbnail` (converting Avalonia `Bitmap` pixels via `CopyPixels` only when `VisualPreferencesService.Instance.EnableAdaptiveGameTint == true` and cancelling stale extractions via `_gameLookupCts`!)

- [ ] **Step 1: Write the failing test**

Create `tests/Shiro.Tests/Ui/AdaptiveGameHeroTests.cs`:
- `LaunchBarViewModel_ClearsAdaptiveGameColor_WhenPlaceIdClearedOrTintDisabled`: verifies `AdaptiveGameColor` resets to `null` when `PlaceId` becomes empty or `EnableAdaptiveGameTint == false`.
- `LaunchBarViewModel_OpenCurrentGameServersCommand_NavigatesToServerList`: sets `PlaceId = "2753915549"`, executes `OpenCurrentGameServersCommand`, asserts navigation callback receives `NavigationPage.ServerList`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\tests\Shiro.Tests\Shiro.Tests.csproj" --no-restore --filter "FullyQualifiedName~AdaptiveGameHeroTests"`
Expected: FAIL (`OpenCurrentGameServersCommand` / `AdaptiveGameColor` missing).

- [ ] **Step 3: Implement `DashboardGameHeroChip`, thumbnail color extraction, and Quick Tile button**

- Keep `LaunchBarViewModel.cs` `<= 450` lines (extract pixel-copy helper into `AdaptivePaletteExtractor` if needed).
- Update `DashboardView.axaml` Launch Dock keeping `TxtPlaceId`, `TxtJobId` in `DashboardView`'s NameScope and `<= 390` lines total.

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet build "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore -warnaserror; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore --no-build --filter "FullyQualifiedName~AdaptiveGameHeroTests|FullyQualifiedName~UiUxUpgradeTests|FullyQualifiedName~ArchitectureTests"`
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/AvaloniaUI/Views/Panels/DashboardGameHeroChip.axaml src/Shiro/AvaloniaUI/Views/Panels/DashboardGameHeroChip.axaml.cs src/Shiro/ViewModels/Dashboard/LaunchBarViewModel.cs src/Shiro/AvaloniaUI/Views/DashboardView.axaml src/Shiro/Resources/Strings/en.json src/Shiro/Resources/Strings/vi.json tests/Shiro.Tests/Ui/AdaptiveGameHeroTests.cs; git commit -m "feat(ui): add adaptive game hero dock with thumbnail palette tinting and quick window tiling"
```

---

### Task 7: Marquee Rubber-Band Selection & Keyboard-First Grid Navigation

**Files:**
- Create: `src/Shiro/AvaloniaUI/Controls/MarqueeSelectionOverlay.cs` (`<= 240` lines)
- Modify: `src/Shiro/ViewModels/Dashboard/AccountCollectionViewModel.SyncAndSelection.cs` (add `ApplySelectionBatch`)
- Modify: `src/Shiro/AvaloniaUI/Views/DashboardView.axaml` & `src/Shiro/AvaloniaUI/Views/DashboardView.axaml.cs`
- Test: `tests/Shiro.Tests/Ui/MarqueeAndKeyboardNavTests.cs`

**Interfaces:**
- Consumes: `DashboardView.IsInsideInteractiveControl`, `AccountCollectionViewModel`
- Produces:
  - `AccountCollectionViewModel.ApplySelectionBatch(IReadOnlyList<AccountViewModel> targets, bool additive)` (sets `_suppressCountNotifications = true` around loop, calls `NotifyCounts()` and `_onSelectionChanged?.Invoke()` exactly once!)
  - `public class MarqueeSelectionOverlay : Control` (`SelectionRect`, `IsSelecting`, `ThresholdPixels = 6.0`, draws `AccentSoftFillBrush` + `1px AccentBorderBrush` rounded `6px`, ignores pointer down over `Button`, `CheckBox`, `TextBox`, `ComboBox`, `ScrollBar`, `Thumb`).
  - `DashboardView` keyboard handlers for `ArrowUp/Down/Left/Right`, `Space`, `Enter` (opens Inspector Drawer when implemented in Task 9), `Ctrl+Enter` (launches focused account), `Ctrl+A`.

- [ ] **Step 1: Write the failing test**

Create `tests/Shiro.Tests/Ui/MarqueeAndKeyboardNavTests.cs`:
- `ApplySelectionBatch_Updates200Accounts_WithSingleSelectedCountNotification`: loads 200 accounts, attaches `PropertyChanged` counter for `nameof(AccountCollectionViewModel.SelectedCount)`, calls `ApplySelectionBatch(accounts.Take(100).ToList(), additive: false)`, asserts `SelectedCount == 100` and notification count `== 1`.
- `MarqueeSelectionOverlay_IgnoresSub6PixelClick_AndInteractiveControls`: simulates `< 5px` move (asserts `IsSelecting == false`) and `24px` drag from empty space (asserts `IsSelecting == true` and normalized `SelectionRect` computed accurately regardless of drag quadrant).
- `DashboardView_KeyboardArrowAndSpace_NavigatesAndTogglesSelection`: checks grid index stepping with `ColumnCount = 3`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\tests\Shiro.Tests\Shiro.Tests.csproj" --no-restore --filter "FullyQualifiedName~MarqueeAndKeyboardNavTests"`
Expected: FAIL (`ApplySelectionBatch`, `MarqueeSelectionOverlay` missing).

- [ ] **Step 3: Implement `ApplySelectionBatch`, `MarqueeSelectionOverlay`, and wire `DashboardView.axaml.cs`**

- Ensure `MarqueeSelectionOverlay` uses `IsHitTestVisible="False"` for the rendering canvas while `DashboardView` handles `PointerPressed`/`PointerMoved`/`PointerReleased` on the `Grid.Row="6"` list container (only capturing pointer AFTER exceeding the `6px` threshold so normal clicks, `Shift+Click`, `DoubleTapped`, `Right-Click ContextMenu`, and `ScrollBar` thumb drags pass through 100% untouched!).
- Ensure `DashboardView.axaml.cs` stays `<= 420` lines.

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet build "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore -warnaserror; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore --no-build --filter "FullyQualifiedName~MarqueeAndKeyboardNavTests|FullyQualifiedName~UiUxUpgradeTests|FullyQualifiedName~SettingsAndWorkflowTask3Tests|FullyQualifiedName~ListScalingPerformanceTests|FullyQualifiedName~ArchitectureTests"`
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/AvaloniaUI/Controls/MarqueeSelectionOverlay.cs src/Shiro/ViewModels/Dashboard/AccountCollectionViewModel.SyncAndSelection.cs src/Shiro/AvaloniaUI/Views/DashboardView.axaml src/Shiro/AvaloniaUI/Views/DashboardView.axaml.cs tests/Shiro.Tests/Ui/MarqueeAndKeyboardNavTests.cs; git commit -m "feat(ui): add marquee rubber-band selection and keyboard grid navigation on Dashboard"
```

---

### Task 8: Account Cards & Compact Table 2.0 (Density Modes, Live RAM Telemetry, Glass Bulk Bar, Empty States)

**Files:**
- Modify: `src/Shiro/ViewModels/AccountViewModel.cs:310-325` (`HasLiveMemory`)
- Modify: `src/Shiro/ViewModels/AccountViewModel.Styling.cs:1-178`
- Modify: `src/Shiro/ViewModels/Dashboard/AccountCollectionViewModel.cs` (`CardRowHeight`, `TableRowHeight`, `CardHeight`, `IsEmptyFiltered`, `IsEmptyNoAccounts`, `DensityMode`, `SetDensityModeCommand`)
- Modify: `src/Shiro/AvaloniaUI/Views/AccountCardView.axaml`, `src/Shiro/AvaloniaUI/Views/DashboardView.axaml`, `src/Shiro/AvaloniaUI/Views/Panels/DashboardBulkBarPanel.axaml`, `src/Shiro/AvaloniaUI/Views/Panels/DashboardFooterBar.axaml`
- Modify: `src/Shiro/Resources/Strings/en.json`, `src/Shiro/Resources/Strings/vi.json`
- Test: `tests/Shiro.Tests/Ui/AccountCardDensityAndTelemetryTests.cs`

**Interfaces:**
- Consumes: `VisualPreferencesService.Instance.DensityMode`, `SkiaGlassSurface`, `SpringCompositionHelper`
- Produces:
  - `AccountViewModel.HasLiveMemory` (`bool`: `(IsInGame || IsLocalClientRunning) && !string.IsNullOrEmpty(LiveMemoryText)`)
  - `AccountCollectionViewModel.CardRowHeight` (`132` Compact / `148` Balanced / `166` Comfortable), `CardHeight` (`120` / `136` / `154`), `TableRowHeight` (`34` / `40` / `46`), `IsEmptyNoAccounts`, `IsEmptyFiltered`
  - Density toggle pill in `DashboardFooterBar.axaml`, `SkiaGlassSurface` backing on `DashboardBulkBarPanel.axaml`, and live RAM telemetry pill on `AccountCardView.axaml` + compact table rows (keeping `Transitions="{x:Null}"` on `FastIconBtn`/`FastPillBtn`/`CheckBox` and keeping `CardRoot`, `BtnDelete`, `ChkSelect` intact!).

- [ ] **Step 1: Write the failing test**

Create `tests/Shiro.Tests/Ui/AccountCardDensityAndTelemetryTests.cs`:
- `AccountCollectionViewModel_DensityMode_UpdatesCardAndTableHeights_Cleanly`: switches `Compact -> Balanced -> Comfortable` and asserts `CardRowHeight` (`132/148/166`), `CardHeight` (`120/136/154`), `TableRowHeight` (`34/40/46`).
- `AccountViewModel_HasLiveMemory_VisibleOnlyWhenClientRunningAndMemoryPresent`: sets `LiveMemoryText = "412 MB"` with `IsInGame = false` -> `false`, then `IsInGame = true` -> `true`.
- `AccountCollectionViewModel_EmptyStates_DistinguishNoAccountsFromFilteredOut`: checks `IsEmptyNoAccounts` vs `IsEmptyFiltered`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\tests\Shiro.Tests\Shiro.Tests.csproj" --no-restore --filter "FullyQualifiedName~AccountCardDensityAndTelemetryTests"`
Expected: FAIL (`HasLiveMemory`, `CardRowHeight`, `IsEmptyNoAccounts` missing).

- [ ] **Step 3: Implement density dimensions, RAM pill, glass bulk bar, and empty states**

- Keep `AccountCollectionViewModel.cs` `<= 485` lines and `DashboardView.axaml` `<= 395` lines.
- Add `Dash_DensityCompact/Balanced/Comfortable`, `Dash_EmptyTitle`, `Dash_EmptyHint`, `Dash_FilterEmptyTitle`, `Dash_ClearFiltersBtn` to `en.json` and `vi.json`.

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet build "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore -warnaserror; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore --no-build --filter "FullyQualifiedName~AccountCardDensityAndTelemetryTests|FullyQualifiedName~DialogSmokeTests|FullyQualifiedName~ListScalingPerformanceTests|FullyQualifiedName~ArchitectureTests"`
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/ViewModels/AccountViewModel.cs src/Shiro/ViewModels/AccountViewModel.Styling.cs src/Shiro/ViewModels/Dashboard/AccountCollectionViewModel.cs src/Shiro/AvaloniaUI/Views/AccountCardView.axaml src/Shiro/AvaloniaUI/Views/DashboardView.axaml src/Shiro/AvaloniaUI/Views/Panels/DashboardBulkBarPanel.axaml src/Shiro/AvaloniaUI/Views/Panels/DashboardFooterBar.axaml src/Shiro/Resources/Strings/en.json src/Shiro/Resources/Strings/vi.json tests/Shiro.Tests/Ui/AccountCardDensityAndTelemetryTests.cs; git commit -m "feat(ui): add density modes, live RAM telemetry pills, glass bulk bar, and smart empty states"
```

---

## Phase 4 — Slide-Over Account Inspector Drawer & Actionable Notification Center

### Task 9: Non-Modal Slide-Over Account Inspector Drawer (Linear Style)

**Files:**
- Create: `src/Shiro/ViewModels/Dashboard/AccountInspectorViewModel.cs` (`<= 280` lines)
- Create: `src/Shiro/AvaloniaUI/Views/Panels/DashboardAccountDrawer.axaml` & `.cs` (`<= 280` lines)
- Modify: `src/Shiro/ViewModels/MainViewModel.cs:94-120` (expose `AccountInspectorViewModel Inspector { get; }`)
- Modify: `src/Shiro/AvaloniaUI/Views/AccountCardView.axaml` & `.cs` (wire Inspect action)
- Modify: `src/Shiro/AvaloniaUI/Views/DashboardView.axaml` & `.cs` (host `DashboardAccountDrawer` in `Grid.Row="6"` right edge, `Width="340"`, non-modal)
- Modify: `src/Shiro/AvaloniaUI/Views/MainWindow.Chrome2026.cs` (strict `Esc` priority layer)
- Modify: `src/Shiro/Resources/Strings/en.json`, `src/Shiro/Resources/Strings/vi.json`
- Test: `tests/Shiro.Tests/Ui/AccountInspectorDrawerTests.cs`

**Interfaces:**
- Consumes: `SkiaGlassSurface`, `SpringCompositionHelper`, `AccountViewModel`, `IAccountRepository`, `LaunchBarViewModel`
- Produces:
  - `AccountInspectorViewModel` (`IsOpen`, `InspectedAccount`, `EditAlias`, `EditGroup`, `EditDescription`, `EditSavedPlaceId`, `Open(AccountViewModel)`, `Close()`, `SaveInlineChangesCommand`, `ValidateSessionCommand`, `ReLoginCommand`, `RefreshRobuxCommand`, `CopyUserIdCommand`, `DeleteWithUndoCommand`)
  - Strict `Esc` routing priority in `MainWindow.Chrome2026.cs`: `CommandPalette -> NotificationCenter -> Inspector -> Selection`.

- [ ] **Step 1: Write the failing test**

Create `tests/Shiro.Tests/Ui/AccountInspectorDrawerTests.cs`:
- `AccountInspector_OpenEditAndSave_PersistsAliasGroupDescription_ToRepository`: opens inspector on mock `AccountViewModel`, edits `EditAlias`, `EditGroup`, `EditDescription`, executes `SaveInlineChangesCommand`, asserts `Account.Alias/Group/Description` updated and repository save called.
- `MainWindow_EscapeKey_RespectsStrictTopmostOverlayPriority`: sets `vm.CommandPalette.IsCommandPaletteOpen = true`, `vm.IsNotificationCenterOpen = true`, `vm.Inspector.IsOpen = true`, `AccountsVm` selection `= 1`; sends 4 `Esc` presses and asserts each closes strictly in order `CommandPalette -> NotificationCenter -> Inspector -> Selection`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\tests\Shiro.Tests\Shiro.Tests.csproj" --no-restore --filter "FullyQualifiedName~AccountInspectorDrawerTests"`
Expected: FAIL (`AccountInspectorViewModel` missing).

- [ ] **Step 3: Implement `AccountInspectorViewModel`, `DashboardAccountDrawer`, and `Esc` layer ordering**

- Build `DashboardAccountDrawer.axaml` inside `SkiaGlassSurface` (`CornerRadius="{StaticResource RadiusOuter16}"`, inner cards `RadiusMid12`/`RadiusInner8`), with Identity Header (avatar + `StatusPulseDot` + copy ID), Inline Edit fields, Session Health badge + action pills, and Quick Actions footer.
- Add `Inspector_*` localized keys symmetrically to `en.json` and `vi.json`.

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet build "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore -warnaserror; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore --no-build --filter "FullyQualifiedName~AccountInspectorDrawerTests|FullyQualifiedName~DialogSmokeTests|FullyQualifiedName~P0UiAndViewModelRegressionTests|FullyQualifiedName~ArchitectureTests"`
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/ViewModels/Dashboard/AccountInspectorViewModel.cs src/Shiro/AvaloniaUI/Views/Panels/DashboardAccountDrawer.axaml src/Shiro/AvaloniaUI/Views/Panels/DashboardAccountDrawer.axaml.cs src/Shiro/ViewModels/MainViewModel.cs src/Shiro/AvaloniaUI/Views/AccountCardView.axaml src/Shiro/AvaloniaUI/Views/AccountCardView.axaml.cs src/Shiro/AvaloniaUI/Views/DashboardView.axaml src/Shiro/AvaloniaUI/Views/DashboardView.axaml.cs src/Shiro/AvaloniaUI/Views/MainWindow.Chrome2026.cs src/Shiro/Resources/Strings/en.json src/Shiro/Resources/Strings/vi.json tests/Shiro.Tests/Ui/AccountInspectorDrawerTests.cs; git commit -m "feat(ui): add non-modal Slide-Over Account Inspector drawer with inline editing"
```

---

### Task 10: Actionable Notification Center (History Popover + Undo Toasts + Drawer Offset)

**Files:**
- Create: `src/Shiro/Services/Ui/NotificationHistoryService.cs` (`<= 200` lines)
- Create: `src/Shiro/AvaloniaUI/Views/Panels/NotificationCenterPanel.axaml` & `.cs` (`<= 220` lines)
- Modify: `src/Shiro/Services/Ui/ToastService.cs` (record emitted toasts into `NotificationHistoryService` + add `ShowWithUndo`)
- Modify: `src/Shiro/AvaloniaUI/Views/Panels/MainWindowOverlaysPanel.axaml` & `.cs` (bind `ToastCard` inside `SkiaGlassSurface`, host `NotificationCenterPanel`, shift `ToastItemsControl` left cleanly when `Inspector.IsOpen == true`)
- Modify: `src/Shiro/ViewModels/Dashboard/AccountCollectionCommandActions.cs` (wire `ShowWithUndo` on reversible unpin / batch group / inspect quick-delete actions where safe)
- Modify: `src/Shiro/Resources/Strings/en.json`, `src/Shiro/Resources/Strings/vi.json`
- Test: `tests/Shiro.Tests/Ui/NotificationCenterAndUndoTests.cs`

**Interfaces:**
- Consumes: `SkiaGlassSurface`, `ToastItem`, `ToastCountdownBar`, `AccountInspectorViewModel.IsOpen`, `MainViewModel.IsNotificationCenterOpen`
- Produces:
  - `NotificationHistoryService` (`HistoryItems` max `30` ring buffer, `UnreadCount`, `HasUnread`, `FilterWarningsOnly`, `Record(ToastItem)`, `MarkAllRead()`, `ClearHistory()`, wired into `MainViewModel.UnreadNotificationCount` & `NotificationCenterPanel`)
  - `ToastService.ShowWithUndo(string title, string message, string undoText, Action onUndo, double durationSeconds = 5.0)`
  - `MainWindowOverlaysPanel` shifts toast stack horizontally (`Margin` / `TranslateTransform.X = -352` when `Inspector.IsOpen` is true) so toasts NEVER cover the Inspector Drawer OR centered `FastLoginDialog`!

- [ ] **Step 1: Write the failing test**

Create `tests/Shiro.Tests/Ui/NotificationCenterAndUndoTests.cs`:
- `NotificationHistoryService_CapsAt30Items_TracksUnread_AndMarksAllRead`: emits 35 toasts, asserts `HistoryItems.Count == 30` (oldest evicted), `UnreadCount == 30`, then `MarkAllRead()` sets `UnreadCount == 0`.
- `ToastService_ShowWithUndo_ExecutesUndoCallbackAndDismissesToast`: invokes `ShowWithUndo`, triggers `ActionCommand.Execute(null)`, asserts undo flag is `true` and `ActiveToasts` is empty.
- `MainWindowOverlaysPanel_ShiftsToastLeft_WhenInspectorDrawerOpen`: checks offset property updates synchronously when `vm.Inspector.IsOpen` toggles `true/false`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\tests\Shiro.Tests\Shiro.Tests.csproj" --no-restore --filter "FullyQualifiedName~NotificationCenterAndUndoTests"`
Expected: FAIL (`NotificationHistoryService`, `ShowWithUndo` missing).

- [ ] **Step 3: Implement `NotificationHistoryService`, `NotificationCenterPanel`, `ShowWithUndo`, and `MainWindowOverlaysPanel` glass/offset wiring**

- Keep `NotificationHistoryService` thread-safe and `AppDispatcher`-aware matching `ToastService.cs` patterns; reset cleanly in test helpers if needed so existing `ToastService` tests stay isolated.
- Add `Notify_CenterTitle`, `Notify_MarkAllRead`, `Notify_ClearAll`, `Notify_FilterAlerts`, `Notify_Empty`, `Toast_Undo` to `en.json` and `vi.json`.

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet build "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore -warnaserror; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore --no-build --filter "FullyQualifiedName~NotificationCenterAndUndoTests|FullyQualifiedName~UiUxUpgradeTests|FullyQualifiedName~P2AuditRemediationTests|FullyQualifiedName~Phase3UiAndViewModelBugfixTests|FullyQualifiedName~ArchitectureTests"`
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/Services/Ui/NotificationHistoryService.cs src/Shiro/AvaloniaUI/Views/Panels/NotificationCenterPanel.axaml src/Shiro/AvaloniaUI/Views/Panels/NotificationCenterPanel.axaml.cs src/Shiro/Services/Ui/ToastService.cs src/Shiro/AvaloniaUI/Views/Panels/MainWindowOverlaysPanel.axaml src/Shiro/AvaloniaUI/Views/Panels/MainWindowOverlaysPanel.axaml.cs src/Shiro/ViewModels/Dashboard/AccountCollectionCommandActions.cs src/Shiro/Resources/Strings/en.json src/Shiro/Resources/Strings/vi.json tests/Shiro.Tests/Ui/NotificationCenterAndUndoTests.cs; git commit -m "feat(ui): add actionable Notification Center history popover, Undo toasts, and drawer clearance"
```

---

## Phase 5 — Server List 2.0, Settings Appearance Studio & Concentric Dialogs

### Task 11: Server List, Game Explorer & Library 2.0 (Cover Cards, Signal Bars & Tiling Preview)

**Files:**
- Create: `src/Shiro/AvaloniaUI/Views/Panels/ServerListWatcherPanel.axaml` & `.cs` (extracted from `ServerListView.axaml` to give generous headroom under 400 lines!)
- Modify: `src/Shiro/AvaloniaUI/Views/ServerListView.axaml` (preserving `TxtPlaceId`)
- Modify: `src/Shiro/AvaloniaUI/Views/Panels/ServerListResultsPanel.axaml`
- Modify: `src/Shiro/ViewModels/ServerItemViewModel.cs` (`CapacityFillBrush`, `PingBarsLevel` 1..3)
- Modify: `src/Shiro/Resources/Strings/en.json`, `src/Shiro/Resources/Strings/vi.json`
- Test: `tests/Shiro.Tests/Ui/ServerListVisualUpgradeTests.cs`

**Interfaces:**
- Consumes: `SpotlightBorder`, `ShimmerSkeleton`, `StatusPulseDot`, `ServerListViewModel`
- Produces:
  - `ServerItemViewModel.CapacityFillBrush` (green `< 75%`, amber `75..94%`, crimson `>= 95%`) & `PingBarsLevel` (`3` when `<= 70ms`, `2` when `<= 140ms`, `1` otherwise)
  - Enhanced Playnite-style `SpotlightBorder` Game Explorer & Library cover cards (`60×60` concentric thumbnail, subtle radial backing, crisp `Trash` button)
  - Visual mini-grid Window Tiling preview strip inside Watcher/Arrange panel.

- [ ] **Step 1: Write the failing test**

Create `tests/Shiro.Tests/Ui/ServerListVisualUpgradeTests.cs`:
- `ServerItemViewModel_ComputesSemanticCapacityBrush_AndPingSignalBars`: verifies thresholds for 3/12, 10/12, 12/12 players and 40ms/110ms/210ms pings.
- `ServerListView_AllFourSubTabs_RenderWithZeroBindingErrors_AndNullIcons`: opens `ServerListView` in headless `Window`, iterates tabs `0..3` with `BindingErrorSink`, asserts zero binding errors and `TxtPlaceId` preserved.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\tests\Shiro.Tests\Shiro.Tests.csproj" --no-restore --filter "FullyQualifiedName~ServerListVisualUpgradeTests"`
Expected: FAIL (`CapacityFillBrush` / `PingBarsLevel` missing).

- [ ] **Step 3: Implement `ServerItemViewModel` visual helpers, `ServerListWatcherPanel`, and ServerList 2.0 XAML**

- Keep `Transitions="{x:Null}"` on `FastIconBtn`, `FastPillBtn`, `ProgressBar` inside `ServerListResultsPanel.axaml` virtualized server rows.
- Verify every `.axaml` file `<= 390` lines and `<= 200` chars/line.

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet build "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore -warnaserror; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore --no-build --filter "FullyQualifiedName~ServerListVisualUpgradeTests|FullyQualifiedName~ServerList|FullyQualifiedName~DialogSmokeTests|FullyQualifiedName~ArchitectureTests"`
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/AvaloniaUI/Views/Panels/ServerListWatcherPanel.axaml src/Shiro/AvaloniaUI/Views/Panels/ServerListWatcherPanel.axaml.cs src/Shiro/AvaloniaUI/Views/ServerListView.axaml src/Shiro/AvaloniaUI/Views/Panels/ServerListResultsPanel.axaml src/Shiro/ViewModels/ServerItemViewModel.cs src/Shiro/Resources/Strings/en.json src/Shiro/Resources/Strings/vi.json tests/Shiro.Tests/Ui/ServerListVisualUpgradeTests.cs; git commit -m "feat(ui): upgrade ServerList, Game Library cover cards, and Watcher tiling preview"
```

---

### Task 12: Settings Appearance Studio (Live Preview) & Concentric Radii Across All 8 Dialogs

**Files:**
- Create: `src/Shiro/ViewModels/Dialogs/SettingsViewModel.Appearance.cs` (`<= 160` lines — DO NOT bloat `SettingsViewModel.cs` past 485!)
- Create: `src/Shiro/AvaloniaUI/Views/Panels/SettingsAppearancePanel.axaml` & `.cs` (`<= 280` lines)
- Modify: `src/Shiro/ViewModels/Dialogs/SettingsViewModel.cs:48-71` (call `InitializeAppearance()` in ctor)
- Modify: `src/Shiro/AvaloniaUI/Views/SettingsView.axaml` (host `<panels:SettingsAppearancePanel />` inside General tab keeping `SettingsView.axaml <= 340` lines and preserving `CmbLanguage Width="205"`)
- Modify: All 8 Dialog `.axaml` files (`MessageBoxDialog`, `InputDialog`, `BulkImportDialog`, `ProxyManagerDialog`, `AccountFieldsDialog`, `AccountUtilsDialog`, `MissingAssetsDialog`, `FastLoginDialog`)
- Modify: `src/Shiro/Resources/Strings/en.json`, `src/Shiro/Resources/Strings/vi.json`
- Test: `tests/Shiro.Tests/Ui/SettingsStudioAndDialogsTests.cs`

**Interfaces:**
- Consumes: `VisualPreferencesService`, `AccentThemeService`, `SkiaGlassSurface`, `SpotlightBorder`
- Produces:
  - `SettingsViewModel.Appearance.cs`: `SelectedSurfacePreset`, `SelectedDensityMode`, `EnableGlassEffects`, `EnableAdaptiveGameTint`, `ReducedMotion`, `SelectSurfacePresetCommand`, `SelectDensityCommand` (applied live with instant Mini Preview Card inside `SettingsAppearancePanel.axaml`!).
  - Concentric geometry (`14px/16px` outer -> `10px/12px` inner card -> `6px/8px` button) and top specular gleam synchronized across all 8 dialogs while preserving every single tested `x:Name` (`TxtImportData`, `BtnStartImport`, `TxtStatus`, `BtnClear`, `PnlInApp`, `PnlCookie`, `BtnVerify`, `BtnAddCookie`, `TxtTip`, `TxtPreviewUsername`, `TxtCookieAlias`, `TxtCookieGroup`, `TxtDialogTitle`, `FieldsListBox`, `TxtHeaderTitle`, `TxtNewKey`, `TxtTitle`, `TxtInput`, `BtnOK`, `BtnCancel`).

- [ ] **Step 1: Write the failing test**

Create `tests/Shiro.Tests/Ui/SettingsStudioAndDialogsTests.cs`:
- `SettingsViewModel_AppearanceControls_UpdateVisualPreferencesAndAccentTheme`: switches `SelectedSurfacePreset` (`AmoledVoid`, `WarmGraphite`), `SelectedDensityMode` (`Compact`), toggles `EnableGlassEffects`, `EnableAdaptiveGameTint`, `ReducedMotion`, executes `SaveCommand`, and asserts persistence in `VisualPreferencesService`.
- `AllEightDialogs_ConstructAndBind_WithZeroBindingErrors_AndPreservedNames`: instantiates all 8 dialogs inside `BindingErrorSink`, verifies every required `x:Name` resolves non-null and zero binding errors occur.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\tests\Shiro.Tests\Shiro.Tests.csproj" --no-restore --filter "FullyQualifiedName~SettingsStudioAndDialogsTests"`
Expected: FAIL (`SelectedSurfacePreset` / `SelectSurfacePresetCommand` missing).

- [ ] **Step 3: Implement `SettingsViewModel.Appearance.cs`, `SettingsAppearancePanel.axaml`, and polish all 8 Dialogs**

- Keep `SettingsViewModel.cs` untouched in length (add only 1 hook line in ctor + load/save hook cleanly or subscribe via partial hooks!).
- Note for `FastLoginDialog.axaml`: remember headless `DialogSmokeTests` calls `MergedDictionaries.Clear()` between iterations, so keep `FastLoginDialog.axaml` resilient if `StaticResource` shadows/radii are cleared or keep fallback resources safe.
- Add `Set_SurfacePreset*`, `Set_Density*`, `Set_GlassEffects*`, `Set_AdaptiveGameTint*`, `Set_ReducedMotion*`, `Set_LivePreview*` symmetrically to `en.json` and `vi.json`.

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet build "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore -warnaserror; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore --no-build --filter "FullyQualifiedName~SettingsStudioAndDialogsTests|FullyQualifiedName~Settings|FullyQualifiedName~DialogSmokeTests|FullyQualifiedName~Task4UiStabilityTests|FullyQualifiedName~Phase2UiSafetyTests|FullyQualifiedName~Plan3FinalFixRegressionTests|FullyQualifiedName~ArchitectureTests"`
Expected: `0 Warning(s), 0 Error(s)` and 100% PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Shiro/ViewModels/Dialogs/SettingsViewModel.cs src/Shiro/ViewModels/Dialogs/SettingsViewModel.Appearance.cs src/Shiro/AvaloniaUI/Views/Panels/SettingsAppearancePanel.axaml src/Shiro/AvaloniaUI/Views/Panels/SettingsAppearancePanel.axaml.cs src/Shiro/AvaloniaUI/Views/SettingsView.axaml src/Shiro/AvaloniaUI/Views/*Dialog.axaml src/Shiro/Resources/Strings/en.json src/Shiro/Resources/Strings/vi.json tests/Shiro.Tests/Ui/SettingsStudioAndDialogsTests.cs; git commit -m "feat(ui): add Settings Appearance Studio with live preview and concentric dialog polish"
```

---

## Phase 6 — Performance Guardrails, Visual Regression Matrix & Release Single-File

### Task 13: Guardrail Audit, 500-Account Scaling Benchmark & Full Solution Verification

**Files:**
- Create: `tests/Shiro.Tests/Ui/Shiro2026UiGuardrailTests.cs` (`<= 250` lines)

**Steps:**
- [ ] **Step 1: Write `Shiro2026UiGuardrailTests.cs` pinning all non-functional & visual invariants**
  - `AllAxamlLucideIcons_ResolveToNonNullIconSource`: parses every `Icon="..."` across `src/Shiro/AvaloniaUI/**/*.axaml` and asserts every `LucideIconNames` enum value produces non-null `Lucide.IconSource` (guaranteeing zero `Trash2`-style blank boxes).
  - `VirtualizedTemplates_HaveZeroTransitionsOnFastControls`: inspects `AccountCardView.axaml`, `DashboardView.axaml` compact table rows, and `ServerListResultsPanel.axaml` server rows ensuring `FastIconBtn`, `FastPillBtn`, and `ProgressBar` keep `Transitions="{x:Null}"`.
  - `AccountCollection_500Accounts_BatchSelectAndFilter_CompletesUnderBudget`: loads 500 `Account` objects, applies `ApplySelectionBatch` + density toggle + search filter, asserts total elapsed time `< 250ms`.
- [ ] **Step 2: Run full solution build & tests**
  - Run: `dotnet build "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore -warnaserror` -> `0 Warning(s), 0 Error(s)`.
  - Run: `dotnet test "d:\Dự Án Visual\Shiro-Account-Manager\Shiro.sln" --no-restore --no-build` -> verify 100% of UI, Architecture, Dialog, ViewModel, Storage, Network, Launch tests pass (and verify any priority-leak Windows test in isolation if triggered by `RobloxBackgroundThrottlerTests`).
- [ ] **Step 3: Commit**

```powershell
git add tests/Shiro.Tests/Ui/Shiro2026UiGuardrailTests.cs; git commit -m "test(ui): add Shiro 2026 Lucide, virtualization, and 500-account scaling guardrails"
```

### Task 14: Automated Skia Offscreen Visual Regression Matrix & Release Single-File Publish

**Files:**
- Scratch render script strictly OUTSIDE the git repo (`<appData>/brain/<conversation-id>/scratch/Shiro2026VisualMatrix/`)

**Steps:**
- [ ] **Step 1: Render and visually inspect (`read_file`) the complete 2026 Visual Matrix at `1260×680` & `1920×1080`, `100%` & `125%` DPI, `vi` & `en`**
  1. Dashboard Card Grid with Adaptive Game Tint (*Blox Fruits* / *Anime Dice*), Marquee selection rectangle, Live RAM telemetry pill, and `SkiaGlassSurface` Floating Bulk Bar.
  2. Collapsible Icon Rail (`64px`) + `Compact` Density Mode (high-density account grid).
  3. Slide-Over Account Inspector Drawer (`340px`) open alongside Dashboard.
  4. Command Palette 2.0 (`720px`) with Live Preview Pane & Scope tabs.
  5. Notification Center popover + Undo Toast stack (confirming clean clearance from both Inspector Drawer and centered `FastLoginDialog`).
  6. Settings Appearance Studio comparing all 4 OKLCH Surface Presets (`LiquidObsidian`, `MidnightGlass`, `AmoledVoid`, `WarmGraphite`).
  7. Server List, Game Library Cover Cards, and About View.
- [ ] **Step 2: Fix any contrast, clipping, spacing, or HiDPI defect discovered during visual inspection** before final publish.
- [ ] **Step 3: Publish Release Single-File EXE & Verify Clean Git Tree**
  - Confirm `Shiro` process is not running, then run:
    `powershell -NoProfile -ExecutionPolicy Bypass -File "d:\Dự Án Visual\Shiro-Account-Manager\scripts\Publish.ps1"`
  - Verify `build\Release\Shiro.exe` exists (`Distributable EXE count: 1`) and `git status --short` is completely clean.
