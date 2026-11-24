# Shiro Game Tab Scheduler — Design Specification

**Date:** 2026-11-24
**Project:** Shiro Account Manager (`.NET 10 win-x64`, `Avalonia 12.1.6`, Liquid Obsidian 2026 UI)
**Status:** Draft — Pending User Approval

---

## 1. Intent & Why

Multi-account Roblox players and AFK farmers frequently rotate accounts across time slots throughout the day (e.g., running Farm Group A from `08:00` to `12:00`, closing Group A cleanly at `12:00`, then launching Farm Group B from `12:00` to `18:00`, or opening specific accounts at exact event/reset times and closing them overnight).

Today, Shiro already excels at *manual* multi-account launching (`LaunchCoordinator`, `AccountLaunchBatchService`), per-account process termination (`RobloxProcessService.CloseAccountRobloxProcess` with automatic `AutoReconnectService` suppression), window grid tiling (`RobloxWindowArrangerService`), and remote alerts (`WebhookNotificationService`). However, every shift change still requires the user to be physically present at the PC.

This feature adds a **first-class, pro-grade Game Tab Scheduler** directly into Shiro so users can define, visualize, verify, and run unattended multi-account launch/close rotations with zero window chaos, zero Auto-Reconnect fights, and full run observability.

### Community Research & UX Benchmarks Synthesized

Research across Roblox multi-account communities (RAM / Bloxkeep / Pet Simulator / AFK farming discussions) and desktop scheduler UX best practices highlighted six recurring pain points this design directly eliminates:

1. **Abrupt force-kills & Error 273 conflicts during rotation:** Force-killing processes without cooldown or launching an account while its old client is still alive triggers Roblox Error 273 (*"Same account launched from different device"*) or data-save loss.
   - *Design response:* Strict **Close-before-Open phase ordering**, graceful per-account termination (`CloseMainWindow` → wait → fallback kill, suppressing Auto-Reconnect), automatic pre-launch close of any existing live client for a target account on scheduled/manual starts, and a configurable **Handoff Cooldown** (`0–15s`, default `2s`).
2. **Window overlap chaos after shift switches:** Newly launched shift tabs pile up haphazardly, breaking visual monitoring or macro coordinates.
   - *Design response:* Per-schedule optional **Post-Launch Window Arrangement** (`None`, `Auto`, `2x2`, `3x2`, `4x3`, `Cascade`) invoking `RobloxWindowArrangerService` after a short window-creation settle delay.
3. **Silent schedule conflicts eating RAM overnight:** Overlapping schedules targeting the same accounts—or an exclusive shift killing another active shift's accounts—are easy to misconfigure in plain text lists.
   - *Design response:* An interactive **24-Hour Day Timeline Strip** (perceptual-safe muted pastel blocks + live `Now` needle + clickable blocks) paired with an automatic **Conflict Detector** surfacing inline warnings in both the timeline banner, affected cards, and the editor drawer before saving.
4. **Overnight blindness ("Did my 02:00 shift actually switch?"):** Users leave PCs unattended and cannot tell whether launches succeeded, skipped banned/expired accounts, or hit rate limits.
   - *Design response:* Persistent **Execution Run History** (ring buffer, last 50 runs) + Toast / Notification Center entries + optional **Discord / Telegram Webhook alerts** via Shiro's existing `WebhookNotificationService`.
5. **Tedious multi-shift entry:** Manually retyping times, games, and options for 3–4 consecutive shifts is friction-heavy.
   - *Design response:* One-click **`+ Next Shift`** button on every schedule card (auto-chains `StartTime = previous.EndTime`, cloning recurrence & game target), quick **Duration chips** (`+2h`, `+4h`, `+6h`, `+8h`, `+12h`) in the editor, and direct **"Schedule Launch..."** shortcuts from the Dashboard account context menu and Bulk Action Bar.
6. **Surprise launches on app startup:** Opening Shiro mid-morning and having it immediately launch 10 Roblox clients unexpectedly is a major user complaint.
   - *Design response:* **`CatchUpIfActiveOnStartup` defaults to `false`**. Users who want mid-window catch-up for 24/7 farming rigs can enable it per schedule, and catch-up skips accounts that already have a live Roblox process.

---

## 2. Goals, Non-Goals, and Anti-Goals

### Goals
- Schedule opening **one or more specific accounts** or an **entire Account Group** into either a **Roblox Game/Server** (`Place ID` + optional `Job ID` / `VIP:...`) or **Roblox Home**.
- Support optional **End Time** to gracefully close only the schedule's target accounts, including **overnight windows** (`EndTime < StartTime`, e.g., `22:00 → 06:00` closing the next morning).
- Support **One-Time** (date + auto-disable after completion), **Daily**, and **Weekly** (custom days of week) recurrences.
- Guarantee clean shift handoffs (`Close` phase → cooldown → `Open` phase → optional window tiling) through Shiro's existing `LaunchCoordinator` and `RobloxProcessService`.
- Provide a flagship **Liquid Obsidian 2026** experience: dedicated sidebar page, live countdown hero, 24-hour visual timeline, conflict warnings, non-modal frosted-glass slide-over editor drawer (`SkiaGlassSurface`), run history, and full English + Vietnamese localization.
- Keep idle overhead near zero (`1s` lightweight in-memory tick; skips work immediately when disabled or empty; UI countdown timer pauses when Scheduler view is hidden).

