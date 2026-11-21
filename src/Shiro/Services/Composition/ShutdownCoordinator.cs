#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;

namespace Shiro.Services
{
    public enum ShutdownPhase
    {
        StopMonitors = 1,
        StopServersAndIntegrations = 2,
        FlushPersistence = 3,
        ClearSecretsAndPools = 4
    }

    public interface IShutdownParticipant
    {
        ShutdownPhase Phase { get; }
        string Name { get; }
        void ExecuteShutdown();
    }

    /// <summary>
    /// Executes shutdown participants in deterministic phase order with exception isolation
    /// and idempotent one-shot execution.
    /// </summary>
    public sealed class ShutdownCoordinator
    {
        private sealed class DelegateParticipant : IShutdownParticipant
        {
            private readonly Action _action;

            public DelegateParticipant(ShutdownPhase phase, string name, Action action)
            {
                Phase = phase;
                Name = name;
                _action = action;
            }

            public ShutdownPhase Phase { get; }
            public string Name { get; }
            public void ExecuteShutdown() => _action();
        }

        private readonly System.Threading.Lock _lock = new();
        private readonly List<(IShutdownParticipant Participant, int Order)> _participants = new();
        private int _nextOrder;
        private int _executed;

        public void Register(IShutdownParticipant participant)
        {
            ArgumentNullException.ThrowIfNull(participant);

            lock (_lock)
            {
                if (Volatile.Read(ref _executed) != 0)
                    return;

                _participants.Add((participant, _nextOrder++));
            }
        }

        public void Register(ShutdownPhase phase, string name, Action action)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Shutdown participant name must not be empty.", nameof(name));
            ArgumentNullException.ThrowIfNull(action);

            Register(new DelegateParticipant(phase, name, action));
        }

        public IReadOnlyList<string> ExecuteAll()
        {
            if (Interlocked.Exchange(ref _executed, 1) != 0)
                return Array.Empty<string>();

            List<(IShutdownParticipant Participant, int Order)> snapshot;
            lock (_lock)
            {
                snapshot = new List<(IShutdownParticipant Participant, int Order)>(_participants);
            }

            snapshot.Sort(static (a, b) =>
            {
                int phaseCmp = ((int)a.Participant.Phase).CompareTo((int)b.Participant.Phase);
                return phaseCmp != 0 ? phaseCmp : a.Order.CompareTo(b.Order);
            });

            var executed = new List<string>(snapshot.Count);
            foreach (var entry in snapshot)
            {
                IShutdownParticipant participant = entry.Participant;
                try
                {
                    participant.ExecuteShutdown();
                }
                catch (Exception ex)
                {
                    AppServices.Logger?.Debug($"[ShutdownCoordinator] '{participant.Name}' failed during {participant.Phase}: {ex.Message}");
                }

                executed.Add(participant.Name);
            }

            return executed;
        }
    }
}
