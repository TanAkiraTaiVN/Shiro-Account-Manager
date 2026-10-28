#nullable enable
using Shiro.Classes;
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
            if (Batch.PlaceDetails.TryGetValue(PlaceId, out GameDetails? ExistingDetails) && !string.IsNullOrEmpty(ImageUrl))
                Details = ExistingDetails;
            else
                _ = LoadDetailsAsync(PlaceId, Name);
        }

        private async Task LoadDetailsAsync(long placeId, string name)
        {
            try
            {
                ImageUrl = await Batch.GetGameIcon(placeId).ConfigureAwait(false);
                Details = Batch.PlaceDetails.TryGetValue(placeId, out GameDetails? resolvedDetails)
                    ? resolvedDetails
                    : (Details ?? GameDetails.Unknown(placeId, name));
            }
            catch (Exception exception)
            {
                Program.Logger?.Debug($"Could not load game details for {placeId}: {exception.Message}");
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
        [GeneratedRegex(@"[\s]?\[[^]]*\][\s]?")]
        private static partial Regex BracketsRegex();

        [GeneratedRegex("[^a-zA-Z0-9% ._]")]
        private static partial Regex CleanNameRegex();

        private string _Name = "";

        public string? filteredName;
        public string name
        {
            get => _Name;
            set
            {
                _Name = value ?? string.Empty;

                if (value != null)
                {
                    value = BracketsRegex().Replace(value, "");
                    value = CleanNameRegex().Replace(value, string.Empty);
                }

                filteredName = value;
            }
        }

        public long placeId;
        public string? description;
        public string? sourceName;
        public string? sourceDescription;
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

    public class PageGame : Game
    {
        public long CreatorID { get; set; }
        public string? CreatorName { get; set; }
        public string? CreatorUrl { get; set; }
        public long Plays { get; set; }
        public long Price { get; set; }
        public long ProductID { get; set; }
        public bool IsOwned { get; set; }
        public bool IsVotingEnabled { get; set; }
        public long TotalUpVotes { get; set; }
        public long TotalDownVotes { get; set; }
        public long TotalBought { get; set; }
        public long UniverseID { get; set; }
        public bool HasErrorOcurred { get; set; }
        public string? GameDetailReferralUrl { get; set; }
        public string? Url { get; set; }
        public object? RetryUrl { get; set; }
        public bool Final { get; set; }
        public string Name { get; set; }
        public long PlaceID { get; set; }
        public int PlayerCount { get; set; }
        public long ImageId { get; set; }
        public int? LikeRatio; // Doesn't actually exist in roblox's return value

        [JsonConstructor] public PageGame(long PlaceID, string Name) : base(PlaceID, Name) { this.PlaceID = PlaceID; this.Name = Name; }
    }

    public class FavoriteGame : Game
    {
        public string Name;
        public string PrivateServer;
        public long PlaceID;

        public FavoriteGame() : base(0)
        {
            Name = "???";
            PrivateServer = "";
        }

        [JsonConstructor]
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
