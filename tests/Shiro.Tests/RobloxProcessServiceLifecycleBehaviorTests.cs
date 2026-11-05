#nullable enable
using System;
using System.Collections.Generic;
using Shiro.Classes;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class RobloxProcessServiceLifecycleBehaviorTests
    {
        [Fact]
        public void KillAllRobloxProcesses_ExecutesSafelyAndReturnsCount()
        {
            var ex = Record.Exception(() =>
            {
                int count = RobloxProcessService.GetTotalRunningRobloxCount() == 0 ? RobloxProcessService.KillAllRobloxProcesses() : 0;
                Assert.InRange(count, 0, int.MaxValue);
            });
            Assert.Null(ex);
        }

        [Fact]
        public void CloseAccountRobloxProcess_WithNullAccount_ReturnsFalse()
        {
            bool closed = RobloxProcessService.CloseAccountRobloxProcess(null!);
            Assert.False(closed);
        }

        [Fact]
        public void CloseAccountRobloxProcess_WithNonExistentPid_ResetsPidAndReturnsFalse()
        {
            var account = new Account
            {
                Username = "DeadPidUser",
                LastLaunchedProcessId = 999999
            };

            bool closed = RobloxProcessService.CloseAccountRobloxProcess(account);

            Assert.False(closed);
            Assert.Equal(0, account.LastLaunchedProcessId);
        }

        [Fact]
        public void CloseAccountRobloxProcess_WithEmptyAccountState_ReturnsFalse()
        {
            var account = new Account
            {
                Username = "NoProcessUser",
                LastLaunchedProcessId = 0,
                BrowserTrackerID = ""
            };

            bool closed = RobloxProcessService.CloseAccountRobloxProcess(account);
            Assert.False(closed);
        }

        [Fact]
        public void CloseAccountRobloxProcesses_NullOrEmptyList_ReturnsZero()
        {
            int countNull = RobloxProcessService.CloseAccountRobloxProcesses(null!);
            Assert.Equal(0, countNull);

            int countEmpty = RobloxProcessService.CloseAccountRobloxProcesses(Array.Empty<Account>());
            Assert.Equal(0, countEmpty);
        }

        [Fact]
        public void CloseAccountRobloxProcesses_SingleAccount_DelegatesCorrectly()
        {
            var account = new Account
            {
                Username = "SingleProcUser",
                LastLaunchedProcessId = 999998
            };

            int closedCount = RobloxProcessService.CloseAccountRobloxProcesses(new[] { account });

            Assert.Equal(0, closedCount);
            Assert.Equal(0, account.LastLaunchedProcessId);
        }

        [Fact]
        public void CloseAccountRobloxProcesses_MultipleAccounts_HandlesDeduplicationAndCleanup()
        {
            var acc1 = new Account { Username = "MultiProc1", LastLaunchedProcessId = 999991 };
            var acc2 = new Account { Username = "MultiProc2", LastLaunchedProcessId = 999992 };
            var acc3 = new Account { Username = "MultiProc3", LastLaunchedProcessId = 0 };

            int closed = RobloxProcessService.CloseAccountRobloxProcesses(new[] { acc1, acc2, acc3 });

            Assert.Equal(0, closed);
            Assert.Equal(0, acc1.LastLaunchedProcessId);
            Assert.Equal(0, acc2.LastLaunchedProcessId);
        }

        [Fact]
        public void IsProcessAlive_ValidatesPidBounds()
        {
            Assert.False(RobloxProcessService.IsProcessAlive(0));
            Assert.False(RobloxProcessService.IsProcessAlive(-1));
            Assert.False(RobloxProcessService.IsProcessAlive(-999));
            Assert.False(RobloxProcessService.IsProcessAlive(99999999));
        }

        [Fact]
        public void CleanZombieProcesses_ExecutesSafely()
        {
            var exception = Record.Exception(() => RobloxProcessService.CleanZombieProcesses());
            Assert.Null(exception);
        }

        [Fact]
        public void ClearRobloxClientSession_ExecutesSafely()
        {
            var exception = Record.Exception(() => RobloxProcessService.ClearRobloxClientSession(killProcesses: false));
            Assert.Null(exception);
        }

        [Fact]
        public void IsRobloxInstallerRunning_ExecutesSafely()
        {
            RobloxProcessService.InvalidateSnapshotCache();
            bool running = RobloxProcessService.IsRobloxInstallerRunning();
            Assert.False(running);
        }

        [Fact]
        public void ProcessExtensions_GetCommandLine_HandlesEdgeCasesAndCache()
        {
            // Null process
            Assert.Null(ProcessExtensions.GetCommandLine(null));

            // Negative and zero PID
            Assert.Null(ProcessExtensions.GetCommandLineByPid(0));
            Assert.Null(ProcessExtensions.GetCommandLineByPid(-1));

            // Current process lookup (exercises NtQueryInformationProcess and cache)
            using var currentProc = System.Diagnostics.Process.GetCurrentProcess();
            string? cmd1 = currentProc.GetCommandLine();
            string? cmd2 = ProcessExtensions.GetCommandLineByPid(currentProc.Id);

            Assert.Equal(cmd1, cmd2);
        }

        [Fact]
        public void CloseAccountRobloxProcess_WithBrowserTrackerId_ScansPidsAndReturnsFalse()
        {
            var account = new Account
            {
                Username = "TrackerUser",
                LastLaunchedProcessId = 0,
                BrowserTrackerID = "998877665544"
            };

            bool closed = RobloxProcessService.CloseAccountRobloxProcess(account);
            Assert.False(closed);
        }

        [Fact]
        public void CloseAccountRobloxProcesses_WithBrowserTrackerIds_ScansPidsAndReturnsZero()
        {
            var acc1 = new Account { Username = "Track1", LastLaunchedProcessId = 0, BrowserTrackerID = "11111" };
            var acc2 = new Account { Username = "Track2", LastLaunchedProcessId = 0, BrowserTrackerID = "22222" };

            int closed = RobloxProcessService.CloseAccountRobloxProcesses(new[] { acc1, acc2 });
            Assert.Equal(0, closed);
        }

        [Fact]
        public void ClearRobloxClientSession_WithoutKillingProcesses_ReturnsTrue()
        {
            bool cleared = RobloxProcessService.ClearRobloxClientSession(killProcesses: false);
            Assert.True(cleared);
        }

        [Fact]
        public void IsProcessAlive_ValidatesCurrentAndInvalidPids()
        {
            Assert.False(RobloxProcessService.IsProcessAlive(0));
            Assert.False(RobloxProcessService.IsProcessAlive(-1));
            Assert.True(RobloxProcessService.IsProcessAlive(Environment.ProcessId));
            Assert.False(RobloxProcessService.IsProcessAlive(9999999));
        }

        [Fact]
        public void ProcessDiscovery_MetricsReturnConsistentValues()
        {
            RobloxProcessService.InvalidateSnapshotCache();

            int clientCount = RobloxProcessService.GetRunningRobloxCount();
            int totalCount = RobloxProcessService.GetTotalRunningRobloxCount();
            bool anyRunning = RobloxProcessService.IsAnyRobloxRunning();
            var processes = RobloxProcessService.GetRunningRobloxProcesses();

            Assert.InRange(clientCount, 0, int.MaxValue);
            Assert.InRange(totalCount, 0, int.MaxValue);
            Assert.Equal(clientCount > 0, anyRunning);
            Assert.InRange(processes.Count, 0, int.MaxValue);
        }

        [Fact]
        public void CloseAccountRobloxProcess_And_Batch_WithTrackerAndGhost_CoverBranches()
        {
            var prevPidsOverride = RobloxProcessService.TestGetRunningRobloxPidsOverride;
            var prevCmdOverride = ProcessExtensions.TestCommandLineOverride;

            using var shortProc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c ping -n 5 127.0.0.1 > nul",
                CreateNoWindow = true,
                UseShellExecute = false
            });

            Assert.NotNull(shortProc);
            int pid = shortProc.Id;

            try
            {
                // 1. Single close by BrowserTrackerID
                RobloxProcessService.TestGetRunningRobloxPidsOverride = () => new[] { pid };
                ProcessExtensions.TestCommandLineOverride = p => p == pid ? "RobloxPlayerBeta.exe -b 98765432101" : null;

                var acc1 = new Account
                {
                    Username = "CloseByTrackerUser",
                    LastLaunchedProcessId = 0,
                    BrowserTrackerID = "98765432101"
                };

                bool closed = RobloxProcessService.CloseAccountRobloxProcess(acc1);
                Assert.True(closed);
                Assert.Equal(0, acc1.LastLaunchedProcessId);

                // 2. Batch close by BrowserTrackerID and deduplication
                using var shortProc2 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c ping -n 5 127.0.0.1 > nul",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                Assert.NotNull(shortProc2);
                int pid2 = shortProc2.Id;

                RobloxProcessService.TestGetRunningRobloxPidsOverride = () => new[] { pid2 };
                ProcessExtensions.TestCommandLineOverride = p => p == pid2 ? "RobloxPlayerBeta.exe -b 98765432102" : null;

                var accBatch1 = new Account { Username = "BatchUser1", LastLaunchedProcessId = 0, BrowserTrackerID = "98765432102" };
                var accBatchDuplicate = new Account { Username = "BatchUser2", LastLaunchedProcessId = 0, BrowserTrackerID = "98765432102" };
                var accBatchUnmatched = new Account { Username = "BatchUser3", LastLaunchedProcessId = 0, BrowserTrackerID = "98765432103" };

                int batchClosed = RobloxProcessService.CloseAccountRobloxProcesses(new[] { accBatch1, accBatchDuplicate, accBatchUnmatched });
                Assert.Equal(1, batchClosed);

                // 3. CleanZombieProcesses with --launch-to-tray ghost process
                using var shortProc3 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c ping -n 5 127.0.0.1 > nul",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                Assert.NotNull(shortProc3);
                int pid3 = shortProc3.Id;

                RobloxProcessService.TestGetRunningRobloxPidsOverride = () => new[] { pid3 };
                ProcessExtensions.TestCommandLineOverride = p => p == pid3 ? "RobloxPlayerBeta.exe --launch-to-tray" : null;

                int cleaned = RobloxProcessService.CleanZombieProcesses();
                Assert.True(cleaned >= 1);
            }
            finally
            {
                RobloxProcessService.TestGetRunningRobloxPidsOverride = prevPidsOverride;
                ProcessExtensions.TestCommandLineOverride = prevCmdOverride;
                try { if (!shortProc.HasExited) shortProc.Kill(); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void ClearRobloxClientSession_CleansLocalStorageFiles_WhenPresent()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string localStorageDir = System.IO.Path.Combine(localAppData, "Roblox", "LocalStorage");
            System.IO.Directory.CreateDirectory(localStorageDir);

            string cookiesDat = System.IO.Path.Combine(localStorageDir, "RobloxCookies.dat");
            string appStorage = System.IO.Path.Combine(localStorageDir, "appStorage.json");
            string rbxStorageDb = System.IO.Path.Combine(localAppData, "Roblox", "rbx-storage.db");

            try
            {
                System.IO.File.WriteAllText(cookiesDat, "dummy-cookie");
                System.IO.File.WriteAllText(appStorage, "dummy-app-storage");
                System.IO.File.WriteAllText(rbxStorageDb, "dummy-rbx-db");

                bool cleared = RobloxProcessService.ClearRobloxClientSession(killProcesses: false);
                Assert.True(cleared);
                Assert.False(System.IO.File.Exists(cookiesDat));
                Assert.False(System.IO.File.Exists(appStorage));
                Assert.False(System.IO.File.Exists(rbxStorageDb));
            }
            finally
            {
                try { if (System.IO.File.Exists(cookiesDat)) System.IO.File.Delete(cookiesDat); } catch { /* ignore */ }
                try { if (System.IO.File.Exists(appStorage)) System.IO.File.Delete(appStorage); } catch { /* ignore */ }
                try { if (System.IO.File.Exists(rbxStorageDb)) System.IO.File.Delete(rbxStorageDb); } catch { /* ignore */ }
            }
        }

        [Fact]
        public void RobloxProcessService_LiveProcesses_LifecycleAndKillAll_CoverBranches()
        {
            string pingPath = System.IO.Path.Combine(Environment.SystemDirectory, "ping.exe");
            if (!System.IO.File.Exists(pingPath)) return;

            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ShiroBetaProc_{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(tempDir);
            string betaExe = System.IO.Path.Combine(tempDir, "RobloxPlayerBeta.exe");
            string crashExe = System.IO.Path.Combine(tempDir, "RobloxCrashHandler.exe");
            System.IO.File.Copy(pingPath, betaExe, true);
            System.IO.File.Copy(pingPath, crashExe, true);

            try
            {
                // 1. Single CloseAccountRobloxProcess with real RobloxPlayerBeta name
                using var pBeta1 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = betaExe,
                    Arguments = "-n 5 127.0.0.1",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                Assert.NotNull(pBeta1);

                var accBeta = new Account
                {
                    Username = "BetaProcUser",
                    LastLaunchedProcessId = pBeta1.Id,
                    BrowserTrackerID = "12345678"
                };

                ProcessExtensions.TestCommandLineOverride = pid => pid == pBeta1.Id ? $"{betaExe} -b 12345678" : null;
                bool closed = RobloxProcessService.CloseAccountRobloxProcess(accBeta);
                Assert.True(closed);
                Assert.Equal(0, accBeta.LastLaunchedProcessId);

                // 2. Batch CloseAccountRobloxProcesses with real RobloxPlayerBeta name
                using var pBeta2 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = betaExe,
                    Arguments = "-n 5 127.0.0.1",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                Assert.NotNull(pBeta2);

                ProcessExtensions.TestCommandLineOverride = pid => pid == pBeta2.Id ? $"{betaExe} -b 88776655" : null;
                var accTrackerMismatch = new Account { Username = "", LastLaunchedProcessId = 0, BrowserTrackerID = "mismatch_9999" };
                bool closedMismatch = RobloxProcessService.CloseAccountRobloxProcess(accTrackerMismatch);
                Assert.False(closedMismatch);

                var accBatch1 = new Account { Username = "BatchBeta1", LastLaunchedProcessId = pBeta2.Id, BrowserTrackerID = "88776655" };
                var accBatch2 = new Account { Username = "BatchBeta2", LastLaunchedProcessId = pBeta2.Id, BrowserTrackerID = "88776655" }; // duplicate PID

                ProcessExtensions.TestCommandLineOverride = pid => pid == pBeta2.Id ? $"{betaExe} -b 88776655" : null;
                int batchClosed = RobloxProcessService.CloseAccountRobloxProcesses(new[] { accBatch1, accBatch2 });
                Assert.Equal(1, batchClosed);
                Assert.Equal(0, accBatch1.LastLaunchedProcessId);
                Assert.True(accBatch2.LastLaunchedProcessId > 0);

                // 3. KillAllRobloxProcesses with real RobloxPlayerBeta running
                using var pBeta3 = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = betaExe,
                    Arguments = "-n 5 127.0.0.1",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                Assert.NotNull(pBeta3);

                int killed = RobloxProcessService.KillAllRobloxProcesses();
                Assert.True(killed >= 1);

                // 4. CleanZombieProcesses with real RobloxCrashHandler
                using var pCrash = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = crashExe,
                    Arguments = "-n 5 127.0.0.1",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                Assert.NotNull(pCrash);

                int cleanedCrash = RobloxProcessService.CleanZombieProcesses();
                Assert.True(cleanedCrash >= 1);
            }
            finally
            {
                ProcessExtensions.TestCommandLineOverride = null;
                try { System.IO.Directory.Delete(tempDir, true); } catch { /* ignore */ }
            }
        }
    }
}
