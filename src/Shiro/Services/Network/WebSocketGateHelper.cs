#nullable enable
using System.Runtime.CompilerServices;
using System.Threading;

namespace Shiro.Services
{
    internal static class WebSocketGateHelper
    {
        private sealed class GateState
        {
            public readonly object Sync = new();
            public int ActiveUsers;
            public bool Retired;
        }

        private static readonly ConditionalWeakTable<SemaphoreSlim, GateState> States = new();

        public static bool TryEnterUse(SemaphoreSlim? gate)
        {
            if (gate == null) return false;
            var state = States.GetOrCreateValue(gate);
            lock (state.Sync)
            {
                if (state.Retired) return false;
                state.ActiveUsers++;
                return true;
            }
        }

        public static bool IsRetired(SemaphoreSlim? gate)
        {
            if (gate == null) return true;
            if (!States.TryGetValue(gate, out var state)) return false;
            lock (state.Sync)
            {
                return state.Retired;
            }
        }

        public static void ExitUse(SemaphoreSlim? gate)
        {
            if (gate == null) return;
            bool shouldDispose = false;
            if (States.TryGetValue(gate, out var state))
            {
                lock (state.Sync)
                {
                    if (state.ActiveUsers > 0)
                        state.ActiveUsers--;
                    if (state.Retired && state.ActiveUsers == 0)
                        shouldDispose = true;
                }
            }

            if (shouldDispose)
            {
                SafeExecuteHelper.Try(gate.Dispose);
            }
        }

        public static void RetireAndDispose(SemaphoreSlim? gate)
        {
            if (gate == null) return;
            bool shouldDispose = false;
            var state = States.GetOrCreateValue(gate);
            lock (state.Sync)
            {
                if (!state.Retired)
                {
                    state.Retired = true;
                    if (state.ActiveUsers == 0)
                        shouldDispose = true;
                }
            }

            if (shouldDispose)
            {
                SafeExecuteHelper.Try(gate.Dispose);
            }
        }
    }
}
