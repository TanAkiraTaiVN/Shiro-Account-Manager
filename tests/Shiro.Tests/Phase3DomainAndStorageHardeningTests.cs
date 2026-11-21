#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;

namespace Shiro.Tests;

[Collection("SerialGlobalState")]
public sealed class Phase3DomainAndStorageHardeningTests : IDisposable
{
    private readonly string _tempDir;

    public Phase3DomainAndStorageHardeningTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ShiroPhase3T6_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        AccountManager.ResetAccountsForTests(Array.Empty<Account>());
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task Account_BeginSuppressDeferredSaveScope_FlowsAcrossAwaitThreadHops()
    {
        Assert.False(Account.IsSuppressingDeferredSave);

        bool insideOuterTask = false;
        bool insideNestedTask = false;
        bool afterNestedDispose = false;
        bool afterAwaitInScope = false;

        using (Account.BeginSuppressDeferredSaveScope())
        {
            Assert.True(Account.IsSuppressingDeferredSave);

            await Task.Run(async () =>
            {
                await Task.Delay(5).ConfigureAwait(false);
                insideOuterTask = Account.IsSuppressingDeferredSave;

                using (Account.BeginSuppressDeferredSaveScope())
                {
                    await Task.Yield();
                    insideNestedTask = Account.IsSuppressingDeferredSave;
                }

                afterNestedDispose = Account.IsSuppressingDeferredSave;
            });

            afterAwaitInScope = Account.IsSuppressingDeferredSave;
        }

        bool afterOuterDispose = Account.IsSuppressingDeferredSave;
        bool onThreadPoolAfterDispose = await Task.Run(() => Account.IsSuppressingDeferredSave);

        Assert.True(insideOuterTask);
        Assert.True(insideNestedTask);
        Assert.True(afterNestedDispose);
        Assert.True(afterAwaitInScope);
        Assert.False(afterOuterDispose);
        Assert.False(onThreadPoolAfterDispose);
    }

    [Fact]
    public async Task AccountWorkflowService_ReLoginWithCookieAsync_RestoresPreviousTokenWhenValidateThrows()
    {
        const string previousToken = "_|WARNING:-DO-NOT-SHARE-THIS.--previous-valid-token";
        const string attemptedToken = "_|WARNING:-DO-NOT-SHARE-THIS.--attempted-new-token";
        DateTime previousTokenSet = new(2026, 2, 1, 10, 30, 0, DateTimeKind.Utc);

        var dialogs = Substitute.For<IDialogService>();
        dialogs.ShowInputPromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult(attemptedToken));

        var repo = Substitute.For<IAccountRepository>();
        var logger = Substitute.For<IAppLogger>();

        using var dialogsScope = AppServices.Override<IDialogService>(dialogs);
        using var repoScope = AppServices.Override<IAccountRepository>(repo);

        var account = new Account
        {
            Username = "RollbackUser",
            UserID = 4242,
            SecurityToken = previousToken,
            Valid = true,
            AccountStatusIssue = string.Empty,
            CSRFToken = "prev-csrf",
            TokenSet = previousTokenSet
        };
        var vm = new AccountViewModel(account);
        var mainVm = new MainViewModel();
        var workflow = new AccountWorkflowService(logger)
        {
            TestValidateSessionAsync = _ => throw new HttpRequestException("Simulated validation transport error")
        };

        string status = string.Empty;
        await workflow.ReLoginWithCookieAsync(vm, mainVm, s => status = s);

        Assert.Equal(previousToken, account.SecurityToken);
        Assert.True(account.Valid);
        Assert.Equal(4242L, account.UserID);
        Assert.Equal("RollbackUser", account.Username);
        Assert.Equal(string.Empty, account.AccountStatusIssue);
        Assert.Equal("prev-csrf", account.CSRFToken);
        Assert.Equal(previousTokenSet, account.TokenSet);
        Assert.NotEmpty(status);
        logger.Received().Warn(Arg.Is<string>(m => m.Contains("Simulated validation transport error", StringComparison.Ordinal)));
        await repo.DidNotReceive().SaveAsync(Arg.Any<bool>());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TriggerResetEncryptionSync(AccountPersistenceService persistence)
    {
        persistence.ResetEncryption(manualReset: true);
    }

    [Fact]
    public async Task AccountPersistenceService_ResetEncryptionSync_ObservesTaskExceptionsSafely()
    {
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            if (args.Exception.Flatten().InnerExceptions.Count > 0 &&
                args.Exception.ToString().Contains("Simulated prompt failure", StringComparison.Ordinal))
            {
                unobserved.Add(args.Exception);
                args.SetObserved();
            }
        }