### Non-Goals (Out of Scope for v1)
- **Windows Task Scheduler / OS Service integration** to launch `Shiro.exe` when Shiro is completely closed (blocked by Vault password unlock security and Portable path fragility).
- **Duration-only playlist/loop mode** without clock anchors (clock-anchored shifts + `+ Next Shift` chaining covers rotation cleanly without dual-mode UI clutter).
- **Local Kestrel HTTP/WebSocket API CRUD endpoints** for schedules (may be added in a future phase without changing the domain/service layer).
- **Cron expression text syntax** (visual time + day-of-week pickers are far clearer for target users).

### Anti-Goals (Must Never Happen)
- **Double-firing** a schedule start or close within the same occurrence window (even if Shiro restarts within 90 seconds).
- **Auto-Reconnect fighting the Scheduler** and reopening a tab the scheduler intentionally closed.
- **Blocking the UI thread** during launches, process termination, or disk I/O.
- **Breaking Shiro architecture invariants** (line budgets, zero new NuGet packages, `Services` free of `AccountManager`/`Views` references, compiled bindings, symmetric VI/EN localization, `TreatWarningsAsErrors=true`).
- **Leaking account usernames or Job IDs** when Streamer Mode is active.

---

## 3. Approaches Considered

| Approach | Summary | Trade-offs | Decision |
|---|---|---|---|
| **1. Dedicated Sidebar Page ("Scheduler") + Slide-Over Editor + Dashboard Shortcuts** | Add `NavigationPage.Scheduler` on the collapsible sidebar rail (with live active-count pulsing pill badge), 24-hour timeline, schedule cards, non-modal `SkiaGlassSurface` editor drawer, run history panel, and right-click / bulk-bar shortcuts from Dashboard. | Adds a new View + ViewModel set (~6–8 small, modular files), but delivers full visibility of daily rotations, live countdowns, timeline conflicts, and run logs consistent with Shiro's Liquid Obsidian 2026 shell. | **Selected** |
| **2. Modal Dialog inside Tools Menu** | Add an item inside the `Tools` flyout opening a standalone modal dialog (like Proxy Manager). | Keeps sidebar untouched, but cramped for multi-shift timelines, hides live countdown/status behind a menu, and disrupts workflow when picking accounts/games. | Rejected |
| **3. Windows Task Scheduler / Background Daemon** | Register OS scheduled tasks to auto-start `Shiro.exe` even when closed. | Conflicts with encrypted Vault master password unlock (cannot decrypt unattended), fragile in Portable mode (`portable.mode`), and adds OS-level elevation/cleanup surface. | Rejected for v1 |

---

## 4. User Experience & Liquid Obsidian Visual Design

All visuals strictly adhere to `docs/superpowers/specs/2026-11-23-shiro-2026-liquid-obsidian-ui-design.md`:
- **Concentric radii:** `RadiusOuter16` (slide-over drawer), `RadiusMid12` (timeline card, schedule cards, history card), `RadiusInner8` (inputs, chips, inner sections), `RadiusChip6` (status/behavior/day pills), `RadiusPill` (sidebar badge, master status pill).
- **Surface tokens:** Use `{DynamicResource Surface*Brush}`, `{DynamicResource Border*Brush}`, `{DynamicResource Status*Brush}`, `{DynamicResource Accent*Brush}` exclusively (never raw pure `#000000` canvas; automatically adapts to `LiquidObsidian`, `MidnightGlass`, `AmoledVoid`, `WarmGraphite`, and `AccentTheme`).
- **Glass & Motion Fallbacks:** Slide-over editor uses `SkiaGlassSurface` (automatically falls back to matte tint when `EnableGlassEffects == false`) and `SpringCompositionHelper` (instant when `ReducedMotion == true`).
- **Verified Lucide Icons Only:** `Clock`, `Play`, `Square`, `Plus`, `ArrowRight`, `Pencil`, `Copy`, `Trash`, `LayoutGrid`, `Bell`, `ShieldAlert`, `Activity`, `Folder`, `User`, `House`, `Gamepad2`, `Check`, `X`, `Search`, `Info`.

### 4.1 Sidebar Rail & Command Palette Integration
- **Sidebar (`MainWindow.axaml`):** Add `BtnNavScheduler` directly beneath `BtnNavRecentGames` with `Icon="Clock"`, localized label (`Nav_Scheduler`: `"Scheduler"` / `"Lịch hẹn"`), and a compact `ToolsOnBadge`-style green pill (`StatusPulseDot` + `EnabledScheduleCount`, visible when `Scheduler.IsEngineActive && Scheduler.EnabledCount > 0`), respecting `.collapsed` icon-rail mode (`64px`).
- **Command Palette (`Ctrl+K`):** Add 2 built-in commands to `CommandPaletteCatalog`:
  - `nav.scheduler`: Go to Scheduler (`Icon="Clock"`, category Navigation)
  - `tool.scheduler`: Toggle Game Tab Scheduler (`Icon="Play"`, category Tools)
  - Update the built-in command count assertion in `ArchitectureTests.cs` and `CommandPalette2Tests.cs` from `29` to `31`.
- **Dashboard Quick Entry Points:**
  - Account context menu (`DashboardView.axaml`): `"Schedule Launch..."` (`Icon="Clock"`) opens `NavigationPage.Scheduler` with the editor drawer pre-filled with that account + current Dashboard `PlaceId`/`JobId`.
  - Bulk Action Bar (`DashboardBulkBarPanel.axaml` flyout): `"Schedule Selected..."` (`Icon="Clock"`) opens `NavigationPage.Scheduler` with the editor drawer pre-filled with all selected accounts + current Dashboard `PlaceId`/`JobId`.

