# Native AOT & JSON Source Generator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate all reflection-based JSON serialization (IL2026 / IL3050 warnings) across Shiro Account Manager, enabling Native AOT compilation to cut binary size from ~110MB to ~25-35MB and achieve near-zero millisecond startup time while preserving 100% of the 529 passing unit tests.

**Architecture:** 
1. Expand `ShiroAppJson` source generation context to cover all DTOs and internal payloads (HBA signatures, CookieGuard payloads, and WebServer responses).
2. Refactor `AccountDataSerializer` to eliminate `DefaultJsonTypeInfoResolver()`, relying strictly on compiled `ShiroAppJson.Default`.
3. Update `Account.HbaBinding`, `RobloxDirectLauncher.CookieGuard`, `FastLoginDialog`, and `KestrelWebServer` to use compiled `JsonTypeInfo` overloads instead of generic reflection overloads.
4. Configure `<PublishAot>` in `Shiro.csproj` and provide single-file AOT publish automation in `scripts/Publish.ps1`.
5. Verify zero trim/AOT warnings, validate all 529 existing tests, and benchmark publish output.

**Tech Stack:** .NET 10.0 (win-x64), C# 13, `System.Text.Json` Source Generators, Avalonia UI 11 (Compiled Bindings), Native AOT (`PublishAot`), xUnit.

**Spec:** Codebase diagnostic audit for Native AOT (`/p:PublishAot=true`):
- Compiler diagnostic revealed that Win32 P/Invoke (`[LibraryImport]`) and Avalonia 11 are already AOT-ready.
- The sole blockers preventing clean Native AOT compilation are ~8 specific call sites of `System.Text.Json` using reflection-based overloads (`IL2026: RequiresUnreferencedCodeAttribute` and `IL3050: RequiresDynamicCodeAttribute`):
  1. `AccountDataSerializer.cs`: constructs `new DefaultJsonTypeInfoResolver()` which requires reflection.
  2. `Account.HbaBinding.cs`: calls `JsonSerializer.Serialize` without `JsonTypeInfo`.
  3. `RobloxDirectLauncher.CookieGuard.cs`: calls `JsonSerializer.Serialize` without `JsonTypeInfo`.
  4. `FastLoginDialog.Automation.cs` & `FastLoginDialog.HbaCapture.cs`: call generic `JsonSerializer.Serialize` and `JsonSerializer.Deserialize<string>`.
  5. `KestrelWebServer.cs`: calls `JsonSerializer.SerializeToUtf8Bytes` without precompiled metadata.

## Global Constraints
- Maximum file length across all changed files must remain strictly under 500 lines per file (enforced by `ArchitectureTests.NoSourceCodeFiles_ShouldExceed500Lines`).
- Partial class budgets in `ArchitectureTests.NoClass_ShouldExceedLineBudget_AcrossPartials` must not be exceeded (`Account`: 2900, `AccountManager`: 3400).
- Preserve 100% backward compatibility for all existing unit tests (529/529 passing).
- Zero memory leaks and no breaking changes to JSON schemas (`AccountData.json`, `RAMSettings.json`).

## Review Focus
1. `AccountDataSerializer.Deserialize` must continue loading both legacy unversioned account arrays and modern versioned envelopes without exceptions.
2. `ShiroAppJson` must register every payload type required by HBA and CookieGuard without missing metadata at runtime.
3. FastLogin web view JavaScript injection and session capture must parse JSON strings accurately using typed converters.
4. Kestrel and WebSocket event streaming (`/ws`, `/events`) must serialize real-time status packets without dynamic code generation.
5. The Native AOT build must succeed with zero `IL2026` or `IL3050` warnings when `TreatWarningsAsErrors=true` is enabled.

---

### Task 1: Eliminate Reflection from `AccountDataSerializer` & Expand `ShiroAppJson`

**Files:**
- Modify: `src/Shiro/Services/ShiroAppJson.cs`
- Modify: `src/Shiro/Services/AccountDataSerializer.cs:20-35`
- Test: `tests/Shiro.Tests/SecurityHardeningTests.cs`

