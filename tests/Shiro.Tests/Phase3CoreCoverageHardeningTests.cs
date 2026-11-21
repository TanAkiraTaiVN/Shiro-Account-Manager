#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests;

[Collection("SerialGlobalState")]
public sealed class Phase3CoreCoverageHardeningTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IAppLogger _origHbaLogger = RobloxHbaService.Logger;

    public Phase3CoreCoverageHardeningTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ShiroPhase3T10_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        RobloxHbaService.Logger = _origHbaLogger;
        RobloxHbaService.ClearKeyCache();
        CryptoHelper.TestProtectBytesOverride = null;
        RobloxDirectLauncher.ResetCookieGuardStateForTest();
        RobloxWatcher.Stop();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); }
            catch (IOException) { }
        }
    }

    private sealed class ThrowingHttpContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new IOException("Simulated content failure"));

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) =>
            throw new IOException("Simulated non-seekable read failure");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }

    private sealed class ReadOnlyMemoryStream : MemoryStream
    {
        public override bool CanWrite => false;
    }

    private sealed class WriteOnlyStream : MemoryStream
    {
        public override bool CanRead => false;
    }

    [Fact]
    public async Task RobloxHbaService_EdgeBranches_AndCacheEviction_BehaveSafely()
    {
        var mockLogger = Substitute.For<IAppLogger>();
        RobloxHbaService.Logger = mockLogger;
        Assert.Same(mockLogger, RobloxHbaService.Logger);
        Assert.Throws<ArgumentNullException>(() => RobloxHbaService.Logger = null!);

        Assert.False(RobloxHbaService.IsWithinPostInteractiveLoginGracePeriod(TimeSpan.Zero));

        using var badJson = JsonDocument.Parse("[1,2,3]");
        RobloxHbaService.MergeCapturePayloadIntoSnapshot(null!, badJson.RootElement);
        var snap = new RobloxHbaService.CaptureSnapshot();
        RobloxHbaService.MergeCapturePayloadIntoSnapshot(snap, badJson.RootElement);
        Assert.False(snap.HasPrivateKey);

        Assert.False(RobloxHbaService.DoPublicAndPrivateKeysMatch("not-base64!", "not-base64!"));
        Assert.False(RobloxHbaService.TryBuildBoundAuthToken("not-valid-key", "https://auth.roblox.com/v1/login", "POST", "{}", null, 1700000000, out string emptyBat));
        Assert.Equal(string.Empty, emptyBat);

        // Exercise >= 64 key cache eviction path cleanly
        RobloxHbaService.ClearKeyCache();
        for (int i = 0; i < 65; i++)
        {
            using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string pkcs8 = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
            bool built = RobloxHbaService.TryBuildBoundAuthToken(pkcs8, "https://users.roblox.com/v1/users/authenticated", "GET", null, null, 1700000000 + i, out string bat);
            Assert.True(built);
            Assert.StartsWith("v1|", bat, StringComparison.Ordinal);
        }

        // Binding.cs edge branches: valid timestamp override, invalid HBA key, non-seekable/throwing content, null WebView2
        using ECDsa validKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string validPriv = Convert.ToBase64String(validKey.ExportPkcs8PrivateKey());
        string otherPub = Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo());

        var validAccount = new Account { Username = "HbaUser", HbaPrivateKey = validPriv, HbaPublicKey = otherPub };
        string? tsToken = RobloxHbaService.GenerateBoundAuthToken(validAccount, "POST", "https://auth.roblox.com/v1/logout", "1700000123");
        Assert.NotNull(tsToken);
        Assert.Contains("|1700000123|", tsToken!, StringComparison.Ordinal);

        var corruptKeyAccount = new Account { Username = "BadHba", HbaPrivateKey = "invalid-pkcs8-base64" };
        Assert.Null(RobloxHbaService.GenerateBoundAuthToken(corruptKeyAccount, "GET", "https://auth.roblox.com/v1/test", null));

        using var reqBadKey = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v1/test");
        Assert.False(RobloxHbaService.TryAttachHbaHeader(reqBadKey, corruptKeyAccount, "{}"));
        Assert.False(await RobloxHbaService.TryAttachHbaHeaderAsync(reqBadKey, corruptKeyAccount));

        using var reqThrowing = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v1/test") { Content = new ThrowingHttpContent() };
        Assert.False(RobloxHbaService.TryAttachHbaHeader(reqThrowing, validAccount, null));
        Assert.False(await RobloxHbaService.TryAttachHbaHeaderAsync(reqThrowing, validAccount));
        Assert.False(await RobloxHbaService.TryAttachHbaHeaderAsync(null!, validAccount));

        using var reqNonSeek = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v1/test")
        {
            Content = new StreamContent(new NonSeekableStream())
        };
        Assert.False(RobloxHbaService.TryAttachHbaHeader(reqNonSeek, validAccount, null));

        Assert.Null(await RobloxHbaService.RestoreWebViewModerationSessionAsync(null!, validAccount, snap));
        Assert.False(RobloxHbaService.TryDeriveSpkiPublicKeyBase64("  ", out string spkiEmpty));
        Assert.Equal(string.Empty, spkiEmpty);
        Assert.False(RobloxHbaService.TryDeriveSpkiPublicKeyBase64("bad-base64", out string spkiBad));
        Assert.Equal(string.Empty, spkiBad);

        RobloxHbaService.ApplyCaptureToAccount(validAccount, new RobloxHbaService.CaptureSnapshot { PrivateKeyPkcs8Base64 = validPriv });
        Assert.Equal(string.Empty, validAccount.HbaPublicKey);
        Assert.Equal("offline", RobloxHbaService.GetNetworkSignatureForAccount(validAccount, "offline"));
    }

    [Fact]
    public void ShutdownCoordinator_VaultExport_VaultKeyRecovery_And_Crypto_EdgeCases()
    {
        var coord = new ShutdownCoordinator();
        Assert.Throws<ArgumentException>(() => coord.Register(ShutdownPhase.StopMonitors, "   ", () => { }));
        int count = 0;
        coord.Register(ShutdownPhase.StopMonitors, "first", () => count++);
        IReadOnlyList<string> ran = coord.ExecuteAll();
        Assert.Equal(new[] { "first" }, ran);
        coord.Register(ShutdownPhase.FlushPersistence, "after-exec", () => count++);
        Assert.Equal(1, count);

        // VaultExportService non-writable, non-readable, truncated streams
        using (var ro = new ReadOnlyMemoryStream())
        {
            Assert.Throws<ArgumentException>(() => VaultExportService.ExportVault(Array.Empty<Account>(), "pass", ro));
        }
        using (var wo = new WriteOnlyStream())
        {
            Assert.Throws<ArgumentException>(() => VaultExportService.ImportVault(wo, "pass"));
        }
        using (var truncated = new MemoryStream(new byte[10]))
        {
            Assert.Throws<InvalidDataException>(() => VaultExportService.ImportVault(truncated, "pass"));
        }

        // VaultKeyRecoveryService invalid inputs & corrupt DPAPI payload & directory collision
        Assert.Null(VaultKeyRecoveryService.TryRecoverInProcess(null!));
        Assert.Null(VaultKeyRecoveryService.TryRecoverInProcess(new byte[] { 1, 2, 3 }));

        byte[] corruptDpapi = new byte[StoragePaths.DpapiKeyHeader.Length + 16];
        StoragePaths.DpapiKeyHeader.CopyTo(corruptDpapi, 0);
        Assert.Null(VaultKeyRecoveryService.TryExtractKeyFromFileBytes(corruptDpapi));

        byte[] wrongLenDpapiPayload = ProtectedData.Protect(new byte[10], StoragePaths.DpapiKeyEntropy, DataProtectionScope.CurrentUser);
        byte[] wrongLenFile = new byte[StoragePaths.DpapiKeyHeader.Length + wrongLenDpapiPayload.Length];
        StoragePaths.DpapiKeyHeader.CopyTo(wrongLenFile, 0);
        wrongLenDpapiPayload.CopyTo(wrongLenFile, StoragePaths.DpapiKeyHeader.Length);
        Assert.Null(VaultKeyRecoveryService.TryExtractKeyFromFileBytes(wrongLenFile));

        using (StoragePaths.OverrideDataDirectoryForTesting(_tempDir))
        {
            // Directory with candidate filename forces UnauthorizedAccessException in File.ReadAllBytes
            Directory.CreateDirectory(StoragePaths.VaultKeyFile + ".bak");
            byte[] key = new byte[32];
            Random.Shared.NextBytes(key);
            byte[] encrypted = Cryptography.Encrypt(Encoding.UTF8.GetBytes("[{\"Username\":\"Rec\"}]"), key);
            Assert.Null(VaultKeyRecoveryService.TryRecoverInProcess(encrypted));

            // LoadOrCreateVaultKeyAtPath recreates missing .bak and HasExistingEncryptedVaultFile handles 0-byte file
            string isolatedKey = Path.Combine(_tempDir, "isolated.key");
            byte[] loaded1 = StoragePaths.LoadOrCreateVaultKeyAtPath(isolatedKey);
            File.Delete(isolatedKey + ".bak");
            byte[] loaded2 = StoragePaths.LoadOrCreateVaultKeyAtPath(isolatedKey);
            Assert.Equal(loaded1, loaded2);
            Assert.True(File.Exists(isolatedKey + ".bak"));

            string emptyVault = Path.Combine(_tempDir, "empty.json");
            File.WriteAllBytes(emptyVault, Array.Empty<byte>());
            Assert.False(StoragePaths.HasExistingEncryptedVaultFile(emptyVault));
        }

        // CryptoHelper TestProtectBytesOverride
        CryptoHelper.TestProtectBytesOverride = bytes => new byte[] { (byte)(bytes[0] ^ 0xFF) };
        string protectedStr = CryptoHelper.ProtectSecretString("abc");
        Assert.StartsWith("dpapi:", protectedStr, StringComparison.Ordinal);

        // AccountFileDecoder: UnprotectDpapi returns invalid JSON -> Corrupt
        var keys = new AccountDecodeKeys(
            () => Array.Empty<byte>(),
            null,
            _ => (Encoding.UTF8.GetBytes("{not-valid-account-json"), false),
            PlaintextOptIn: false);
        AccountDecodeResult decoded = AccountFileDecoder.Decode(new byte[] { 1, 2, 3, 4 }, null, keys);
        Assert.Equal(AccountDecodeStatus.Corrupt, decoded.Status);
    }

    [Fact]
    public void LogRedactor_And_WebAuthPolicy_UncoveredBranches_AreEnforced()
    {
        string rawProxy = "Connecting via http://alice:s3cr3t@proxy.example.com/path";
        string redactedRaw = LogRedactor.Redact(rawProxy);
        Assert.DoesNotContain("alice", redactedRaw, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", redactedRaw, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_PROXY_AUTH]@", redactedRaw, StringComparison.Ordinal);

        string encodedProxy = "url=http%3A%2F%2Falice%3As3cr3t%40proxy.example.com";
        string redactedEncoded = LogRedactor.Redact(encodedProxy);
        Assert.DoesNotContain("s3cr3t", redactedEncoded, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_PROXY_AUTH]%40", redactedEncoded, StringComparison.Ordinal);

        WebAuthPolicy.ResetLockoutsForTesting();
        Assert.False(WebAuthPolicy.IsClientLockedOut(null));
        Assert.False(WebAuthPolicy.IsClientLockedOut("   "));
        Assert.False(WebAuthPolicy.RecordAuthResult("client-x", AuthDecision.ForbiddenOrigin));
        Assert.True(WebAuthPolicy.IsLoopbackHostName("::ffff:127.0.0.1"));
        Assert.False(WebAuthPolicy.IsOriginAllowed("not a valid uri", "example.com", allowExternal: true));
        Assert.False(WebAuthPolicy.IsOriginAllowed("ftp://example.com", "example.com", allowExternal: true));
    }

    [Fact]
    public async Task CookieGuard_And_DeElevation_EdgeBranches_HandleGuardsAndRetries()
    {
        RobloxDirectLauncher.ResetCookieGuardStateForTest();
        RobloxDirectLauncher.TestDefaultLocalStorageDirOverride = _tempDir;

        // Post-spawn PID <= 0 or no pending backup returns immediately
        RobloxDirectLauncher.SchedulePostSpawnCookieRestore(0);
        RobloxDirectLauncher.SchedulePostSpawnCookieRestore(999999);
        Assert.False(RobloxDirectLauncher.HasPendingCookieBackup);

        // Held CookieGuardScope blocks default-dir RestoreOriginalCookiesCore
        using (var scope = RobloxDirectLauncher.BeginCookieGuardScope(restoreDelayOnDisposeMs: 0))
        {
            Assert.True(RobloxDirectLauncher.IsCookieGuardHeld);
            Assert.False(RobloxDirectLauncher.RestoreOriginalCookiesCore(localStorageDirectoryOverride: null));
        }

        // Inject fails cleanly when target dir path is an existing file
        string blockingFile = Path.Combine(_tempDir, "not-a-dir");
        File.WriteAllText(blockingFile, "lock");
        Assert.False(RobloxDirectLauncher.InjectAccountCookieToLocalStorage("tok-1", localStorageDirectoryOverride: blockingFile));

        // Retry loop when Roblox is reported running once, then stopped
        string cookiesPath = Path.Combine(_tempDir, "RobloxCookies.dat");
        string origPath = Path.Combine(_tempDir, "RobloxCookies.dat.shiro-orig");
        File.WriteAllText(cookiesPath, "temp");
        File.WriteAllText(origPath, "original-backup");

        int checks = 0;
        RobloxDirectLauncher.TestCookieRestoreRetryDelayMsOverride = 5;
        RobloxDirectLauncher.TestIsAnyRobloxRunningOverride = () => Interlocked.Increment(ref checks) == 1;
        RobloxDirectLauncher.ScheduleOriginalCookieRestore(delayMs: 1);

        for (int i = 0; i < 40 && File.Exists(origPath); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(checks >= 2);
        Assert.False(File.Exists(origPath));
        Assert.Equal("original-backup", File.ReadAllText(cookiesPath));

        // DeElevation helpers
        Assert.Equal((0u, IntPtr.Zero), RobloxDirectLauncher.ResolveDeElevationEnvironmentArgs(IntPtr.Zero));
        Assert.Equal((0x00000400u, new IntPtr(1234)), RobloxDirectLauncher.ResolveDeElevationEnvironmentArgs(new IntPtr(1234)));

        using var self = Process.GetCurrentProcess();
        var disposed = new Process();
        disposed.Dispose();
        int matchedSelf = RobloxDirectLauncher.SelectExplorerPidForCurrentSession(new[] { disposed, self }, self.SessionId);
        Assert.Equal(self.Id, matchedSelf);
        Assert.Equal(0, RobloxDirectLauncher.SelectExplorerPidForCurrentSession(Array.Empty<Process>(), self.SessionId));
    }

    [Fact]
    public void RobloxWatcher_PureAndMetadataSeams_CoverClassificationPruningAndBounds()
    {
        RobloxWatcher.Stop();
        Assert.False(RobloxWatcher.TryClassifyClientCommandLine(null, out _));
        Assert.False(RobloxWatcher.TryClassifyClientCommandLine(@"\??\C:\Windows\System32\conhost.exe", out _));
        Assert.False(RobloxWatcher.TryClassifyClientCommandLine("RobloxPlayerBeta.exe --plain", out _));
        Assert.True(RobloxWatcher.TryClassifyClientCommandLine("RobloxPlayerBeta.exe -t ticket1 -b 987654321", out string tracker));
        Assert.Equal("987654321", tracker);

        Assert.False(RobloxWatcher.ShouldCloseForMemoryThreshold(false, 4096, 1024));
        Assert.False(RobloxWatcher.ShouldCloseForMemoryThreshold(true, 0, 1024));
        Assert.False(RobloxWatcher.ShouldCloseForMemoryThreshold(true, 2048, 0));
        Assert.False(RobloxWatcher.ShouldCloseForMemoryThreshold(true, 1024, 2048));
        Assert.True(RobloxWatcher.ShouldCloseForMemoryThreshold(true, 3072, 2048));

        var prev = new RobloxWatcher.RECT { Left = 10, Top = 10, Right = 810, Bottom = 610 };
        Assert.False(RobloxWatcher.TryValidateWindowBoundsChange(new RobloxWatcher.RECT { Left = 0, Top = 0, Right = 100, Bottom = 100 }, prev, out _, out _));
        Assert.False(RobloxWatcher.TryValidateWindowBoundsChange(new RobloxWatcher.RECT { Left = -10001, Top = 0, Right = 500, Bottom = 500 }, prev, out _, out _));
        Assert.False(RobloxWatcher.TryValidateWindowBoundsChange(prev, prev, out _, out _));
        Assert.True(RobloxWatcher.TryValidateWindowBoundsChange(new RobloxWatcher.RECT { Left = 50, Top = 50, Right = 950, Bottom = 750 }, prev, out int w, out int h));
        Assert.Equal(900, w);
        Assert.Equal(700, h);

        // Prune & cache validation seams
        RobloxWatcher.PruneExitedMetadataUnderLock(new[] { 1 });
        RobloxWatcher.SeedProcessCacheForTest(101, "trk-101", isGameClient: true, creationTicks: 5000);
        RobloxWatcher.SeedProcessCacheForTest(102, "trk-102", isGameClient: true, creationTicks: 6000);
        RobloxWatcher.SeedSeenWithoutMetadataForTest(103, creationTicks: 7000);
        Assert.True(RobloxWatcher.TryGetValidatedMetadataForTest(101, 5000, out string trk101));
        Assert.Equal("trk-101", trk101);
        Assert.True(RobloxWatcher.HasSeenCreationTicksForTest(103));

        RobloxWatcher.PruneExitedMetadataUnderLock(new[] { 101 });
        Assert.False(RobloxWatcher.HasSeenCreationTicksForTest(103));
        Assert.False(RobloxWatcher.TryGetValidatedMetadataForTest(102, 6000, out _));

        // Creation ticks mismatch invalidates cache
        Assert.False(RobloxWatcher.TryGetValidatedMetadataForTest(101, 9999, out _));

        RobloxWatcher.KillNative(0, "ignore-zero");
        RobloxWatcher.RemoveProcessMetadata(101);
        Assert.False(RobloxWatcher.TryRegisterSeenProcess(-1, "trk", 0));
        Assert.False(RobloxWatcher.TryRegisterSeenProcess(Environment.ProcessId, "trk", 12345));
        Assert.False(RobloxWatcher.TryRegisterSeenProcess(Environment.ProcessId, "trk", 0, _ => 0));

        RobloxWatcher.ClearAndDisposeTrackedProcesses();
    }

    [Fact]
    public void RobloxProcess_LogPolling_And_DisposalSeams_HandleEventsAndCleanupSafely()
    {
        Assert.Equal(Array.Empty<string>(), RobloxProcess.SplitCompletedLogLines(string.Empty));
        Assert.Equal(Array.Empty<string>(), RobloxProcess.SplitCompletedLogLines("\n\n"));
        Assert.Equal(new[] { "" }, RobloxProcess.SplitCompletedLogLines("\r\n\n"));
        Assert.Equal(new[] { "line1", "line2" }, RobloxProcess.SplitCompletedLogLines("line1\r\nline2\r\n"));
        Assert.True(RobloxProcess.ShouldAllowKill(ignoreExistingProcesses: false, startedBeforeWatcher: true, lastPosition: 0));
        Assert.False(RobloxProcess.ShouldAllowKill(ignoreExistingProcesses: true, startedBeforeWatcher: true, lastPosition: 0));

        long pos = 0;
        Assert.Equal(Array.Empty<string>(), RobloxProcess.ReadAppendedLines(null!, ref pos));

        int callbackCount = 0;
        EventHandler<EventArgs> handlers = (_, _) => throw new InvalidOperationException("boom");
        handlers += (_, _) => callbackCount++;
        RobloxProcess.RaiseLogFileReadSafely(handlers, null, EventArgs.Empty);
        Assert.Equal(1, callbackCount);

        using var exitedDummy = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        exitedDummy.WaitForExit();

        int connectedPid = 0;
        int disconnectedPid = 0;
        int disconnectReason = 0;
        void OnConnected(int pid) => connectedPid = pid;
        void OnDisconnected(int pid, int reason) { disconnectedPid = pid; disconnectReason = reason; }
        RobloxProcess.Connected += OnConnected;
        RobloxProcess.Disconnected += OnDisconnected;

        string logPath = Path.Combine(_tempDir, "test_Player_last.log");
        File.WriteAllText(logPath, string.Empty);

        try
        {
            using var rbxProc = new RobloxProcess(exitedDummy, "trk-dummy");
            FieldInfo logStreamField = typeof(RobloxProcess).GetField("LogStream", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo streamDisposedField = typeof(RobloxProcess).GetField("StreamDisposed", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo pidField = typeof(RobloxProcess).GetField("_pid", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo cleanedUpField = typeof(RobloxProcess).GetField("_cleanedUp", BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo readLogMethod = typeof(RobloxProcess).GetMethod("ReadLogFile", BindingFlags.Instance | BindingFlags.NonPublic)!;

            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            using (var writer = new StreamWriter(fs, Encoding.UTF8, leaveOpen: true))
            {
                writer.WriteLine("2026-11-21T00:00:00.000Z,0.1,1,Info [FLog::UgcExperienceController] UgcExperienceController: join: initialized dm 0x1234ABCD");
                writer.WriteLine("2026-11-21T00:00:01.000Z,0.1,1,Info [FLog::Output] ! Joining game '12345678-1234-1234-1234-123456789012' place 1818 at 127.0.0.1");
                writer.WriteLine("2026-11-21T00:00:02.000Z,0.1,1,Info [FLog::Network] Sending disconnect with reason: 267");
                writer.WriteLine("2026-11-21T00:00:03.000Z,0.1,1,Info [FLog::SingleSurfaceApp] setStage: (stage:LuaApp)");
                writer.Flush();
            }
            fs.Position = 0;

            pidField.SetValue(rbxProc, 424242);
            cleanedUpField.SetValue(rbxProc, 0);
            streamDisposedField.SetValue(rbxProc, false);
            logStreamField.SetValue(rbxProc, fs);

            bool prevVerify = RobloxWatcher.VerifyDataModel;
            bool prevIgnore = RobloxWatcher.IgnoreExistingProcesses;
            RobloxWatcher.VerifyDataModel = true;
            RobloxWatcher.IgnoreExistingProcesses = false;
            try
            {
                readLogMethod.Invoke(rbxProc, new object?[] { null, EventArgs.Empty });
            }
            finally
            {
                RobloxWatcher.VerifyDataModel = prevVerify;
                RobloxWatcher.IgnoreExistingProcesses = prevIgnore;
            }

            Assert.Equal(424242, connectedPid);
            Assert.Equal(424242, disconnectedPid);
            Assert.Equal(267, disconnectReason);

            rbxProc.Dispose();
            rbxProc.Dispose();
        }
        finally
        {
            RobloxProcess.Connected -= OnConnected;
            RobloxProcess.Disconnected -= OnDisconnected;
        }
    }
}