### 4.2 Scheduler Page Layout (`SchedulerView.axaml`)

```text
┌──────────────────────────────────────────────────────────────────────────────────────────┐
│  Scheduler   [● Active • 3 shifts]    Next: "Afternoon Shift" in 01:24:10                │
│                                              [Master Toggle]  [History (12)]  [+ New]    │
├──────────────────────────────────────────────────────────────────────────────────────────┤
│  24-HOUR SHIFT TIMELINE              [Today] [Mon] [Tue] [Wed] [Thu] [Fri] [Sat] [Sun]   │
│  00:00    04:00    08:00    12:00    16:00    20:00    24:00                             │
│  │        │        █████████│▒▒▒▒▒▒▒▒│        │        │   ← Clickable pastel blocks     │
│  │        │        Morning  │Afternoon        │        │     + vertical accent "Now" bar │
│  [!] 1 overlap warning: "Event Boss" overlaps "Afternoon Shift" on 2 accounts (14:00)   │
├──────────────────────────────────────────────────────────────────────────────────────────┤
│  Filter: [All (3)] [Enabled (3)] [Running Now (1)] [Paused (0)]    [Search schedules...] │
│                                                                                          │
│  ┌─ ▌ Morning Shift   [08:00 → 12:00] [Daily] [● Active Now]       [Toggle] [▶][■][+→][✎][⧉][🗑]
│  │    Group: Farm-A (5 ready) • Place 2753915549 (Public) • Tile: Auto • Handoff: 2s     │
│  ├─ ▌ Afternoon Shift [12:00 → 18:00] [Daily] [Next in 01:24:10]   [Toggle] [▶]   [+→][✎][⧉][🗑]
│  │    Group: Farm-B (5 ready) • Place 2753915549 (Public) • Close Others • Webhook       │
│  └─ ▌ Night Farm      [22:00 → 06:00 (+1d)] [Mon–Fri] [Scheduled]  [Toggle] [▶]   [+→][✎][⧉][🗑]
│       3 Accounts • Home • Catch-up: On                                                   │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

1. **Header & Command Hero Bar:**
   - Page title + live status pill (`Active • N enabled` with pulsing `StatusPulseDot`, or `Paused`).
   - Live Next-Event chip (`TabularNum` font): displays upcoming action icon (`Play` for Open, `Square` for Close), schedule name, clock time, and live `HH:mm:ss` countdown (ticks every `1s` only while `SchedulerView` is visible).
   - Master `ToggleSwitch` (globally enables/pauses background execution without modifying per-schedule toggles).
   - `Run History` toggle button (expands/collapses `SchedulerHistoryPanel` right below the list or switches view cleanly) and primary `+ New Schedule` button.
2. **24-Hour Visual Timeline (`SchedulerTimelinePanel.axaml`):**
   - Day selector chips (`Today` + `Mon`..`Sun`).
   - Horizontal 24-hour axis (`00:00`, `04:00`, `08:00`, `12:00`, `16:00`, `20:00`, `24:00`) with whisper-quiet `BorderHairlineBrush` gridlines (`0.5` opacity).
   - Enabled schedules applicable to the selected day render as horizontal rounded bars (`RadiusChip6`) in stacked lanes (greedy interval-packing lanes so overlapping blocks never mask one another!).
   - Overnight schedules (`22:00 → 06:00`) automatically project as two clean segments on the relevant days (`22:00–24:00` on start day, and `00:00–06:00` carryover on next day).
   - Open-only schedules (no `EndTime`) render as a crisp fixed-width marker pill (minimum 30-min visual width) with an open-ended chevron arrow (`→`).
   - Clicking any timeline block opens it in the Slide-Over Editor Drawer.
   - Live accent vertical `Now` needle rendered when viewing `Today`.
   - **Conflict Banner:** Shown directly beneath the timeline only when `Conflicts.Count > 0` (`StatusWarningBgBrush` / `StatusWarningBorderBrush` with `ShieldAlert` icon), listing the conflicting schedule pairs and reason.
3. **Schedule Cards List:**
   - Filter chips (`All`, `Enabled`, `Active Now`, `Paused`) + quick search box.
   - Progressive disclosure per card: 4px colored left accent bar, title, `TabularNum` window chip (`08:00 → 12:00` or `22:00 → 06:00 (+1d)` or `08:00`), recurrence chip, live state pill (`Active Now` / `Next in ...` / `Paused` / `Completed` / `Conflict`), concise target + destination metadata row, and compact action bar:
     - Per-schedule `ToggleSwitch`
     - `Run Now` (`Play`) — manually executes the schedule's Open flow immediately (even if paused)
     - `Close Tabs` (`Square`) — manually executes the schedule's Close flow immediately
     - `+ Next Shift` (`ArrowRight`) — opens Editor Drawer pre-filled as a chained continuation (`StartTime = EndTime`, same recurrence & game target, empty/next name)
     - `Edit` (`Pencil`), `Duplicate` (`Copy`), `Delete` (`Trash` — deletes immediately with a 6-second **Undo Toast** via `ToastService` action button!)
   - **Streamer Mode compliance:** When `StreamerModeService.IsActive` is true, individual account usernames in card summaries, editor account checklist, tooltips, and run history are masked consistently using Shiro's existing masking convention (`StreamerModeService` / `••••`) and Job IDs are masked.
4. **Non-Modal Slide-Over Editor Drawer (`SchedulerEditorDrawer.axaml`, `Width="390"`):**
   - Overlaid on the right edge of `SchedulerView` using `SkiaGlassSurface` (`RadiusOuter16`, `ShadowFloating`), matching `DashboardAccountDrawer`.
   - Participates in `MainWindow.TryDismissTopmostLayer` `Esc` priority (at the same tier as `Inspector.IsOpen`: pressing `Esc` closes the editor drawer cleanly).
   - **Section 1 — Identity & Timing:**
     - Name textbox + 6-swatch muted perceptual-safe color tag picker (`#38BDF8` Sky, `#4ADE80` Emerald, `#A855F7` Violet, `#FBBF24` Amber, `#FB7185` Rose, `#2DD4BF` Teal).
     - Recurrence segmented buttons (`One-Time`, `Daily`, `Weekly`).
     - When `One-Time`: Date input (`yyyy-MM-dd` with quick `Today` / `Tomorrow` chips).
     - When `Weekly`: 7 compact toggle pills (`Mon`..`Sun`) + quick chips (`Weekdays`, `Weekends`, `All`).
     - `Start Time` (`HH:mm` 24-hour input with `±15m` / hour-minute pickers or validated `HH:mm` box).
     - `Enable Auto-Close (End Time)` toggle + `End Time` (`HH:mm`) + **1-click Duration chips (`+2h`, `+4h`, `+6h`, `+8h`, `+12h`)** that automatically compute `EndTime` from `StartTime` and show an `Overnight (+1 day)` pill whenever `EndTime < StartTime`.
   - **Section 2 — Accounts Target:**
     - Segmented toggle: `Account Group` vs `Specific Accounts`.
     - *Group mode:* ComboBox of existing groups (live count of ready/eligible accounts shown beneath).
     - *Specific Accounts mode:* Searchable inline multi-select list of accounts with checkboxes, session status dot, live `"N selected (M ready)"` counter, and quick buttons (`Select Ready`, `From Dashboard Selection`, `Clear`).
   - **Section 3 — Game Destination:**
     - Segmented toggle: `Join Server` vs `Open Home`.
     - When `Join Server`: `Place ID` input + quick-pick flyout listing Shiro's Recent & Favorite games + optional `Job ID / VIP:` input (`PasswordChar` honors Streamer Mode).
   - **Section 4 — Rotation & Handoff Options:**
     - `Close other running Roblox tabs before opening` (`ToggleSwitch`, default `false`).
     - `Handoff cooldown after close` (`0–15s` numeric/stepper, default `2s`).
     - `Arrange windows after launch` ComboBox (`None`, `Auto`, `2x2`, `3x2`, `4x3`, `Cascade`; default `None`).
     - `Catch up if Shiro opens mid-window` (`ToggleSwitch`, default `false`).
     - `Send Discord / Telegram webhook alert` (`ToggleSwitch`, default `false`; displays subtle hint if no webhook is configured in Settings).
   - **Footer:** Live inline validation error (if invalid time/missing target/missing Place ID) + live non-blocking conflict preview banner + `Cancel` / `Save Schedule` buttons.
