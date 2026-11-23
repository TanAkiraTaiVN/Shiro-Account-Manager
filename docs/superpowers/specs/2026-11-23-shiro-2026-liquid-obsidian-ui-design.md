# Shiro 2026 — "Liquid Obsidian" UI/UX Design Specification

**Date:** 2026-11-23
**Baseline Commit:** `01e2443` (`main`)
**Target Stack:** `.NET 10 (win-x64)`, `Avalonia 12.1.6`, `Avalonia.Skia 12.1.6`, `SkiaSharp 3.119.4`, `Froststrap.LucideAvalonia 2.0.1`, `Avalonia.Fonts.Inter`

---

## 1. Intent & Research Synthesis (2025–2026 Benchmarks)

Shiro Account Manager currently runs on an **Obsidian Studio** baseline (4 surface tiers, `SpotlightBorder`, `AmbientBackdropLayer`, floating bulk action bar, glass-aura toast cards). To deliver a cohesive **flagship 2026** desktop experience without adding heavy third-party frameworks or sacrificing virtualized speed with 500+ accounts, this design synthesizes four proven industry benchmarks:

1. **Apple Liquid Glass (macOS 26) & Windows 11 Fluent 2:**
   - Explicit separation between floating optical control chrome and unobstructed scrollable content.
   - **Concentric Corner Radii** (`R_inner = max(4, R_outer - padding)`) across every container nesting level.
   - **Localized GPU Frosted Glass** (`SkiaGlassSurface` with downscaled `SKImageFilter.CreateBlur`, cached Perlin micro-noise grain to eliminate gradient banding, and subtle specular top/edge bevels).
   - **OKLCH Perceptual Color Science + APCA Contrast Gate:** Predictable luminance progression across 4 surface presets and guaranteed legibility (`|Lc| >= 60` UI/buttons, `|Lc| >= 75` primary text) even when dynamically tinting from Roblox game thumbnails.
   - **Accessibility & Battery Fallbacks:** Clean matte/instant fallbacks when `EnableGlassEffects == false` or `ReducedMotion == true`.
2. **Pro Desktop Ergonomics (Linear × Raycast × Arc × Zed):**
   - **Collapsible Sidebar Icon Rail (`215px <-> 64px`, `Ctrl+B`)** reclaiming horizontal viewport for 4-column card grids on laptops.
   - **Command Palette 2.0 (`Ctrl+K`):** 2-column `720px` modal with contextual scope pills, recent commands history, and a live right-hand **Preview Pane**.
   - **Non-Modal Slide-Over Account Inspector Drawer (`340px`):** Inspect live telemetry, session health, and edit alias/group/notes/SavedPlaceId inline without blocking the account list or spawning 3–4 separate dialogs.
   - **Marquee Rubber-Band Selection & Keyboard-First Grid Navigation:** Click-drag box selection (`> 6px` threshold, `Ctrl+Drag` additive) + `↑↓←→` / `Space` / `Enter` / `Ctrl+Enter`.
   - **Actionable Notification Center + Undo Toasts:** Titlebar bell popover with 30-item ring buffer history alongside non-destructive `Undo` action toasts.
3. **Community Game Launcher Craft (Modrinth × Playnite × Bloxstrap/Fishstrap):**
   - **Adaptive Game Hero Launch Dock:** Extracts dominant safe-chroma OKLCH accent from `CurrentGameThumbnail` so the launch dock and ambient backdrop subtly adopt the active game's atmosphere, paired with 1-click Window Tiling presets (`Auto`, `2×2`, `3×2`, `4×3`, `Cascade`).
   - **Live Per-Client RAM Telemetry:** Surfaces existing `LiveMemoryText` directly on active account cards/rows and inside the Inspector Drawer.
   - **UI Density Presets (`Compact`, `Balanced`, `Comfortable`):** Lets multi-account farmers pack maximum rows on screen or switch to a spacious showcase grid.
