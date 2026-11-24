#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public interface ILaunchSchedulerService : IDisposable
    {
        bool IsGlobalEnabled { get; set; }
        bool IsRunning { get; }

        IReadOnlyList<LaunchSchedule> GetSchedules();
        IReadOnlyList<ScheduleRunEntry> GetHistory();

        event Action? SchedulesChanged;
        event Action? HistoryChanged;
        event Action? StateChanged;

        void AttachExecutor(ISchedulerActionExecutor executor);
        void Start();
        void StopForShutdown();

        ScheduleValidationResult UpsertSchedule(LaunchSchedule schedule);
        bool DeleteSchedule(string scheduleId, out LaunchSchedule? removed);
        bool RestoreSchedule(LaunchSchedule schedule);
        bool SetScheduleEnabled(string scheduleId, bool enabled);
        void ClearHistory();

        Task<ScheduleRunEntry?> RunScheduleNowAsync(string scheduleId, CancellationToken ct = default);
        Task<ScheduleRunEntry?> CloseScheduleTabsNowAsync(string scheduleId, CancellationToken ct = default);
    }
}