        var dialogs = Substitute.For<IDialogService>();
        var logger = Substitute.For<IAppLogger>();
        using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);
        using var dialogsScope = AppServices.Override<IDialogService>(dialogs);
        using var loggerScope = AppServices.Override<IAppLogger>(logger);

        var persistence = AccountPersistenceService.Default;
        var prevPrompt = persistence.TestPasswordPromptAsync;
        persistence.TestPasswordPromptAsync = async (_, _) =>
        {
            await Task.Yield();
            throw new InvalidOperationException("Simulated prompt failure");
        };

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            TriggerResetEncryptionSync(persistence);
            await Task.Delay(80);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.Empty(unobserved);
            logger.Received().Error(Arg.Is<string>(m => m.Contains("Simulated prompt failure", StringComparison.Ordinal)));
            dialogs.Received().ShowError(Arg.Is<string>(m => m.Contains("Simulated prompt failure", StringComparison.Ordinal)), Arg.Any<string>());
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
            persistence.TestPasswordPromptAsync = prevPrompt;
        }
    }

    [Theory]
    [InlineData("alice:p@ss|word", "alice", "p@ss|word")]
    [InlineData("alice|sec:ret:123", "alice", "sec:ret:123")]
    [InlineData("user\tpass|123", "user", "pass|123")]
    [InlineData("bob:pw\twith:tab", "bob", "pw\twith:tab")]
    [InlineData(":missingUser", "", "")]
    [InlineData("missingPass:", "", "")]
    public void ParseUserPassCombo_PrefersEarliestDelimiter_SupportingPipeAndColonPasswords(
        string combo, string expectedUser, string expectedPass)
    {
        var (username, password) = AccountSessionMaintenanceService.ParseUserPassCombo(combo);
        Assert.Equal(expectedUser, username);
        Assert.Equal(expectedPass, password);
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("prn", "_prn")]
    [InlineData("AUX.txt", "_AUX.txt")]
    [InlineData("nul.json", "_nul.json")]
    [InlineData("COM1.log", "_COM1.log")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("con . .", "_con")]
    [InlineData("foo . .", "foo")]
    [InlineData("Normal_User.1", "Normal_User.1")]
    [InlineData("console", "console")]
    [InlineData("COM10", "COM10")]
    public void MakeSafeFileName_RejectsWindowsReservedDeviceNames_WithOrWithoutExtensions(
        string input, string expected)
    {
        string actual = AccountSessionMaintenanceService.MakeSafeFileName(input, "fallback");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void VaultKeyRecoveryService_TryRecoverInProcess_ClonesAndZerosCandidateBuffers()
    {
        using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(_tempDir);

        byte[] raw32 = new byte[32];
        Random.Shared.NextBytes(raw32);
        byte[] raw32Copy = raw32.AsSpan().ToArray();

        byte[]? extracted = VaultKeyRecoveryService.TryExtractKeyFromFileBytes(raw32);
        Assert.NotNull(extracted);
        Assert.NotSame(raw32, extracted);
        CryptographicOperations.ZeroMemory(raw32);
        Assert.Equal(raw32Copy, extracted);

        byte[] validKey = new byte[32];
        Random.Shared.NextBytes(validKey);
        byte[] wrongKey = new byte[32];
        Random.Shared.NextBytes(wrongKey);

        byte[] payload = Encoding.UTF8.GetBytes("[{\"Username\":\"Phase3RecoveryUser\"}]");
        byte[] encrypted = Cryptography.Encrypt(payload.AsSpan(), validKey);

        // Only corrupt/wrong candidate exists -> returns null and leaves primary vault key uncreated.
        File.WriteAllBytes(StoragePaths.VaultKeyFile + ".bak", wrongKey);
        Assert.Null(VaultKeyRecoveryService.TryRecoverInProcess(encrypted));
        Assert.False(File.Exists(StoragePaths.VaultKeyFile));

        // Add DPAPI-wrapped valid key in .previous -> recovers valid key after skipping wrong .bak.
        byte[] wrappedValid = StoragePaths.BuildProtectedVaultKeyPayload(validKey);
        File.WriteAllBytes(StoragePaths.VaultKeyFile + ".previous", wrappedValid);

        byte[]? recovered = VaultKeyRecoveryService.TryRecoverInProcess(encrypted);
        Assert.Equal(validKey, recovered);
        Assert.True(File.Exists(StoragePaths.VaultKeyFile));
    }

    [Fact]
    public void StoragePaths_AtomicWriteText_WritesUtf8AndCleansTempFiles()
    {
        string targetPath = Path.Combine(_tempDir, "atomic_utf8.txt");
        const string content = "Shiro-Vault-Check-12345";

        StoragePaths.AtomicWriteText(targetPath, content, durable: false);

        Assert.Equal(content, File.ReadAllText(targetPath, Encoding.UTF8));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));
    }
}
