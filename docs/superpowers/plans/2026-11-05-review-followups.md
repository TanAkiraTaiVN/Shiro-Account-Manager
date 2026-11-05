# Review Follow-ups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Address the verified findings from the 2026-11-05 project review without changing runtime behavior.

**Architecture:** Three small, independent tasks: rename session-named test files, lift branch coverage on the launch pipeline, add a ToS disclaimer. No production code changes except where a test exposes a real bug.

**Tech Stack:** .NET 10, xUnit, coverlet (`tests/coverage.runsettings`), `tests/coverage-floors.json`.

**Spec:** Review findings in conversation 9fbff2f7 (2026-11-05). Verified-out (NOT in scope):
- `RobloxSingletonService.cs:247 stop.Wait()` — runs on a dedicated guard thread, intended blocking. OK.
- `FastLoginDialog.axaml.cs` `async void` handlers — all wrapped in try/catch. OK.
- `RelayCommand.Execute` `async void` — `ExecuteAsync` catches all exceptions. OK.
- `coverage/` and `.superpowers/sdd/*.log` — already git-ignored, untracked. Local-only cleanup.

## Global Constraints
- All 1720 existing tests must keep passing: `dotnet test Shiro.sln -c Release`.
- Build must stay at 0 warnings.
- Coverage floors in `tests/coverage-floors.json` may only go up.
- Pure renames must be done with `git mv` so history follows.

## Review Focus
1. Renamed test classes colliding with an existing class name → compile error; check before renaming.
2. Test filters in CI (`.github/workflows/build.yml`) referencing `Wave*` names → must be updated or CI silently runs fewer tests.
3. New launch tests touching real Roblox processes / network → must use existing fakes only, never spawn `RobloxPlayerBeta.exe`.
4. Coverage floor raised above what CI measures (different runner) → keep 2-point margin below locally measured value.
5. Test count must not drop after Task 1 (rename only).

---

### Task 1: Rename session-named test files by feature

**Files (git mv, and rename the class inside to match):**
| From | To |
|---|---|
| `tests/Shiro.Tests/Wave2ProcessAndSingletonOptimizationTests.cs` | `ProcessAndSingletonOptimizationTests.cs` |
| `Wave3ClientSettingsAndAntiAfkTests.cs` | `ClientSettingsAndAntiAfkTests.cs` |
| `Wave3LoggerAndRedactorTests.cs` | `LoggerAndRedactorTests.cs` |
| `Wave3NexusWebSocketTests.cs` | `NexusWebSocketEndToEndTests.cs` |
| `Wave3ServerPingAndDatacenterTests.cs` | `ServerPingAndDatacenterTests.cs` |
| `Wave4..7CoreOptimizationTests.cs` | Split/rename by the dominant class under test in each file (e.g. `AccountManagerOptimizationTests.cs`); implementer reads each file and picks the name; if a file mixes subjects, move tests into existing `<Subject>Tests.cs` files. |

- [x] **Step 1:** Record baseline: `dotnet test Shiro.sln -c Release --nologo` → note `Total: 1720`.
- [x] **Step 2:** `grep -rn "Wave" .github tests/*.runsettings tests/*.json` — update any reference.
- [x] **Step 3:** Perform the renames (`git mv`) and class renames.
- [x] **Step 4:** Run tests. Expected: `Passed!`, `Total: 1720` (same as baseline).
- [x] **Step 5:** Commit `test: rename wave-numbered test files by feature`.

### Task 2: Raise branch coverage on the launch pipeline

**Targets (current floor → new floor):**
| File | Floor now | Target |
|---|---|---|
| `Services/Launch/AccountLaunchService.cs` | 59 | ≥ 70 |
| `Services/Launch/AccountLaunchService.Diagnostics.cs` | 61 | ≥ 70 |
| `AccountManager.Lifecycle.cs` | 62 | ≥ 70 |

**Files:**
- Test: `tests/Shiro.Tests/AccountLaunchServiceBranchTests.cs` (create), `tests/Shiro.Tests/AccountManagerLifecycleBranchTests.cs` (create)
- Modify: `tests/coverage-floors.json`

- [x] **Step 1:** Run coverage: `dotnet test Shiro.sln -c Release --settings tests/coverage.runsettings`; open the generated `coverage.cobertura.xml`, list uncovered branch lines for the three files.
- [x] **Step 2:** For each uncovered branch, write one test named `<Method>_<Condition>_<Expected>` using the existing fakes already used by `AccountLaunchService` tests (search `tests/` for `AccountLaunchService` to reuse setup). No real process/network.
- [x] **Step 3:** If a test reveals a real bug, stop and report it before fixing (separate commit `fix:` with the test).
- [x] **Step 4:** Re-run coverage; set each floor to (measured − 2), never below target.
- [x] **Step 5:** Full test run passes; commit `test(launch): cover remaining AccountLaunchService/Lifecycle branches`.

### Task 3: ToS / risk disclaimer in README

**Files:** Modify `README.md` (insert after the feature list, before "Tải về & Cài đặt").

- [x] **Step 1:** Add a `> [!WARNING]` block in Vietnamese stating: tool is unofficial and not affiliated with Roblox Corporation; multi-instance, cookie capture and anti-AFK may violate Roblox Terms of Use; users bear account risk; `.ROBLOSECURITY` must never be shared.
- [x] **Step 2:** Verify file stays UTF-8 (no BOM change): `git diff --stat README.md` shows only additions.
- [x] **Step 3:** Commit `docs: add Roblox ToS risk disclaimer`.

### Out of scope (separate plan if wanted)
- Refactoring `AccountManager` partial class (10 files, ~2.8k lines) into standalone services. Large, behavior-risky; needs its own brainstorm/spec.