5. **Collapsible Run History Panel (`SchedulerHistoryPanel.axaml`):**
   - Shows up to 50 most recent `ScheduleRunEntry` items (newest first): local timestamp (`MM-dd HH:mm:ss`), schedule name, phase pill (`Open` / `Close` / `Manual Open` / `Manual Close` / `Catch-Up`), outcome pill (`Success` / `Partial` / `Skipped` / `Failed`), and concise summary text (e.g., `"Opened 5/5 accounts"`, `"Closed 4 tabs"`, `"Skipped 1 banned account"`), plus a `Clear History` button.

---

## 5. Core Domain Model & Scheduling Engine Semantics

### 5.1 Pure Domain Types (`src/Shiro/Domain/LaunchSchedule.cs` & `ScheduleRunEntry.cs`)
Zero dependencies on Avalonia, Http, or Win32 (`P3DomainPurityTests` compliant):

- `enum ScheduleRecurrence { OneTime = 0, Daily = 1, Weekly = 2 }`
- `[Flags] enum ScheduleDaysOfWeek { None = 0, Monday = 1, Tuesday = 2, Wednesday = 4, Thursday = 8, Friday = 16, Saturday = 32, Sunday = 64, Weekdays = 31, Weekends = 96, All = 127 }`
- `enum ScheduleTargetMode { SpecificAccounts = 0, Group = 1 }`
- `enum ScheduleLaunchMode { JoinServer = 0, OpenHome = 1 }`
- `enum ScheduleArrangeMode { None = 0, Auto = 1, Grid2x2 = 2, Grid3x2 = 3, Grid4x3 = 4, Cascade = 5 }`
- `enum ScheduleRunPhase { Open = 0, Close = 1, ManualOpen = 2, ManualClose = 3, CatchUpOpen = 4 }`
- `enum ScheduleRunOutcome { Success = 0, Partial = 1, Skipped = 2, Failed = 3 }`
- `sealed class ScheduleAccountRef`: `long UserId`, `string Username` (resolves against `IAccountRepository` by `UserId > 0` first, falling back to case-insensitive `Username`).
- `sealed class LaunchSchedule`:
  - `string Id` (GUID `"N"`)
  - `string Name`
  - `bool IsEnabled` (default `true`)
  - `string ColorHex` (default `"#38BDF8"`)
  - `ScheduleRecurrence Recurrence` (default `Daily`)
  - `ScheduleDaysOfWeek DaysOfWeek` (default `All`)
  - `string? OneTimeDate` (`"yyyy-MM-dd"`, used when `Recurrence == OneTime`)
  - `string StartTime` (`"HH:mm"`, 24-hour invariant)
  - `bool HasEndTime` (default `true`)
  - `string? EndTime` (`"HH:mm"`, 24-hour invariant; `EndTime < StartTime` defines an overnight window ending next day; `EndTime == StartTime` is invalid)
  - `ScheduleTargetMode TargetMode`
  - `string TargetGroup` (for `Group` mode)
  - `List<ScheduleAccountRef> TargetAccounts` (for `SpecificAccounts` mode)
  - `ScheduleLaunchMode LaunchMode`
  - `long PlaceId`
  - `string JobId` (supports `"VIP:..."` prefix identical to Dashboard)
  - `bool CloseOtherTabsBeforeLaunch` (default `false`)
  - `int HandoffDelaySeconds` (clamped `0..15`, default `2`)
  - `ScheduleArrangeMode PostLaunchArrange` (default `None`)
  - `bool CatchUpIfActiveOnStartup` (default `false`)
  - `bool SendWebhookNotification` (default `false`)
  - `string? LastTriggeredStartKey` (`"yyyy-MM-dd"` occurrence start date)
  - `string? LastTriggeredEndKey` (`"yyyy-MM-dd"` occurrence start date)
  - `DateTimeOffset CreatedAtUtc`
  - `DateTimeOffset UpdatedAtUtc`