4. **Avalonia 12 + SkiaSharp 3.119.4 Native Performance:**
   - Render-thread animations via `Avalonia.Rendering.Composition` (`CompositionCustomVisualHandler`, `ImplicitAnimations`) and `SKRuntimeEffect` (SkSL mesh shader with strict `450ms` settle-to-idle -> **0% idle GPU/CPU**).
   - Zero new NuGet packages (`RestorePackagesWithLockFile=true`).

---

## 2. Visual System Tokens & Rules

### 2.1 Concentric Geometry Scale (`StaticResource`)
| Tier | Token | Radius | Typical Usage |
|---|---|---|---|
| Outer Overlay | `RadiusOuter16` | `16` | Modals, Command Palette 2.0, Inspector Drawer, Notification Center |
| Primary Container | `RadiusMid12` | `12` | Account Cards, KPI Cards, Launch Dock, Game Cover Cards, Dialog Inner Panels |
| Nested Surface | `RadiusInner8` | `8` | Inputs, Thumbnails inside `padding=4`, Action Footers |
| Micro Pill / Chip | `RadiusChip6` | `6` | Status Pills, Group Chips, Density Badges, Keyboard Keycaps |

### 2.2 Four OKLCH Surface Presets (`SurfacePreset`)
Generated algorithmically in `OklchColorMath` across 5 tonal steps (`Canvas` -> `Chrome` -> `Panel` -> `Card` -> `Elevated`):

| Preset | Base Hue `H` | Chroma `C` | Lightness Ladder `L` (Canvas -> Elevated) | Character |
|---|---|---|---|---|
| `LiquidObsidian` *(Default)* | `270°` | `0.014` | `0.11 -> 0.13 -> 0.16 -> 0.19 -> 0.22` | Crisp obsidian slate with cool glass refraction |
| `MidnightGlass` | `255°` | `0.024` | `0.11 -> 0.14 -> 0.17 -> 0.20 -> 0.23` | Deeper midnight indigo for vibrant accent aura |
| `AmoledVoid` | `270°` | `0.005` | `0.04 -> 0.07 -> 0.11 -> 0.15 -> 0.19` | Ultra-deep OLED black with crisp hairline borders |
| `WarmGraphite` | `65°` | `0.008` | `0.12 -> 0.14 -> 0.17 -> 0.20 -> 0.23` | Warm neutral graphite inspired by Zed / Raycast |

### 2.3 Density Presets (`UiDensityMode`)
| Mode | Card Height | Compact Row Height | Row Margin / Spacing |
|---|---|---|---|
| `Compact` | `122` | `34` | Tight (`8` bottom margin, `3px` spacing) |
| `Balanced` *(Default)* | `138` | `40` | Standard (`12` bottom margin, `5px` spacing) |
| `Comfortable` | `156` | `46` | Spacious (`14` bottom margin, `7px` spacing) |

---

## 3. Spatial & Interaction Architecture

### 3.1 Layer Z-Order & `Esc` Priority Stack
From back to front inside `MainWindow`:
1. `SkslAmbientMesh` / `AmbientBackdropLayer` (`IsHitTestVisible="False"`).
2. Shell Grid (Titlebar `44px` + Collapsible Sidebar `215px/64px` + Active Page Host + Status Footer `30px`).
3. Dashboard Inline Controls (`DashboardAccountDrawer` `Width="340"`, non-modal right slide-over inside `DashboardView`).
4. Floating Action Dock (`DashboardBulkBarPanel` bottom-center `SkiaGlassSurface`).
5. Transient Overlays (`NotificationCenterPanel` top-right popover, `ToastItemsControl` bottom-right stacked above footer and offset left by `356px` when Inspector Drawer is open).
6. Modal Overlay (`CommandPaletteOverlayPanel` `Width="720"`).