**Interfaces:**
- Consumes: `ShiroAppJson.Default`
- Produces: AOT-safe `AccountDataSerializer` without dynamic resolvers, preserving envelope and array serialization.

- [x] **Step 1: Write unit tests verifying AOT-safe serialization of Account envelopes**

```csharp
[Fact]
public void AccountDataSerializer_SerializeAndDeserialize_UsesSourceGenWithoutReflection()
{
    var accounts = new List<Account> { new Account("TestUser", "password", "alias") };
    byte[] serialized = AccountDataSerializer.Serialize(accounts);
    Assert.NotNull(serialized);
    Assert.True(serialized.Length > 0);

    var deserialized = AccountDataSerializer.Deserialize(serialized, out bool migration);
    Assert.False(migration);
    Assert.Single(deserialized);
    Assert.Equal("TestUser", deserialized[0].Username);
}
```

- [x] **Step 2: Run test to verify current behavior passes**

Run: `dotnet test tests\Shiro.Tests --filter "AccountDataSerializer_SerializeAndDeserialize_UsesSourceGenWithoutReflection"`
Expected: PASS

- [x] **Step 3: Modify `AccountDataSerializer.cs` to remove `new DefaultJsonTypeInfoResolver()`**

Change `JsonOptions.TypeInfoResolver` to use `ShiroAppJson.Default` directly instead of `JsonTypeInfoResolver.Combine(ShiroAppJson.Default, new DefaultJsonTypeInfoResolver())`.

- [x] **Step 4: Register any missing types in `ShiroAppJson.cs`**

Add missing types:
```csharp
[JsonSerializable(typeof(AccountDataSerializer.AccountEnvelope))]
[JsonSerializable(typeof(List<Account>))]
[JsonSerializable(typeof(Account[]))]
```

- [x] **Step 5: Run tests to verify all serialization tests pass**

Run: `dotnet test tests\Shiro.Tests --filter "AccountDataSerializer"`
Expected: PASS

- [x] **Step 6: Commit**

```bash
git add src/Shiro/Services/AccountDataSerializer.cs src/Shiro/Services/ShiroAppJson.cs tests/Shiro.Tests/
git commit -m "perf: eliminate DefaultJsonTypeInfoResolver reflection from AccountDataSerializer"
```

---

### Task 2: Refactor CookieGuard & HBA Binding JSON to Use Source Generator

**Files:**
- Modify: `src/Shiro/Services/ShiroAppJson.cs`
- Modify: `src/Shiro/Classes/Account.HbaBinding.cs:320-335`
- Modify: `src/Shiro/Services/RobloxDirectLauncher.CookieGuard.cs:160-175`
- Test: `tests/Shiro.Tests/RobloxHbaAndIpPersistenceTests.cs`

**Interfaces:**
- Consumes: `ShiroAppJson.Default.DictionaryStringString`, `ShiroAppJson.Default.String`
- Produces: Reflection-free serialization for HBA hardware challenge bindings and CookieGuard payload signatures.

- [x] **Step 1: Write unit tests verifying HBA binding JSON serialization**

Ensure `RobloxHbaAndIpPersistenceTests` exercises HBA challenge signature serialization without runtime reflection.

- [x] **Step 2: Run test to verify baseline**

Run: `dotnet test tests\Shiro.Tests --filter "RobloxHba"`
Expected: PASS

- [x] **Step 3: Update `Account.HbaBinding.cs`**

Replace generic `JsonSerializer.Serialize(payload)` with `JsonSerializer.Serialize(payload, ShiroAppJson.Default.DictionaryStringString)` (or dedicated DTO).

- [x] **Step 4: Update `RobloxDirectLauncher.CookieGuard.cs`**

Replace generic `JsonSerializer.Serialize(...)` with the pre-compiled `JsonTypeInfo` from `ShiroAppJson.Default`.

- [x] **Step 5: Run tests to verify pass**

Run: `dotnet test tests\Shiro.Tests --filter "RobloxHba"`
Expected: PASS

- [x] **Step 6: Commit**