- `sealed class ScheduleRunEntry`:
  - `string Id`, `string ScheduleId`, `string ScheduleName`, `DateTimeOffset TimestampUtc`, `ScheduleRunPhase Phase`, `ScheduleRunOutcome Outcome`, `int TargetCount`, `int SucceededCount`, `int SkippedCount`, `string Summary`
- `sealed class LaunchScheduleDocument`:
  - `int SchemaVersion = 1`
  - `List<LaunchSchedule> Schedules`
  - `List<ScheduleRunEntry> History`

### 5.2 Deterministic Evaluator (`LaunchScheduleEvaluatorHelper`)
All time calculations are pure, stateless, and accept `DateTimeOffset localNow` (sourced from `TimeProvider.GetLocalNow()` in production and `FakeTimeProvider` in tests):

1. **Vault Lock Gate & Startup Catch-Up Rule:**
   - **Master Password Vault Lock:** While `AccountPersistenceService.IsVaultLockedPendingMasterPassword == true`, `LaunchSchedulerService` pauses all trigger evaluation and defers one-time startup catch-up so schedules are never evaluated against an empty unlocked repository or stripped of their occurrence keys during the password prompt. Once the vault unlocks (`AccountsChanged` with `!IsVaultLockedPendingMasterPassword`), the scheduler immediately runs the deferred startup catch-up once and resumes the `1s` tick loop.
   - **Startup Catch-Up:** Runs once after accounts are unlocked/loaded: for each enabled schedule with `CatchUpIfActiveOnStartup == true` where `localNow` lies strictly inside `[Start + 90s, End)` (or `[Start, End)` when `HasEndTime` is true and `LastTriggeredStartKey != occurrenceKey`):
     - Queue a `CatchUpOpen` execution that **only launches target accounts not already running a live Roblox process**, preventing disruption when restarting Shiro mid-shift.
2. **Occurrence Ownership & Overnight Rule:**
   - Every occurrence is keyed by its **start calendar date** `occurrenceDate` (`"yyyy-MM-dd"`).
   - For a schedule with `StartTime` and optional `EndTime`:
     - `isOvernight = HasEndTime && EndTime < StartTime`.
     - Window for `occurrenceDate`: `[occurrenceDate + StartTime, (isOvernight ? occurrenceDate.AddDays(1) : occurrenceDate) + EndTime]`.
   - Checking due events at `localNow`:
     - Always inspect **two candidate start dates**: `today = DateOnly.FromDateTime(localNow.DateTime)` AND `yesterday = today.AddDays(-1)` (so an overnight window starting on Monday `22:00` accurately fires its `End` event on Tuesday `06:00` even if Tuesday is unchecked in `DaysOfWeek`!).
     - DST safety: convert local `(DateOnly, TimeOnly)` via `TimeZoneInfo.Local` handling invalid/ambiguous times gracefully (advance invalid spring-forward gap by 1 hour; take first offset for ambiguous fall-back).
3. **Trigger Grace Window, Idempotency & Manual Run Interaction:**
   - Normal trigger fires when `scheduledAt <= localNow < scheduledAt + TimeSpan.FromSeconds(90)` AND `LastTriggeredStartKey != occurrenceKey` (or `LastTriggeredEndKey != occurrenceKey` for End).
   - Mark `LastTriggeredStartKey` / `LastTriggeredEndKey` **before** awaiting async execution (under the scheduler lock) and persist immediately so crashes/restarts or overlapping ticks can never double-fire.
   - For `OneTime` schedules: once the scheduled terminal event (`End` if `HasEndTime`, otherwise `Start`) triggers, automatically set `IsEnabled = false` and persist.
   - **Manual `Run Now` (`ManualOpen`) & `Close Tabs` (`ManualClose`):** Ad-hoc manual actions never disable `OneTime` schedules and leave future occurrences intact when tested outside the trigger window. However, if `ManualOpen` (or `ManualClose`) is clicked *inside* the current 90-second start (or end) grace window, it marks `LastTriggeredStartKey` (or `LastTriggeredEndKey`) for that occurrence so the `1s` tick does not immediately double-trigger and kill/relaunch the freshly launched tabs.
