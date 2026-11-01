# SDD ledger — plan: docs/superpowers/plans/2026-11-01-native-aot-and-source-gen.md

Pre-flight: Tasks 1-3 provide typed JSON source generation schemas consumed by Task 4 for Native AOT compilation. No conflicting interfaces detected.

- Task 1: complete. Eliminated DefaultJsonTypeInfoResolver from AccountDataSerializer; verified with GoldenCompatibilityTests and AccountDataSerializerTests.

- Task 2: complete. Refactored Account.HbaBinding and RobloxDirectLauncher.CookieGuard to strongly-typed record structs in ShiroAppJson; all 20 HBA and CookieGuard tests passed.

- Task 3: complete. Converted FastLoginDialog and KestrelWebServer to source-generated JSON; all 529 unit tests passed.

- Task 4: complete. Configured Native AOT profile in Shiro.csproj and updated Publish.ps1 automation; successfully published release single-file executable Shiro.exe and verified all 529 unit tests pass.