```bash
git add src/Shiro/Classes/Account.HbaBinding.cs src/Shiro/Services/RobloxDirectLauncher.CookieGuard.cs src/Shiro/Services/ShiroAppJson.cs
git commit -m "perf: refactor HBA and CookieGuard serialization to use ShiroAppJson"
```

---

### Task 3: Refactor FastLoginDialog & KestrelWebServer JSON Calls

**Files:**
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.Automation.cs:310-325`
- Modify: `src/Shiro/AvaloniaUI/Views/FastLoginDialog.HbaCapture.cs:95-115`
- Modify: `src/Shiro/Services/KestrelWebServer.cs:165-200, 280-290`
- Modify: `src/Shiro/Services/ShiroAppJson.cs`
- Test: `tests/Shiro.Tests/SecurityHardeningTests.cs`

**Interfaces:**
- Consumes: `ShiroAppJson.Default`
- Produces: Fully static serialization for FastLogin DOM scripts and Kestrel web socket event broadcasts.

- [x] **Step 1: Update FastLogin dialog JSON calls**

In `FastLoginDialog.Automation.cs` and `FastLoginDialog.HbaCapture.cs`, replace calls like:
`JsonSerializer.Serialize(username)` -> `JsonSerializer.Serialize(username, ShiroAppJson.Default.String)`
`JsonSerializer.Deserialize<string>(passJson)` -> `JsonSerializer.Deserialize(passJson, ShiroAppJson.Default.String)`

- [x] **Step 2: Update `KestrelWebServer.cs` serialization calls**

Replace:
`JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions)` with strongly-typed `ShiroAppJson.Default` overloads (e.g. `Command`, `ControlledAccount`, or `JsonElement`).

- [x] **Step 3: Register WebServer message DTOs in `ShiroAppJson.cs` if missing**

Ensure any custom envelope used in WebSocket broadcasts is registered with `[JsonSerializable]`.

- [x] **Step 4: Run unit tests**

Run: `dotnet test tests\Shiro.Tests`
Expected: 529 Passed, 0 Failed.

- [x] **Step 5: Commit**

```bash
git add src/Shiro/AvaloniaUI/Views/FastLoginDialog.*.cs src/Shiro/Services/KestrelWebServer.cs src/Shiro/Services/ShiroAppJson.cs
git commit -m "perf: convert FastLogin and KestrelWebServer to source-generated JSON"
```

---

### Task 4: Configure Native AOT Publishing & Verify Build

**Files:**
- Modify: `src/Shiro/Shiro.csproj`
- Modify: `scripts/Publish.ps1`
- Test: Native AOT compile validation command

**Interfaces:**
- Consumes: AOT-compatible assemblies and source-generated JSON
- Produces: Single-file native executable `Shiro.exe` compiled directly to x64 machine code.

- [x] **Step 1: Update `Shiro.csproj` with AOT configuration profile**

Add conditional property group:
```xml
  <PropertyGroup Condition="'$(PublishAot)' == 'true'">
    <PublishTrimmed>true</PublishTrimmed>
    <TrimMode>partial</TrimMode>
    <IlcGenerateCompleteTypeMetadata>false</IlcGenerateCompleteTypeMetadata>
    <IlcOptimizationPreference>Speed</IlcOptimizationPreference>
  </PropertyGroup>
```

- [x] **Step 2: Update `scripts/Publish.ps1` with `-UseAot` switch**

Add an optional parameter `[switch]$Aot` to `Publish.ps1` allowing publishing via:
`dotnet publish $project --configuration $Configuration --runtime $Runtime --self-contained true /p:PublishAot=true ...`

- [x] **Step 3: Run Native AOT publish test**

Run: `dotnet publish src/Shiro/Shiro.csproj -r win-x64 -c Release /p:PublishAot=true /p:PublishSingleFile=false`
Expected: Succeeded with 0 errors and 0 warnings.

- [x] **Step 4: Verify all unit tests pass on Debug and Release**

Run: `dotnet test tests\Shiro.Tests`
Expected: 529 Passed, 0 Failed.

- [x] **Step 5: Commit**

```bash
git add src/Shiro/Shiro.csproj scripts/Publish.ps1
git commit -m "build: add Native AOT publish profile and script support"
```
