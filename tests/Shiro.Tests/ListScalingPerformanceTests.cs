using System;
using System.Collections.Generic;
using System.Diagnostics;
using Shiro.Services;
using Shiro.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Shiro.Tests
{
    /// <summary>
    /// Scaling guard for the account list: load / search / group / sort with 1000 accounts.
    /// Budgets are deliberately generous (CI noise tolerant); timings are written to test output.
    /// </summary>
    [Collection("CookieGuardSerial")]
    public class ListScalingPerformanceTests
    {
        private const int AccountCount = 1000;
        private readonly ITestOutputHelper _output;

        public ListScalingPerformanceTests(ITestOutputHelper output) => _output = output;

        private static List<Account> BuildAccounts(int count)
        {
            var list = new List<Account>(count);
            for (int i = 1; i <= count; i++)
            {
                list.Add(new Account
                {
                    UserID = 100000 + i,
                    Username = $"Farmer_{i:D4}",
                    Group = (i % 5) switch { 0 => "Alpha", 1 => "Beta", 2 => "Gamma", 3 => "Delta", _ => "Default" },
                    Valid = true
                });
            }
            return list;
        }

        private double Measure(string label, Action action)
        {
            action(); // warm-up (JIT)
            var sw = Stopwatch.StartNew();
            const int iterations = 5;
            for (int i = 0; i < iterations; i++) action();
            sw.Stop();
            double avg = sw.Elapsed.TotalMilliseconds / iterations;
            _output.WriteLine($"{label}: {avg:F2} ms avg ({AccountCount} accounts)");
            return avg;
        }

        [Fact]
        public void LoadAccounts_1000_CompletesWithinBudget()
        {
            var accounts = BuildAccounts(AccountCount);
            var vm = new MainViewModel();
            double ms = Measure("LoadAccounts", () => vm.LoadAccounts(accounts));
            Assert.Equal(AccountCount, vm.FilteredAccounts.Count);
            Assert.True(ms < 1500, $"LoadAccounts took {ms:F1} ms (budget 1500 ms)");
        }

        [Fact]
        public void Search_1000_CompletesWithinBudget()
        {
            var vm = new MainViewModel();
            vm.LoadAccounts(BuildAccounts(AccountCount));
            // SearchText is debounced (120 ms) so the filter pass is timed via ApplyFilter directly.
            vm.SearchText = "Farmer_09";
            double ms = Measure("Search ApplyFilter", () => vm.ApplyFilter());
            Assert.Equal(100, vm.FilteredAccounts.Count);
            Assert.True(ms < 500, $"Search took {ms:F1} ms (budget 500 ms)");
        }

        [Fact]
        public void GroupFilter_1000_CompletesWithinBudget()
        {
            var vm = new MainViewModel();
            vm.LoadAccounts(BuildAccounts(AccountCount));
            bool alpha = true;
            double ms = Measure("Group filter toggle", () =>
            {
                vm.SelectedGroup = alpha ? "Alpha" : "Beta";
                alpha = !alpha;
            });
            vm.SelectedGroup = "Alpha";
            Assert.Equal(200, vm.FilteredAccounts.Count);
            Assert.True(ms < 500, $"Group filter took {ms:F1} ms (budget 500 ms)");
        }

        [Fact]
        public void Sort_1000_CompletesWithinBudget()
        {
            var vm = new MainViewModel();
            vm.LoadAccounts(BuildAccounts(AccountCount));
            bool desc = true;
            double ms = Measure("Sort toggle", () =>
            {
                vm.SortMode = desc ? AccountSortMode.NaturalDescending : AccountSortMode.NaturalAscending;
                desc = !desc;
            });
            vm.SortMode = AccountSortMode.NaturalDescending;
            Assert.Equal("Farmer_1000", vm.FilteredAccounts[0].Username);
            Assert.True(ms < 500, $"Sort took {ms:F1} ms (budget 500 ms)");
        }
    }
}