4. **Active Window & Next Event Projection:**
   - `IsCurrentlyActive(schedule, localNow)` checks both `today` and `yesterday` occurrences.
   - `GetNextTransition(schedule, localNow)` scans forward up to 8 days (or the specific `OneTimeDate`) to find the exact next `Open` or `Close` timestamp for UI countdowns.
5. **24-Hour Timeline Projection & Conflict Detection:**
   - `ProjectDayTimeline(schedules, date)` projects enabled schedules onto `[0..1440]` minutes for `date` (splitting overnight schedules into tail `0..EndMin` from `date.AddDays(-1)` and head `StartMin..1440` on `date`), assigning non-overlapping horizontal lanes via greedy interval packing.
   - `DetectConflicts(schedules, accountResolver)` scans a 7-day rolling window from `today` plus all enabled `OneTime` dates:
     - `AccountOverlap`: two distinct enabled schedules overlap in time (`A.Start < B.End && B.Start < A.End`; open-only schedules treated as a 15-minute launch conflict footprint `[Start, Start+15m]`) and share ≥ 1 resolved account.
     - `ExclusiveCloseCollision`: enabled schedule B has `CloseOtherTabsBeforeLaunch == true` and `B.Start` falls strictly inside enabled schedule A's active window `(A.Start, A.End)` on the same day.

### 5.3 Execution Pipeline & Rotation Safety (`LaunchSchedulerService`)
All executions are serialized behind a single `SemaphoreSlim(1, 1)` execution gate so overlapping ticks, manual runs, and shift handoffs never race or collide with `LaunchCoordinator`'s single queue gate:

1. **Tick Aggregation (Close-Before-Open Ordering):**
   - Each `1s` tick collects all due `Close` triggers and all due `Open` triggers across enabled schedules.
   - **Phase 1 — Closes First:**
     - Execute every due `Close` trigger by resolving its target accounts and invoking `ISchedulerActionExecutor.CloseAccountsAsync(accounts)` → calls `RobloxProcessService.CloseAccountRobloxProcesses(accounts)` (which calls `AutoReconnectService.Instance.SuppressReconnectForAccount` before graceful `CloseMainWindow` + 600ms wait + fallback `Kill`).
     - For each due `Open` trigger:
       - Resolve target accounts (`Account.Valid && !Account.IsBannedOrLocked && string.IsNullOrWhiteSpace(Account.AccountStatusIssue)`). Record skipped invalid/banned/missing accounts for the run summary.
       - If `CloseOtherTabsBeforeLaunch == true`, close all currently running Roblox accounts in `IAccountRepository` that are **not** in this schedule's target set.
       - **Error 273 Prevention:** For non-catch-up `Open` / `ManualOpen`, if any resolved target account already has a live Roblox process tracked by Shiro, close that account's existing process first (suppressing Auto-Reconnect) so switching games cleanly replaces the client instead of triggering Error 273. (For `CatchUpOpen`, skip already-running target accounts instead of closing them.)
   - **Phase 2 — Handoff Cooldown:**
     - If ≥ 1 Roblox process was closed in Phase 1 and at least one `Open` trigger is pending, wait `TimeSpan.FromSeconds(maxHandoffDelaySeconds)` (default `2s`, cancellable on shutdown) so Roblox clients finish exiting and releasing RAM/handles before launching the next shift.
   - **Phase 3 — Launches & Queue Contention Policy:**
     - For each due `Open` trigger (sequentially):
       - Ensure Multi-Roblox mutex is active when `eligibleTargets.Count > 1 && EnableMultiRbx` (non-prompting).
       - **LaunchCoordinator Queue Contention:** Wait up to a bounded `30-second` grace timeout (`launchCoordinator.WaitForIdleAsync(TimeSpan.FromSeconds(30))`, cancellable on shutdown) for any user-initiated manual launch batch to finish—never cancelling a user's active manual batch. If the queue is still busy after 30 seconds, skip launching that batch, record `ScheduleRunOutcome.Skipped` in `ScheduleRunEntry` with a clear queue-busy summary, emit a warning toast, and continue so subsequent schedules are not blocked.
       - Delegate to `ISchedulerActionExecutor` via a dedicated scheduler-aware `AccountLaunchBatchService` path that returns `LaunchBatchResult` directly, **suppresses `AccountLaunchBatchService`'s generic batch summary toast** (so Phase 4 emits the single scheduler Toast), **does not mutate Dashboard UI state** (`CurrentPlaceId`, `CurrentJobId`, `SelectedAccounts`), and **bypasses per-account `SavedJobId` fallback** when `schedule.JobId` is empty so Public Server schedules always join public servers:
         - `JoinServer`: routes through `LaunchCoordinator` (preserving Smart Fast Launch, Captcha deferral, HTTP 429 rate-limit coordination, and configured join delay).
         - `OpenHome`: routes through `LaunchCoordinator` with `openHome: true`.
       - Derive `ScheduleRunOutcome` from `LaunchBatchResult` (`Success` when all eligible targets succeed, `Partial` when some succeed and some fail/skip, `Failed` when 0 succeed and ≥ 1 attempted, `Skipped` when 0 eligible targets or queue busy).
       - If `PostLaunchArrange != None` and ≥ 1 account succeeded, wait `3s` (cancellable) and call `ISchedulerActionExecutor.ArrangeWindows(schedule.PostLaunchArrange)`.
   - **Phase 4 — Observability & Persistence:**
     - Append `ScheduleRunEntry` (ring buffer capped at 50 entries) and persist via `LaunchScheduleStore`.
     - Emit Toast notification via `IToastNotifier` (auto-recorded in `NotificationHistoryService`).
     - If `schedule.SendWebhookNotification == true`, dispatch `WebhookNotificationService.NotifyAsync(WebhookEventType.Info, ...)` (respecting Streamer Mode and non-blocking).
     - Raise `SchedulesChanged` / `HistoryChanged` events and trigger `IHostUiBridgeService.NotifyLiveProcessStatusChanged()`.

