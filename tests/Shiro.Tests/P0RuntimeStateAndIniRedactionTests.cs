#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class P0RuntimeStateAndIniRedactionTests
    {
        private static void CleanupTempDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch (IOException) { }
            }
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }

        [Fact]
        public void AccountRuntimeStateStore_FilePathOverride_DoesNotClobberDefaultCacheOrDebounceState()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-P0-RuntimeOverride-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                AccountRuntimeStateStore.ResetInMemoryCacheForTests();

                string fileB = Path.Combine(tempDir, "overrideB.json");
                string fileC = Path.Combine(tempDir, "overrideC.json");

                var defaultAcc1 = new Account { UserID = 9001, Username = "DefaultUserOne" };
                DateTime defaultTs1 = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
                AccountRuntimeStateStore.RecordLastUse(defaultAcc1, defaultTs1);
                AccountRuntimeStateStore.RecordWindowBounds(defaultAcc1, 10, 20, 640, 480);

                var overrideAcc = new Account { UserID = 9002, Username = "OverrideUserB" };
                DateTime overrideTs = new DateTime(2026, 6, 15, 12, 30, 0, DateTimeKind.Utc);
                AccountRuntimeStateStore.RecordLastUse(overrideAcc, overrideTs, filePathOverride: fileB);
                Assert.True(AccountRuntimeStateStore.Flush(filePathOverride: fileB));

                var overrideAccC = new Account { UserID = 9004, Username = "OverrideUserC" };
                AccountRuntimeStateStore.RecordWindowBounds(overrideAccC, 300, 400, 1024, 768, filePathOverride: fileC);
                Assert.True(AccountRuntimeStateStore.Flush(filePathOverride: fileC));

                // Default in-memory cache must still hold DefaultUserOne and never OverrideUserB/C.
                var preFlushCheckDefault = new Account { UserID = 9001, Username = "DefaultUserOne" };
                var preFlushCheckOverride = new Account { UserID = 9002, Username = "OverrideUserB" };
                int preFlushHydrated = AccountRuntimeStateStore.HydrateAccounts(new[] { preFlushCheckDefault, preFlushCheckOverride });
                Assert.Equal(1, preFlushHydrated);
                Assert.Equal(defaultTs1, preFlushCheckDefault.LastUse);
                Assert.Equal("10", preFlushCheckDefault.GetField("Window_Position_X"));
                Assert.Equal(DateTime.MinValue, preFlushCheckOverride.LastUse);

                // Subsequent default RecordLastUse + Flush must write to default file with DefaultUserOne intact.
                var defaultAcc2 = new Account { UserID = 9003, Username = "DefaultUserTwo" };
                DateTime defaultTs2 = new DateTime(2026, 5, 2, 11, 0, 0, DateTimeKind.Utc);
                AccountRuntimeStateStore.RecordLastUse(defaultAcc2, defaultTs2);
                Assert.True(AccountRuntimeStateStore.Flush());

                Assert.True(File.Exists(StoragePaths.AccountRuntimeStateFile));
                Assert.True(File.Exists(fileB));
                Assert.True(File.Exists(fileC));

                // Reset cache and verify all three files from disk independently.
                AccountRuntimeStateStore.ResetInMemoryCacheForTests();

                var diskDefault1 = new Account { UserID = 9001, Username = "DefaultUserOne" };
                var diskDefault2 = new Account { UserID = 9003, Username = "DefaultUserTwo" };
                var diskOverrideLeak = new Account { UserID = 9002, Username = "OverrideUserB" };
                int diskHydrated = AccountRuntimeStateStore.HydrateAccounts(new[] { diskDefault1, diskDefault2, diskOverrideLeak });
                Assert.Equal(2, diskHydrated);
                Assert.Equal(defaultTs1, diskDefault1.LastUse);
                Assert.Equal("20", diskDefault1.GetField("Window_Position_Y"));
                Assert.Equal(defaultTs2, diskDefault2.LastUse);
                Assert.Equal(DateTime.MinValue, diskOverrideLeak.LastUse);

                var diskB = new Account { UserID = 9002, Username = "OverrideUserB" };
                var diskBLeak = new Account { UserID = 9001, Username = "DefaultUserOne" };
                Assert.Equal(1, AccountRuntimeStateStore.HydrateAccounts(new[] { diskB, diskBLeak }, filePathOverride: fileB));
                Assert.Equal(overrideTs, diskB.LastUse);
                Assert.Equal(DateTime.MinValue, diskBLeak.LastUse);

                var diskC = new Account { UserID = 9004, Username = "OverrideUserC" };
                Assert.Equal(1, AccountRuntimeStateStore.HydrateAccounts(new[] { diskC }, filePathOverride: fileC));
                Assert.Equal("300", diskC.GetField("Window_Position_X"));
                Assert.Equal("768", diskC.GetField("Window_Height"));
            }
            finally
            {
                AccountRuntimeStateStore.ResetInMemoryCacheForTests();
                CleanupTempDir(tempDir);
            }
        }

        [Fact]
        public async Task AccountRuntimeStateStore_ConcurrentRecordLastUseAndFlush_PreservesLatestTimestampWithoutStaleOverwrite()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-P0-RuntimeRace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                AccountRuntimeStateStore.ResetInMemoryCacheForTests();

                string overrideFile = Path.Combine(tempDir, "concurrent_override.json");
                DateTime baseUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
                long seq = 0;

                // Stress filePathOverride concurrent RecordLastUse + Flush calls across multiple rounds.
                for (int round = 0; round < 10; round++)
                {
                    var acc = new Account { UserID = 88001, Username = "ConcurrentOverrideUser" };
                    Task[] workers = new Task[12];
                    for (int w = 0; w < workers.Length; w++)
                    {
                        workers[w] = Task.Run(() =>
                        {
                            for (int i = 0; i < 20; i++)
                            {
                                long next = Interlocked.Increment(ref seq);
                                AccountRuntimeStateStore.RecordLastUse(acc, baseUtc.AddSeconds(next), filePathOverride: overrideFile);
                                AccountRuntimeStateStore.Flush(filePathOverride: overrideFile);
                            }
                        });
                    }

                    await Task.WhenAll(workers);

                    DateTime expectedMax = baseUtc.AddSeconds(Volatile.Read(ref seq));
                    byte[] raw = File.ReadAllBytes(overrideFile);
                    var doc = JsonSerializer.Deserialize(raw, ShiroAppJson.Default.RuntimeStateDocument);
                    Assert.NotNull(doc);
                    Assert.True(doc!.Accounts.TryGetValue("uid:88001", out var entry));
                    Assert.NotNull(entry);
                    Assert.Equal(expectedMax, entry!.LastUse);
                }

                // Stress default path concurrent RecordLastUse + Flush calls as well.
                long defaultSeq = 0;
                for (int round = 0; round < 10; round++)
                {
                    var acc = new Account { UserID = 88002, Username = "ConcurrentDefaultUser" };
                    Task[] workers = new Task[12];
                    for (int w = 0; w < workers.Length; w++)
                    {
                        workers[w] = Task.Run(() =>
                        {
                            for (int i = 0; i < 20; i++)
                            {
                                long next = Interlocked.Increment(ref defaultSeq);
                                AccountRuntimeStateStore.RecordLastUse(acc, baseUtc.AddSeconds(next));
                                AccountRuntimeStateStore.Flush();
                            }
                        });
                    }

                    await Task.WhenAll(workers);
                    AccountRuntimeStateStore.Flush();

                    DateTime expectedMax = baseUtc.AddSeconds(Volatile.Read(ref defaultSeq));
                    byte[] raw = File.ReadAllBytes(StoragePaths.AccountRuntimeStateFile);
                    var doc = JsonSerializer.Deserialize(raw, ShiroAppJson.Default.RuntimeStateDocument);
                    Assert.NotNull(doc);
                    Assert.True(doc!.Accounts.TryGetValue("uid:88002", out var entry));
                    Assert.NotNull(entry);
                    Assert.Equal(expectedMax, entry!.LastUse);
                }
            }
            finally
            {
                AccountRuntimeStateStore.ResetInMemoryCacheForTests();
                CleanupTempDir(tempDir);
            }
        }

        [Fact]
        public void AccountRuntimeStateStore_Flush_WhenWriteFails_PreservesDirtyFlagUntilSubsequentFlushSucceeds()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Shiro-P0-FlushRetry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using var dirScope = StoragePaths.OverrideDataDirectoryForTesting(tempDir);
                AccountRuntimeStateStore.ResetInMemoryCacheForTests();

                var acc = new Account { UserID = 77001, Username = "RetryUser" };
                DateTime firstTs = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
                DateTime secondTs = new DateTime(2026, 8, 1, 11, 0, 0, DateTimeKind.Utc);

                AccountRuntimeStateStore.RecordLastUse(acc, firstTs);
                Assert.True(AccountRuntimeStateStore.Flush());

                using (var exclusiveLock = new FileStream(StoragePaths.AccountRuntimeStateFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    AccountRuntimeStateStore.RecordLastUse(acc, secondTs);
                    Assert.False(AccountRuntimeStateStore.Flush());
                }

                // Dirty state must still be preserved so the next Flush persists secondTs.
                Assert.True(AccountRuntimeStateStore.Flush());
                Assert.False(AccountRuntimeStateStore.Flush());

                AccountRuntimeStateStore.ResetInMemoryCacheForTests();
                var reloaded = new Account { UserID = 77001, Username = "RetryUser" };
                Assert.Equal(1, AccountRuntimeStateStore.HydrateAccounts(new[] { reloaded }));
                Assert.Equal(secondTs, reloaded.LastUse);
            }
            finally
            {
                AccountRuntimeStateStore.ResetInMemoryCacheForTests();
                CleanupTempDir(tempDir);
            }
        }
    }
}
