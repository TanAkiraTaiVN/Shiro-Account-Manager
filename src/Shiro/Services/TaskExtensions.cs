#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Shiro.Services
{
    /// <summary>
    /// Safe fire-and-forget task observation helpers that prevent unobserved background task exceptions from being silently lost.
    /// </summary>
    internal static class TaskExtensions
    {
        public static void Forget(this Task? task, [CallerMemberName] string caller = "")
        {
            if (task == null || task.IsCompletedSuccessfully)
                return;

            _ = ObserveAsync(task, caller);

            static async Task ObserveAsync(Task t, string operation)
            {
                try
                {
                    await t.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Program.Logger?.Error($"[UnhandledAsync:{operation}] {ex.Message}");
                }
            }
        }
    }
}