---

## 6. Architecture, Persistence & Codebase Integration

### 6.1 Decoupling & Strict Layer Compliance
To satisfy `ArchitectureTests.Services_ShouldNotDependOn_AccountManager`, `ViewModels_ShouldNotDependOn_Views`, `Services_ShouldFollowNamingConvention`, and `P3DomainPurityTests`:
- **Domain (`src/Shiro/Domain/`):** `LaunchSchedule.cs`, `ScheduleRunEntry.cs` (namespace `Shiro`).
- **Storage (`src/Shiro/Services/Storage/`):**
  - `StoragePaths.cs`: add `public static string LaunchSchedulesFile => GetUserFile("LaunchSchedules.json");`.
  - `AppSettingsKeys.cs`: add `public const string SchedulerEnabled = "SchedulerEnabled";` under `General`.
  - `ShiroAppJson.cs`: register `[JsonSerializable(typeof(LaunchScheduleDocument))]`, `LaunchSchedule`, `List<LaunchSchedule>`, `ScheduleAccountRef`, `ScheduleRunEntry`, `List<ScheduleRunEntry>`.
  - `LaunchScheduleStore.cs`: atomic write (`.tmp` → `File.Replace` / `File.Move(overwrite: true)`) + `.backup` recovery on corrupted JSON, thread-safe lock.
- **Launch Services (`src/Shiro/Services/Launch/`):**
  - `ISchedulerActionExecutor.cs` & `ILaunchSchedulerService.cs`
  - `LaunchScheduleEvaluatorHelper.cs` (pure time math, timeline projection, conflict detection)
  - `LaunchSchedulerService.cs` + `LaunchSchedulerService.Execution.cs` (timer loop, CRUD, serialized execution, `TimeProvider` injection, `IDisposable`)
- **Host Wiring (`AccountManager` & `AppBootstrap`):**
  - `AppBootstrap.cs` & `AppServices.cs`: register `LaunchScheduleStore` and `ILaunchSchedulerService` (`LaunchSchedulerService`) as singletons.
  - `AccountManager.Lifecycle.cs`:
    - Wire `ISchedulerActionExecutor` during host setup so `LaunchSchedulerService` invokes `LaunchAccounts`, `LaunchHomeAccountsAsync`, `RobloxProcessService.CloseAccountRobloxProcesses`, `TryEnableMultiRoblox(promptIfRobloxRunning: false)`, and `RobloxWindowArrangerService.ArrangeWindows` without `Shiro.Services` referencing `AccountManager`.
    - Start `AppServices.LaunchScheduler.Start()` in `InitializePostStartup()` (with startup catch-up).
    - Register `LaunchSchedulerService.StopForShutdown` in `ShutdownCoordinator` (`ShutdownPhase.StopMonitors`) and `LaunchScheduleStore.Flush` in `ShutdownPhase.FlushPersistence`.
- **ViewModels (`src/Shiro/ViewModels/Scheduler/`):**
  - `SchedulerViewModel.cs` (page state, filters, timeline projection, countdown refresh, commands, undo-delete toast hook)
  - `ScheduleItemViewModel.cs` (card projection, Streamer Mode aware summaries, live status/countdown formatting)
  - `ScheduleEditorViewModel.cs` (slide-over draft state, duration chips, account/group selection, recent/favorite game picker, validation, live conflict preview)
  - `SchedulerTimelineBlockViewModel.cs` (normalized left%/width% bar coordinates, lane index, tooltip, color brush)
  - `MainViewModel.cs`: add `NavigationPage.Scheduler`, `SchedulerViewModel Scheduler { get; }`, `NavigateToSchedulerCommand`, `OpenNewScheduleForAccounts(...)`, forward language/streamer/theme changes.
- **Views (`src/Shiro/AvaloniaUI/Views/` & `Panels/`):**
  - `SchedulerView.axaml` / `.cs`
  - `Panels/SchedulerTimelinePanel.axaml` / `.cs`
  - `Panels/SchedulerEditorDrawer.axaml` / `.cs`
  - `Panels/SchedulerHistoryPanel.axaml` / `.cs`
  - Update `MainWindow.axaml`, `MainWindow.Navigation.cs`, `MainWindow.Chrome2026.cs` (`BtnNavScheduler`, `ViewScheduler`, `Esc` closes `Scheduler.Editor.IsOpen`).
  - Update `DashboardView.axaml`, `DashboardView.ContextMenu.cs`, `DashboardBulkBarPanel.axaml`.
