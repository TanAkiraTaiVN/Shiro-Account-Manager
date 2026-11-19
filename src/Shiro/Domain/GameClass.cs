#nullable enable
using Shiro.Services;
using System;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Shiro
{
    public class Game
    {
        public GameDetails? Details;
        public string? ImageUrl;
        [JsonIgnore] public Action? Callback;

        public Game() { } // Default Constructor so that Details isn't overriden with Unknown when deserializing

        public Game(long PlaceId) : this(PlaceId, "Unknown") { }

        public Game(long PlaceId, string Name)
        {
            if (Batch.PlaceDetails.TryGetValue(PlaceId, out GameDetails? ExistingDetails) && ExistingDetails != null)
                Details = ExistingDetails;
            else if (PlaceId > 0)
                _ = LoadDetailsAsync(PlaceId, Name);
        }

        private async Task LoadDetailsAsync(long placeId, string name)
        {
            try
            {
                ImageUrl = await Batch.GetGameIcon(placeId).ConfigureAwait(false);
                if (Batch.PlaceDetails.TryGetValue(placeId, out GameDetails? resolvedDetails) &&
                    !Services.GameInfoService.IsGenericPlaceholderName(placeId, resolvedDetails.BestDisplayName))
                {
                    Details = resolvedDetails;
                    Services.GameInfoService.CachePlaceName(placeId, resolvedDetails.BestDisplayName);
                }
                else
                {
                    string? fallbackTitle = await Services.GameInfoService.ResolvePlaceNameAsync(placeId).ConfigureAwait(false);
                    string finalName = !string.IsNullOrWhiteSpace(fallbackTitle) ? fallbackTitle! : name;
                    Details ??= GameDetails.Unknown(placeId, finalName);
                    if (!string.IsNullOrWhiteSpace(fallbackTitle))
                    {
                        Details.name = fallbackTitle!;
                        Details.sourceName = fallbackTitle!;
                    }
                }
            }
            catch (Exception exception)
            {
                AppServices.Logger?.Debug($"Could not load game details for {placeId}: {exception.Message}");
                Details ??= GameDetails.Unknown(placeId, name);
            }
        }

        public async Task WaitForDetails(int Delay = 60, System.Threading.CancellationToken cancellationToken = default)
        {
            long startTick = System.Diagnostics.Stopwatch.GetTimestamp();
            TimeSpan maxWait = TimeSpan.FromSeconds(15);
            int safeDelay = Math.Max(15, Delay);

            while (Details == null && System.Diagnostics.Stopwatch.GetElapsedTime(startTick) < maxWait)
            {
                await Task.Delay(safeDelay, cancellationToken).ConfigureAwait(false);
            }

            Callback?.Invoke();
        }
    }

    public partial class GameDetails
    {
        private string _Name = "";

        public string name
        {
            get => _Name;
            set => _Name = value ?? string.Empty;
        }

        public long placeId;
        public string? description;
        public string? sourceName;
        public string? sourceDescription;

        [JsonIgnore]
        public string BestDisplayName => Services.GameInfoService.SanitizeDisplayTitle(
            !string.IsNullOrWhiteSpace(sourceName)
                ? sourceName.Trim()
                : (name ?? string.Empty).Trim());
        public string? url;
        public string? builder;
        public long builderId;
        public bool hasVerifiedBadge;
        public bool isPlayable;
        public string? reasonProhibited;
        public long universeId;
        public long universeRootPlaceId;
        public long price;
        public string? imageToken;

        public static GameDetails Unknown(long PlaceID = 0, string Name = "Unknown") => new GameDetails() { placeId = PlaceID, name = Name };
    }

    public class FavoriteGame : Game
    {
        public string Name;
        public string PrivateServer;
        public long PlaceID;

        [JsonConstructor]
        public FavoriteGame() : base(0)
        {
            Name = "???";
            PrivateServer = "";
        }

        public FavoriteGame(long PlaceID, Action? Callback = null) : base(PlaceID)
        {
            Name = "???";
            PrivateServer = "";
            this.PlaceID = PlaceID;
            this.Callback = Callback;
        }

        public FavoriteGame(string Name, long PlaceID) : this(PlaceID) => this.Name = Name;
        public FavoriteGame(string Name, long PlaceID, Action? Callback) : this(PlaceID, Callback) => this.Name = Name;
        public FavoriteGame(string Name, long PlaceID, string PrivateServer) : this(Name, PlaceID) => this.PrivateServer = PrivateServer;
        public FavoriteGame(string Name, long PlaceID, string PrivateServer, Action? Callback) : this(Name, PlaceID, Callback) => this.PrivateServer = PrivateServer;
    }

    public class GameArgs : EventArgs
    {
        public Game Game { get; set; }

        public GameArgs(Game Game) => this.Game = Game;
    }
}
