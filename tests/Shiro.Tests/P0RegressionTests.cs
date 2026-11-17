#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class P0RegressionTests
    {
        private sealed class StubDialogService : IDialogService
        {
            public bool ConfirmationResult { get; set; } = true;
            public void ShowInfo(string message, string title = "Shiro") { }
            public void ShowWarning(string message, string title = "Shiro") { }
            public void ShowError(string message, string title = "Shiro") { }
            public bool ShowConfirmation(string message, string title = "Shiro") => ConfirmationResult;
            public bool? ShowYesNoCancel(string message, string title = "Shiro") => ConfirmationResult;
            public string ShowInputPrompt(string message, string title, string defaultValue = "") => string.Empty;
            public void OpenFastLoginDialog(Account? account = null) { }
            public void OpenFastLoginDialogBatch(IReadOnlyList<Account> accounts) { }
            public void OpenBulkImportDialog() { }
            public void OpenProxyManagerDialog() { }
            public void OpenAboutDialog() { }
            public void OpenAccountFieldsDialog(Account account) { }
            public void OpenAccountUtilsDialog(Account account) { }
            public void OpenMissingAssetsDialog(Account account, long[] assetIds) { }
        }

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Shiro.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate repository root containing Shiro.sln.");
        }

        [Fact]
        public void Crit01_LoadAccounts_WithoutMasterPassword_ClearsStalePasswordHash()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroCrit01_Load_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using (AppServices.Override<IDialogService>(new StubDialogService { ConfirmationResult = true }))
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    StoragePaths.ClearCachedVaultKey();
                    byte[] vaultKey = StoragePaths.GetOrCreateVaultKey();
                    byte[] masterHash = SHA512.HashData(Encoding.UTF8.GetBytes("OldMasterPass!123"));

                    var masterAccounts = new List<Account> { new() { Username = "MasterUser", UserID = 101 } };
                    byte[] masterEncrypted = Cryptography.Encrypt(
                        AccountDataSerializer.Serialize(masterAccounts, includePasswords: true),
                        masterHash);

                    var vaultKeyAccounts = new List<Account> { new() { Username = "VaultUser", UserID = 202 } };
                    byte[] vaultKeyEncrypted = Cryptography.EncryptWithRandomKey(
                        AccountDataSerializer.Serialize(vaultKeyAccounts, includePasswords: true),
                        vaultKey);

                    string accountFile = Path.Combine(tempDir, "AccountData.json");
                    string backupsDir = Path.Combine(tempDir, "Backups");
                    Directory.CreateDirectory(backupsDir);

                    var repo = new AccountRepository();
                    var persistence = new AccountPersistenceService(repo)
                    {
                        Store = new AccountStore(accountFile, backupsDir),
                        StartTime = DateTime.Now.AddMinutes(-5)
                    };

                    // 1. First unlock a Master Password encrypted database (stores protected PasswordHash).
                    File.WriteAllBytes(accountFile, masterEncrypted);
                    persistence.LoadAccounts(masterHash, out bool masterUnlocked);
                    Assert.True(masterUnlocked);

                    // 2. Now load a vault-key encrypted database WITHOUT a Master Password.
                    File.WriteAllBytes(accountFile, vaultKeyEncrypted);
                    persistence.LoadAccounts(null, out bool vaultUnlocked);
                    Assert.True(vaultUnlocked);

                    // 3. Save accounts; stale PasswordHash MUST NOT silently re-encrypt with old master password.
                    repo.GetAll()[0].IsDirty = true;
                    persistence.SaveAccounts(bypassRateLimit: true, bypassCountCheck: true);

                    var freshPersistence = new AccountPersistenceService(new AccountRepository())
                    {
                        Store = new AccountStore(accountFile, backupsDir)
                    };
                    freshPersistence.LoadAccounts(null, out bool reopenedWithoutPassword);
                    Assert.True(reopenedWithoutPassword);
                }
            }
            finally
            {
                StoragePaths.ClearCachedVaultKey();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                catch (IOException) { }
            }
        }

        [Fact]
        public void Crit01_LoadAccounts_FallbackToVaultKeyBackup_ClearsStalePasswordHash()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroCrit01_Backup_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using (AppServices.Override<IDialogService>(new StubDialogService { ConfirmationResult = true }))
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    StoragePaths.ClearCachedVaultKey();
                    byte[] vaultKey = StoragePaths.GetOrCreateVaultKey();
                    byte[] masterHash = SHA512.HashData(Encoding.UTF8.GetBytes("OldMasterPass!456"));

                    string accountFile = Path.Combine(tempDir, "AccountData.json");
                    string backupFile = accountFile + ".backup";
                    string backupsDir = Path.Combine(tempDir, "Backups");
                    Directory.CreateDirectory(backupsDir);

                    var masterAccounts = new List<Account> { new() { Username = "MasterUser", UserID = 301 } };
                    File.WriteAllBytes(
                        accountFile,
                        Cryptography.Encrypt(AccountDataSerializer.Serialize(masterAccounts, includePasswords: true), masterHash));

                    var repo = new AccountRepository();
                    var persistence = new AccountPersistenceService(repo)
                    {
                        Store = new AccountStore(accountFile, backupsDir),
                        StartTime = DateTime.Now.AddMinutes(-5)
                    };

                    persistence.LoadAccounts(masterHash, out bool unlockedMaster);
                    Assert.True(unlockedMaster);

                    // Corrupt primary (unencrypted garbage -> triggers TryLoadFromBackup branch), place valid vault-key backup.
                    var backupAccounts = new List<Account> { new() { Username = "BackupVaultUser", UserID = 302 } };
                    repo.ReplaceAll(new List<Account>());
                    File.WriteAllBytes(accountFile, Encoding.UTF8.GetBytes("CORRUPT_PRIMARY"));
                    File.WriteAllBytes(
                        backupFile,
                        Cryptography.EncryptWithRandomKey(AccountDataSerializer.Serialize(backupAccounts, includePasswords: true), vaultKey));

                    persistence.LoadAccounts(null, out _);

                    var freshPersistence = new AccountPersistenceService(new AccountRepository())
                    {
                        Store = new AccountStore(accountFile, backupsDir)
                    };
                    freshPersistence.LoadAccounts(null, out bool reopenedWithoutPassword);
                    Assert.True(reopenedWithoutPassword);

                    var checkRepo = new AccountRepository();
                    var verifyPersistence = new AccountPersistenceService(checkRepo) { Store = new AccountStore(accountFile, backupsDir) };
                    verifyPersistence.LoadAccounts(null, out bool verified);
                    Assert.True(verified);
                    Assert.Equal("BackupVaultUser", Assert.Single(checkRepo.GetAll()).Username);
                }
            }
            finally
            {
                StoragePaths.ClearCachedVaultKey();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                catch (IOException) { }
            }
        }

        [Fact]
        public void HighC3_LoadOrCreateVaultKeyAtPath_WhenEncryptedVaultExistsAndKeyMissing_DoesNotCreateOrphanKeyFile()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ShiroHighC3_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using (AppServices.Override<IDialogService>(new StubDialogService { ConfirmationResult = true }))
                using (StoragePaths.OverrideDataDirectoryForTesting(tempDir))
                {
                    StoragePaths.ClearCachedVaultKey();
                    byte[] realKey = RandomNumberGenerator.GetBytes(32);
                    var accounts = new List<Account> { new() { Username = "ProtectedVaultUser", UserID = 505 } };
                    byte[] encryptedVault = Cryptography.EncryptWithRandomKey(
                        AccountDataSerializer.Serialize(accounts, includePasswords: true),
                        realKey);

                    File.WriteAllBytes(StoragePaths.AccountDataFile, encryptedVault);
                    // No vault.key and no vault.key.bak exist on disk yet (or key is in .previous only).
                    string keyFile = StoragePaths.VaultKeyFile;
                    Assert.False(File.Exists(keyFile));

                    Assert.Throws<CryptographicException>(() =>
                        StoragePaths.LoadOrCreateVaultKeyAtPath(keyFile, hasExistingEncryptedVault: true));

                    Assert.False(File.Exists(keyFile),
                        "Must NOT auto-generate and persist an orphan vault.key when an encrypted vault already exists without readable primary/bak keys.");
                }
            }
            finally
            {
                StoragePaths.ClearCachedVaultKey();
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                catch (IOException) { }
            }
        }

        [Fact]
        public void HighA3_ShiroAppJson_ContainsTokenRequestAndListTypeInfoForNativeAot()
        {
            JsonTypeInfo? singleInfo = ShiroAppJson.Default.GetTypeInfo(typeof(TokenRequest));
            JsonTypeInfo? listInfo = ShiroAppJson.Default.GetTypeInfo(typeof(List<TokenRequest>));

            Assert.NotNull(singleInfo);
            Assert.NotNull(listInfo);

            var payload = new List<TokenRequest> { new("sample_avatar_token") };
            byte[] utf8 = JsonSerializer.SerializeToUtf8Bytes(payload, (JsonTypeInfo<List<TokenRequest>>)listInfo!);
            string json = Encoding.UTF8.GetString(utf8);
            Assert.Contains("0:sample_avatar_token:AvatarHeadshot", json, StringComparison.Ordinal);
        }

        [Fact]
        public void HighA2_Program_Main_AcquiresMutexBeforeStorageLoggerOrCleanup()
        {
            string programPath = Path.Combine(FindRepoRoot(), "src", "Shiro", "Program.cs");
            string content = File.ReadAllText(programPath);

            Assert.DoesNotContain("public static readonly IAppLogger Logger = CreateLogger();", content, StringComparison.Ordinal);

            int mutexWaitIdx = content.IndexOf("mutex.WaitOne", StringComparison.Ordinal);
            int mainIdx = content.IndexOf("static void Main", StringComparison.Ordinal);
            int storageInitInMainIdx = content.IndexOf("StoragePaths.EnsureInitialized", mainIdx, StringComparison.Ordinal);
            int cleanupIdx = content.IndexOf("CleanupStaleRuntimeArtifacts", mainIdx, StringComparison.Ordinal);

            Assert.True(mutexWaitIdx > mainIdx, "Program.Main must call mutex.WaitOne.");
            Assert.True(storageInitInMainIdx > mutexWaitIdx,
                "Program.Main must acquire the single-instance mutex BEFORE calling StoragePaths.EnsureInitialized().");
            Assert.True(cleanupIdx > mutexWaitIdx,
                "Program.Main must acquire the single-instance mutex BEFORE running CleanupStaleRuntimeArtifacts.");
        }

        [Fact]
        public void HighA1_AppExit_InvokesAccountManagerShutdownBackend()
        {
            string appCsPath = Path.Combine(FindRepoRoot(), "src", "Shiro", "AvaloniaUI", "App.axaml.cs");
            string content = File.ReadAllText(appCsPath);

            Assert.Contains("ShutdownBackend()", content, StringComparison.Ordinal);
        }

        [Fact]
        public void HighG4_AllUnmanagedCallersOnlyCallbacks_MustWrapBodyInTopLevelTryCatch()
        {
            string srcRoot = Path.Combine(FindRepoRoot(), "src", "Shiro");
            var csFiles = Directory.GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/"))
                .ToList();

            var callbackRegex = new Regex(@"\[UnmanagedCallersOnly\b", RegexOptions.Compiled);
            var inspectedMethods = new List<string>();
            var violations = new List<string>();

            foreach (string file in csFiles)
            {
                string text = File.ReadAllText(file);
                foreach (Match match in callbackRegex.Matches(text))
                {
                    int attrClose = FindMatchingBracket(text, match.Index);
                    Assert.True(attrClose > match.Index, $"Could not parse attribute end after [UnmanagedCallersOnly] in {file}");

                    int sigEndBrace = text.IndexOf('{', attrClose + 1);
                    Assert.True(sigEndBrace > attrClose, $"Could not parse method body after [UnmanagedCallersOnly] in {file}");

                    string headerSlice = text.Substring(attrClose + 1, sigEndBrace - attrClose - 1);
                    string methodLabel = $"{Path.GetFileName(file)}: {headerSlice.Replace('\r', ' ').Replace('\n', ' ').Trim()}";
                    inspectedMethods.Add(methodLabel);

                    int bodyClose = FindMatchingBrace(text, sigEndBrace);
                    Assert.True(bodyClose > sigEndBrace, $"Unmatched braces for {methodLabel}");

                    string body = text.Substring(sigEndBrace + 1, bodyClose - sigEndBrace - 1).TrimStart();
                    // Must start directly with try { ... } catch so no managed exception escapes into native frame
                    bool hasTopLevelTry = body.StartsWith("try", StringComparison.Ordinal) &&
                                          Regex.IsMatch(body, @"\bcatch\s*\(", RegexOptions.CultureInvariant);
                    if (!hasTopLevelTry)
                    {
                        violations.Add(methodLabel);
                    }
                }
            }

            Assert.True(inspectedMethods.Count >= 4, $"Expected at least 4 [UnmanagedCallersOnly] callbacks, found {inspectedMethods.Count}.");
            Assert.True(violations.Count == 0,
                $"UnmanagedCallersOnly callbacks missing top-level try/catch:\n{string.Join("\n", violations)}");
        }

        [Fact]
        public void HighA1_ExecuteDesktopExitCleanup_CompletesWithoutThrowing()
        {
            bool previousClosed = Program.Closed;
            bool shutdownInvoked = false;
            var bridge = new HostUiBridgeService { ShutdownBackendHandler = () => shutdownInvoked = true };
            try
            {
                Program.Closed = false;
                using (AppServices.Override<IHostUiBridgeService>(bridge))
                {
                    Shiro.AvaloniaUI.App.ExecuteDesktopExitCleanup(null);
                }
                Assert.True(Program.Closed);
                Assert.True(shutdownInvoked);
            }
            finally
            {
                Program.Closed = previousClosed;
            }
        }

        [Fact]
        public void HighG4_CallbackCoreHelpers_HandleNullLParamSafely()
        {
            Assert.Equal(1, RobloxWindowService.FindWindowForPidCallbackCore(IntPtr.Zero, IntPtr.Zero));
            Assert.Equal(1, RobloxWindowService.FindWindowsForPidsCallbackCore(IntPtr.Zero, IntPtr.Zero));
            Assert.Equal(1, Win32TrayHelper.EnumTopWindowsCallbackCore(IntPtr.Zero, IntPtr.Zero));
            Assert.Equal(1, Win32TrayHelper.EnumChildWindowsCallbackCore(IntPtr.Zero, IntPtr.Zero));
        }

        private static int FindMatchingBracket(string text, int openBracketIndex)
        {
            int depth = 0;
            for (int i = openBracketIndex; i < text.Length; i++)
            {
                if (text[i] == '[') depth++;
                else if (text[i] == ']')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static int FindMatchingBrace(string text, int openBraceIndex)
        {
            int depth = 0;
            for (int i = openBraceIndex; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }
    }
}
