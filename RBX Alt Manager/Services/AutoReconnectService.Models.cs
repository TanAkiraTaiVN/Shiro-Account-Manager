#nullable enable
using System;
using Shiro.Classes;

namespace Shiro.Services
{
    public sealed partial class AutoReconnectService
    {
        public enum ReconnectDecision
        {
            None,
            CloseCleanly,
            Reconnect,
            KillAndReconnect,
            GiveUp
        }

        public sealed class ReconnectSession
        {
            public required Account Account { get; init; }
            public long PlaceId { get; set; }
            public string JobId { get; set; } = string.Empty;
            public bool FollowUser { get; set; }
            public bool JoinVip { get; set; }
            public int ProcessId { get; set; }
            public int RetryCount { get; set; }
            public DateTime LaunchTimeUtc { get; set; }
            public DateTime LastCheckUtc { get; set; }
            public DateTime? LastReconnectUtc { get; set; }
            public DateTime? LastDisconnectUtc { get; set; }
            public int LastDisconnectReason { get; set; }
            public int? ObservedExitCode { get; set; }
            public bool ObservedProcessExited { get; set; }
            public bool IsActive { get; set; } = true;
            public bool IsSuppressed { get; set; }
        }
    }
}
