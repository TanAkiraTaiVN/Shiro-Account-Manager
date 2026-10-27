using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RBX_Alt_Manager
{
    public class ServerData
    {
        public string id { get; set; }
        public int maxPlayers { get; set; }
        public int playing { get; set; }
        public List<string> playerTokens { get; set; }
        public List<object> players { get; set; }
        [JsonConverter(typeof(Services.FlexibleStringConverter))]
        public string fps { get; set; }
        public int? ping { get; set; }
        public string name { get; set; }
        public long? vipServerId { get; set; }
        public string accessCode { get; set; }
        public string type { get; set; }
        public string region { get; set; }
        public string ip { get; set; }
        public bool regionLoaded { get; set; }

        public ServerData()
        {
        }

        public ServerData(string id, int maxPlayers, int playing, string fps, int? ping = null)
        {
            this.id = id;
            this.maxPlayers = maxPlayers;
            this.playing = playing;
            this.fps = fps;
            this.ping = ping;
        }
    }

    public class ServersInfo
    {
        public string previousPageCursor { get; set; } = "_";
        public string nextPageCursor { get; set; } = "_";
        public List<ServerData> data { get; set; } = [];

        public ServersInfo()
        {
        }
    }
}