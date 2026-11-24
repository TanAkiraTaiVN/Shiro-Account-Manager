#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    public sealed class LaunchScheduleStoreTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _storePath;

        public LaunchScheduleStoreTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "Shiro_ScheduleStoreTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _storePath = Path.Combine(_tempDir, "LaunchSchedules.json");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup
            }
        }

        [Fact]
        public void Load_ReturnsEmptyDocument_WhenFileMissing()
        {
            var store = new LaunchScheduleStore(() => _storePath);

            LaunchScheduleDocument doc = store.Load();

            Assert.Equal(1, doc.SchemaVersion);
            Assert.Empty(doc.Schedules);
            Assert.Empty(doc.History);
        }

        [Fact]
        public void SaveAndLoad_RoundTripsAllScheduleFieldsAndHistory_ViaSourceGenJson()
        {
            var store = new LaunchScheduleStore(() => _storePath);
            var createdAt = new DateTimeOffset(2026, 11, 20, 8, 15, 30, TimeSpan.Zero);
            var updatedAt = new DateTimeOffset(2026, 11, 24, 14, 45, 0, TimeSpan.Zero);
            var runAt = new DateTimeOffset(2026, 11, 24, 12, 0, 5, TimeSpan.Zero);

            var schedule = new LaunchSchedule
            {
                Id = "sched-001",
                Name = "Overnight Farm",
                IsEnabled = false,
                ColorHex = "#4ADE80",
                Recurrence = ScheduleRecurrence.Weekly,
                DaysOfWeek = ScheduleDaysOfWeek.Weekdays | ScheduleDaysOfWeek.Sunday,
                OneTimeDate = "2026-12-01",
                StartTime = "22:30",
                HasEndTime = false,
                EndTime = "06:15",
                TargetMode = ScheduleTargetMode.Group,
                TargetGroup = "Farm-A",
                TargetAccounts = new List<ScheduleAccountRef>
                {
                    new() { UserId = 1001, Username = "AlphaFarmer" },
                    new() { UserId = 2002, Username = "BetaFarmer" }
                },
                LaunchMode = ScheduleLaunchMode.OpenHome,
                PlaceId = 2753915549,
                JobId = "VIP:abc-123",
                CloseOtherTabsBeforeLaunch = true,
                HandoffDelaySeconds = 7,
                PostLaunchArrange = ScheduleArrangeMode.Grid3x2,
                CatchUpIfActiveOnStartup = true,
                SendWebhookNotification = true,
                LastTriggeredStartKey = "2026-11-23",
                LastTriggeredEndKey = "2026-11-24",
                CreatedAtUtc = createdAt,
                UpdatedAtUtc = updatedAt
            };

            LaunchSchedule cloned = schedule.Clone();
            Assert.Equal(schedule.Id, cloned.Id);
            Assert.Equal(2, cloned.TargetAccounts.Count);
            cloned.TargetAccounts[0].Username = "Mutated";
            Assert.Equal("AlphaFarmer", schedule.TargetAccounts[0].Username);

            var historyEntry = new ScheduleRunEntry
            {
                Id = "run-001",
                ScheduleId = "sched-001",
                ScheduleName = "Overnight Farm",
                TimestampUtc = runAt,
                Phase = ScheduleRunPhase.CatchUpOpen,
                Outcome = ScheduleRunOutcome.Partial,
                TargetCount = 3,
                SucceededCount = 2,
                SkippedCount = 1,
                Summary = "Opened 2/3 accounts; skipped 1 banned"
            };

            var doc = new LaunchScheduleDocument
            {
                SchemaVersion = 1,
                Schedules = new List<LaunchSchedule> { schedule },
                History = new List<ScheduleRunEntry> { historyEntry }
            };

            // Verify source-gen JSON context handles document directly as well
            string rawJson = JsonSerializer.Serialize(doc, ShiroAppJson.Default.LaunchScheduleDocument);
            Assert.Contains("Overnight Farm", rawJson, StringComparison.Ordinal);

            store.Save(doc);
            store.Flush();

            LaunchScheduleDocument loaded = store.Load();
            Assert.Equal(1, loaded.SchemaVersion);
            LaunchSchedule s = Assert.Single(loaded.Schedules);
            Assert.Equal("sched-001", s.Id);
            Assert.Equal("Overnight Farm", s.Name);
            Assert.False(s.IsEnabled);
            Assert.Equal("#4ADE80", s.ColorHex);
            Assert.Equal(ScheduleRecurrence.Weekly, s.Recurrence);
            Assert.Equal(ScheduleDaysOfWeek.Weekdays | ScheduleDaysOfWeek.Sunday, s.DaysOfWeek);
            Assert.Equal("2026-12-01", s.OneTimeDate);
            Assert.Equal("22:30", s.StartTime);
            Assert.False(s.HasEndTime);
            Assert.Equal("06:15", s.EndTime);
            Assert.Equal(ScheduleTargetMode.Group, s.TargetMode);
            Assert.Equal("Farm-A", s.TargetGroup);
            Assert.Equal(2, s.TargetAccounts.Count);
            Assert.Equal(1001, s.TargetAccounts[0].UserId);
            Assert.Equal("AlphaFarmer", s.TargetAccounts[0].Username);
            Assert.Equal(2002, s.TargetAccounts[1].UserId);
            Assert.Equal("BetaFarmer", s.TargetAccounts[1].Username);
            Assert.Equal(ScheduleLaunchMode.OpenHome, s.LaunchMode);
            Assert.Equal(2753915549, s.PlaceId);
            Assert.Equal("VIP:abc-123", s.JobId);
            Assert.True(s.CloseOtherTabsBeforeLaunch);
            Assert.Equal(7, s.HandoffDelaySeconds);
            Assert.Equal(ScheduleArrangeMode.Grid3x2, s.PostLaunchArrange);
            Assert.True(s.CatchUpIfActiveOnStartup);
            Assert.True(s.SendWebhookNotification);
            Assert.Equal("2026-11-23", s.LastTriggeredStartKey);
            Assert.Equal("2026-11-24", s.LastTriggeredEndKey);
            Assert.Equal(createdAt, s.CreatedAtUtc);
            Assert.Equal(updatedAt, s.UpdatedAtUtc);

            ScheduleRunEntry h = Assert.Single(loaded.History);
            Assert.Equal("run-001", h.Id);
            Assert.Equal("sched-001", h.ScheduleId);
            Assert.Equal("Overnight Farm", h.ScheduleName);
            Assert.Equal(runAt, h.TimestampUtc);
            Assert.Equal(ScheduleRunPhase.CatchUpOpen, h.Phase);
            Assert.Equal(ScheduleRunOutcome.Partial, h.Outcome);
            Assert.Equal(3, h.TargetCount);
            Assert.Equal(2, h.SucceededCount);
            Assert.Equal(1, h.SkippedCount);
            Assert.Equal("Opened 2/3 accounts; skipped 1 banned", h.Summary);
        }

        [Fact]
        public void Save_TrimsHistoryToMax50Entries_NewestFirst()
        {
            Assert.Equal(50, LaunchScheduleStore.MaxHistoryEntries);
            var store = new LaunchScheduleStore(() => _storePath);
            var baseUtc = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);

            var doc = new LaunchScheduleDocument();
            for (int i = 0; i < 65; i++)
            {
                doc.History.Add(new ScheduleRunEntry
                {
                    Id = $"run-{i:D2}",
                    ScheduleId = "sched-1",
                    ScheduleName = "Shift",
                    TimestampUtc = baseUtc.AddMinutes(i),
                    Phase = ScheduleRunPhase.Open,
                    Outcome = ScheduleRunOutcome.Success,
                    TargetCount = 1,
                    SucceededCount = 1,
                    SkippedCount = 0,
                    Summary = $"Run {i}"
                });
            }

            store.Save(doc);
            LaunchScheduleDocument loaded = store.Load();

            Assert.Equal(50, loaded.History.Count);
            Assert.Equal("run-64", loaded.History[0].Id);
            Assert.Equal("run-63", loaded.History[1].Id);
            Assert.Equal("run-15", loaded.History[49].Id);
        }

        [Fact]
        public void Load_FallsBackToBackup_WhenPrimaryFileCorrupted()
        {
            var store = new LaunchScheduleStore(() => _storePath);

            var docV1 = new LaunchScheduleDocument
            {
                Schedules = new List<LaunchSchedule>
                {
                    new() { Id = "s1", Name = "Morning Shift", StartTime = "08:00", EndTime = "12:00" }
                }
            };
            store.Save(docV1);

            var docV2 = new LaunchScheduleDocument
            {
                Schedules = new List<LaunchSchedule>
                {
                    new() { Id = "s2", Name = "Afternoon Shift", StartTime = "12:00", EndTime = "18:00" }
                }
            };
            store.Save(docV2);

            Assert.True(File.Exists(_storePath + ".backup"));
            File.WriteAllText(_storePath, "{ invalid json ::: truncated");

            LaunchScheduleDocument recovered = store.Load();
            LaunchSchedule item = Assert.Single(recovered.Schedules);
            Assert.Equal("s1", item.Id);
            Assert.Equal("Morning Shift", item.Name);
        }

        [Fact]
        public void Save_WritesAtomically_LeavingNoTempFile()
        {
            var store = new LaunchScheduleStore(() => _storePath);
            var doc = new LaunchScheduleDocument
            {
                Schedules = new List<LaunchSchedule>
                {
                    new() { Id = "s1", Name = "Atomic Shift" }
                }
            };

            store.Save(doc);
            store.Save(new LaunchScheduleDocument
            {
                Schedules = new List<LaunchSchedule>
                {
                    new() { Id = "s1", Name = "Atomic Shift v2" }
                }
            });

            string[] files = Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories);
            Assert.DoesNotContain(files, f => f.Contains(".tmp", StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(_storePath));

            LaunchScheduleDocument loaded = store.Load();
            Assert.Equal("Atomic Shift v2", Assert.Single(loaded.Schedules).Name);
        }
    }
}