- **Localization (`src/Shiro/Resources/Strings/en.json` & `vi.json`):**
  - Symmetric `Sched_*` / `Scheduler.*` keys in both files. Zero Vietnamese diacritics in C# comments or literals.
- **File Budgets:** Every `.cs` file `<= 485` lines, every `.axaml` file `<= 400` lines (lines `<= 200` chars), every test file `<= 550` lines.

---

## 7. Designer Calls (Explicit Decisions Made for User Review)

The following defaults were chosen to produce the cleanest, safest experience and can be adjusted before implementation:

1. **Master Switch Default:** Enabled (`true`) by default, but completely idle until at least one enabled schedule exists.
2. **Vault Lock Startup Gate:** While `IsVaultLockedPendingMasterPassword == true`, trigger evaluation and startup catch-up wait until the vault is unlocked so shifts are never skipped against an empty account list.
3. **Handoff Cooldown Default:** `2 seconds` (user-configurable `0–15s` per schedule) whenever tabs are closed before launching the next shift.
4. **Post-Launch Window Arrange Settle Delay:** `3 seconds` after the launch batch finishes before invoking `RobloxWindowArrangerService`.
5. **Startup Mid-Window Catch-Up Default:** `false` (opt-in per schedule) to avoid unexpected client launches when opening Shiro; when enabled, skips accounts that already have a live Roblox process.
6. **Same-Account Pre-Launch Close:** On scheduled/manual `Open`, if a target account already has a live Roblox client, gracefully close it first (suppressing Auto-Reconnect) to prevent Roblox Error 273.
7. **Public Server Target Fidelity:** A `JoinServer` schedule with empty `JobId` strictly joins a Public Server (ignoring per-account `SavedJobId` fields) and never overwrites Dashboard `PlaceId` / `JobId` / selection state.
8. **Manual `Run Now` / `Close Tabs` vs. Occurrence Keys:** Manual runs never disable `OneTime` schedules or cancel future occurrences when tested early, and only mark the current occurrence key if invoked inside its 90s grace window (preventing an immediate duplicate relaunch).
9. **Queue Contention Policy:** If a manual launch batch is still running in `LaunchCoordinator` when a scheduled `Open` fires, wait up to `30s` (without cancelling the manual batch); if still busy, log/notify `Skipped` so later schedules are not starved.
10. **Trigger Grace Window & Run History Cap:** `90 seconds` grace window with persisted `(ScheduleId, OccurrenceDate)` deduplication, and a `50`-entry run history ring buffer persisted in `LaunchSchedules.json`.
11. **Command Palette Count Invariant:** Add 2 built-in commands (`nav.scheduler`, `tool.scheduler`) and update the invariant count in `ArchitectureTests.cs` and `CommandPalette2Tests.cs` from `29` to `31`.

---

## 8. What's Left to the Builder

- Exact XAML spacing, typography weights, and micro-padding refinements inside the 4 new `.axaml` files while honoring Liquid Obsidian tokens and `< 400` line / `< 200` char limits.
- Exact `TimePicker` vs formatted `HH:mm` input + stepper widget choice inside `SchedulerEditorDrawer` (must support fast keyboard entry and pass Avalonia headless UI tests without binding errors).
- Exact wording of localized `en.json` / `vi.json` keys (maintaining natural Vietnamese and English parity).
- Structuring unit and headless UI test classes under `tests/Shiro.Tests/Launch/` and `tests/Shiro.Tests/Ui/`.

---

## 9. Verification & Test Strategy

1. **Pure Evaluator & Conflict Tests (`tests/Shiro.Tests/Launch/LaunchScheduleEvaluatorTests.cs`):**
   - Daily, Weekly, One-Time trigger matching; overnight windows (`22:00 → 06:00` closing next morning even when next weekday is unchecked); `90s` grace window; deduplication keys; `OneTime` auto-disable; `StartupCatchUp` eligibility; 24-hour timeline projection & lane packing; `AccountOverlap` and `ExclusiveCloseCollision` detection.
2. **Scheduler Service Execution Tests (`tests/Shiro.Tests/Launch/LaunchSchedulerServiceTests.cs`):**
   - Uses `FakeTimeProvider` + mocked `ISchedulerActionExecutor` (zero real sleeps or Roblox processes).
   - Verifies strict Close-before-Open ordering, handoff cooldown timing, Error 273 pre-launch close of live target accounts vs catch-up skipping live accounts, `CloseOtherTabsBeforeLaunch`, skipping banned/invalid accounts, post-launch window arrangement, `LaunchScheduleStore` atomic persistence & `.backup` recovery, and clean `ShutdownCoordinator` disposal.
3. **Headless Avalonia UI & ViewModel Tests (`tests/Shiro.Tests/Ui/SchedulerViewModelAndUiTests.cs`):**
   - Creating/editing/duplicating/chaining (`+ Next Shift`) and deleting with Undo; duration quick chips including overnight wraparound; Streamer Mode masking; `MainWindow` navigation to `NavigationPage.Scheduler` & `Esc` dismissing `SchedulerEditorDrawer`; Dashboard context menu / bulk bar pre-fill shortcut; zero Avalonia binding errors.
4. **Full Suite & Coverage Gates:**
   - `dotnet test tests\Shiro.Tests` (all existing 2,619+ tests + new scheduler tests + `ArchitectureTests` passing with 0 warnings).
   - Verify overall line coverage `>= 75%`, branch coverage `>= 60%`, core coverage `>= 92%`.