**Strict `Esc` Close Order (closest/topmost layer first):**
1. `CommandPalette.IsCommandPaletteOpen`
2. `NotificationHistoryService.IsCenterOpen`
3. `AccountInspectorViewModel.IsOpen`
4. `AccountsVm.HasSelection` (`ClearSelectionCommand`, only when on Accounts page and search box is not clearing)

---

## 4. Global Constraints

- **No New Packages:** Zero edits to `Directory.Packages.props` or `packages.lock.json`.
- **Line Budgets (`ArchitectureTests.Budgets`):**
  - Every `.cs` in `src/Shiro/` `<= 485` lines; partial classes total `<= 1150` lines.
  - Every `.axaml` in `src/Shiro/` `<= 400` lines; every line `<= 200` chars.
- **DynamicResource Rule (`ArchitectureTests`):** Every non-localization `{DynamicResource Key}` MUST end with `Brush` or `Color`, or contain `Theme` or `Font`. Static values (`CornerRadius`, `BoxShadows`, `StreamGeometry`) MUST use `{StaticResource ...}`.
- **Hygiene & Safety (`ArchitectureTests.Hygiene/Ratchets`):** Zero Vietnamese diacritics in C# comments, zero hardcoded Vietnamese string literals in `.cs` (add new strings symmetrically to both `en.json` and `vi.json`), zero `async void`, zero `+= async` / `Post(async ...)`, zero empty/untyped catch blocks, `TreatWarningsAsErrors=true`, `AvaloniaUseCompiledBindingsByDefault=true`, zero Avalonia binding errors (`BindingErrorSink`).
- **Locked Invariants:**
  - `Program.cs` stays on `Win32CompositionMode.RedirectionSurface` (no `WinUIComposition`).
  - `AccountCardView`, compact table rows, and server rows keep `Transitions="{x:Null}"` on `FastIconBtn`, `FastPillBtn`, `CheckBox`, `ProgressBar` and frozen `ImmutableSolidColorBrush`es in `AccountViewModel.Styling.cs`.
  - All existing `x:Name` controls (`CardRoot` as `Border`, `BtnDelete`, `ChkSelect`, `TxtSearch`, `TxtPlaceId`, `CmbGroups`, `CardsItemsControl`, `TableItemsControl`, `TxtJobId`, `ViewDashboard..About`, `BtnNav*`, `IconMaximize`/`IconRestore`, `TxtCommandPalette`, `ViewOverlays`, dialog controls) and 5 required panels in `Views/Panels/` stay intact.
  - `AboutView.axaml` retains `v5.0.0`.
  - `CommandPaletteCatalog.GetBuiltinCommands()` retains `29` items.
  - All Lucide icon names resolve to non-null `IconSource` (never `Trash2`; use `Trash`).

---

## 5. Review Focus (High-Risk Edge Cases)

1. **Headless & Software Renderer Fallback:** `SkiaGlassSurface` and `SkslAmbientMesh` must fall back cleanly to immutable gradient brushes without throwing when `ISkiaSharpApiLeaseFeature` or `SKRuntimeEffect` is unavailable.
2. **HiDPI Shader & Marquee Coordinate Mapping:** SkSL `fragCoord` and marquee hit-testing bounds must match `RenderScaling` (`100%`, `125%`, `150%`) without offset drift.
3. **Esc Layer Ordering & Toast/Dialog/Drawer Collision:** `Esc` must dismiss only the single topmost overlay; toasts must never overlap `FastLoginDialog` (`Width=500` centered) or `DashboardAccountDrawer` (`Width=340` right).
4. **Marquee Selection vs. Virtualized Interactions:** Left-drag marquee must ignore clicks (`<= 6px`), `Shift+Click`, double-tap launch, right-click context menus, checkboxes, buttons, and `ScrollBar` thumbs; batch selection updates must emit a single count notification.
5. **Native Skia & Composition Resource Disposal:** All `SKShader`, `SKImageFilter`, `SKRuntimeEffect`, `SKPaint`, and `CompositionCustomVisual` instances must be cached and disposed cleanly on visual detachment.
