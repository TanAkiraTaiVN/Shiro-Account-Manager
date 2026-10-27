#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RBX_Alt_Manager.Classes;
using RBX_Alt_Manager.Services;

namespace RBX_Alt_Manager
{
    public partial class AccountManager
    {
        private static string RecentGamesFilePath => StoragePaths.RecentGamesFile;

        private static readonly Timer _deferredSettingsSaveTimer = new Timer(_ =>
        {
            try { SaveSettings(); }
            catch (Exception ex) { Program.Logger?.Error($"[DeferredSettingsSave] Error saving settings: {ex.Message}"); }
        }, null, Timeout.Infinite, Timeout.Infinite);

        internal static void QueueDeferredSaveSettings(int delayMs = 500)
        {
            try
            {
                _deferredSettingsSaveTimer.Change(Math.Max(50, delayMs), Timeout.Infinite);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Program.Logger?.Warn($"Failed to schedule deferred settings save: {ex.Message}");
            }
        }

        internal static void SaveSettings()
        {
            AccountManager? instance = Instance;
            if (instance?.settingsService != null)
            {
                instance.settingsService.Save();
                return;
            }

            IniSettings?.Save(StoragePaths.SettingsFile);
        }

        private async Task LoadRecentGames()
        {
            lock (rgSaveLock)
            {
                RecentGames = new List<Game>();
            }

            if (File.Exists(RecentGamesFilePath))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(RecentGamesFilePath).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        List<Game>? Games = Utilities.FastJson.TryDeserialize<List<Game>>(json, out var gList) ? gList : null;
                        if (Games != null && Games.Count > 0)
                        {
                            await Task.WhenAll(Games.Where(g => g != null).Select(g => g.WaitForDetails())).ConfigureAwait(false);
                            foreach (Game RG in Games)
                            {
                                if (RG != null)
                                    await AddRecentGame(RG, true).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger?.Warn($"Failed to load recent games: {ex.Message}");
                }
            }
        }

        private async Task AddRecentGame(Game RG, bool Loading = false)
        {
            if (RG == null) return;
            await RG.WaitForDetails().ConfigureAwait(false);

            int maxRecentGames = General?.Get<int>("MaxRecentGames") ?? 10;
            string? serialized = null;

            lock (rgSaveLock)
            {
                RecentGames ??= new List<Game>();
                RecentGames.RemoveAll(g => g?.Details?.placeId == RG.Details?.placeId);

                while (RecentGames.Count > maxRecentGames && RecentGames.Count > 0)
                {
                    RecentGames.RemoveAt(0);
                }

                RecentGames.Add(RG);

                if (!Loading)
                {
                    serialized = Utilities.FastJson.Serialize(RecentGames, indented: true);
                    StoragePaths.AtomicWriteText(RecentGamesFilePath, serialized);
                }
            }

            if (!Loading)
            {
                this.InvokeIfRequired(() => RecentGameAdded?.Invoke(this, new GameArgs(RG)));
            }
        }
    }
}
