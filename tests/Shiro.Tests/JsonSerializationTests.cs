using System;
using System.Collections.Generic;
using Shiro.Services;
using Shiro.Nexus;
using Xunit;

namespace Shiro.Tests
{
    public class JsonSerializationTests
    {
        [Fact]
        public void ControlledAccount_Roundtrip_FastJson()
        {
            var accounts = new List<ControlledAccount>
            {
                new ControlledAccount
                {
                    Username = "TestBot1",
                    AutoExecute = "print('hello')",
                    PlaceId = 1818,
                    JobId = "job-uuid-1234",
                    AutoRelaunch = true,
                    RelaunchDelay = 45
                },
                new ControlledAccount
                {
                    Username = "TestBot2",
                    AutoExecute = "",
                    PlaceId = 9999,
                    JobId = "job-uuid-5678",
                    AutoRelaunch = false,
                    RelaunchDelay = 15
                }
            };

            string json = FastJson.Serialize(accounts, indented: true);
            Assert.False(string.IsNullOrWhiteSpace(json));

            bool success = FastJson.TryDeserialize<List<ControlledAccount>>(json, out var deserialized);
            Assert.True(success);
            Assert.NotNull(deserialized);
            Assert.Equal(2, deserialized.Count);

            Assert.Equal("TestBot1", deserialized[0].Username);
            Assert.Equal("print('hello')", deserialized[0].AutoExecute);
            Assert.Equal(1818, deserialized[0].PlaceId);
            Assert.Equal("job-uuid-1234", deserialized[0].JobId);
            Assert.True(deserialized[0].AutoRelaunch);
            Assert.Equal(45, deserialized[0].RelaunchDelay);

            // Verify runtime fields were ignored during serialization
            Assert.Null(deserialized[0].LinkedAccount);
            Assert.Null(deserialized[0].WebSocket);
            Assert.Null(deserialized[0].InGameJobId);
        }

        [Fact]
        public void FavoriteGame_Roundtrip_FastJson()
        {
            var favorites = new List<FavoriteGame>
            {
                new FavoriteGame("Blox Fruits", 2753915549, "https://roblox.com/privateServer?code=123"),
                new FavoriteGame("Pet Simulator 99", 8737899170, "")
            };

            string json = FastJson.Serialize(favorites, indented: true);
            Assert.False(string.IsNullOrWhiteSpace(json));

            bool success = FastJson.TryDeserialize<List<FavoriteGame>>(json, out var deserialized);
            Assert.True(success);
            Assert.NotNull(deserialized);
            Assert.Equal(2, deserialized.Count);

            Assert.Equal("Blox Fruits", deserialized[0].Name);
            Assert.Equal(2753915549, deserialized[0].PlaceID);
            Assert.Equal("https://roblox.com/privateServer?code=123", deserialized[0].PrivateServer);

            Assert.Equal("Pet Simulator 99", deserialized[1].Name);
            Assert.Equal(8737899170, deserialized[1].PlaceID);
        }

        [Fact]
        public void ServersInfo_Deserialization_FastJson()
        {
            string robloxServersJson = """
            {
                "previousPageCursor": null,
                "nextPageCursor": "cursor_token_abc_123",
                "data": [
                    {
                        "id": "e45a2cbb-3e28-4ef6-bb4d-6169ce3309a9",
                        "maxPlayers": 20,
                        "playing": 18,
                        "playerTokens": ["token1", "token2"],
                        "fps": "59.8",
                        "ping": 42
                    }
                ]
            }
            """;

            bool success = FastJson.TryDeserialize<ServersInfo>(robloxServersJson, out var info);
            Assert.True(success);
            Assert.NotNull(info);
            Assert.Equal("cursor_token_abc_123", info.nextPageCursor);
            Assert.Single(info.data);

            var server = info.data[0];
            Assert.Equal("e45a2cbb-3e28-4ef6-bb4d-6169ce3309a9", server.id);
            Assert.Equal(20, server.maxPlayers);
            Assert.Equal(18, server.playing);
            Assert.NotNull(server.playerTokens);
            Assert.Equal(2, server.playerTokens.Count);
            Assert.Equal("59.8", server.fps);
            Assert.Equal(42, server.ping);
        }

        [Fact]
        public void ThumbnailBatch_Deserialization_FastJson()
        {
            string batchResponseJson = """
            {
                "data": [
                    {
                        "targetId": 12345678,
                        "state": "Completed",
                        "imageUrl": "https://tr.rbxcdn.com/test_avatar_headshot.png",
                        "errorCode": 0,
                        "errorMessage": ""
                    },
                    {
                        "targetId": 87654321,
                        "state": "Completed",
                        "imageUrl": "https://tr.rbxcdn.com/test_avatar_headshot_2.png",
                        "errorCode": 0,
                        "errorMessage": ""
                    }
                ]
            }
            """;

            bool success = FastJson.TryDeserialize<ThumbnailBatchResponse>(batchResponseJson, out var batch);
            Assert.True(success);
            Assert.NotNull(batch);
            Assert.NotNull(batch.Data);
            Assert.Equal(2, batch.Data.Count);

            Assert.Equal(12345678, batch.Data[0].TargetId);
            Assert.Equal("https://tr.rbxcdn.com/test_avatar_headshot.png", batch.Data[0].ImageUrl);
            Assert.Equal(0, batch.Data[0].ErrorCode);

            Assert.Equal(87654321, batch.Data[1].TargetId);
        }

        [Fact]
        public void RobloxPinInfo_Deserialization_FastJson()
        {
            string pinJson = """
            {
                "isEnabled": true,
                "unlockedUntil": 300
            }
            """;

            bool success = FastJson.TryDeserialize<RobloxPinInfo>(pinJson, out var pin);
            Assert.True(success);
            Assert.NotNull(pin);
            Assert.True(pin.isEnabled);
            Assert.Equal(300, pin.unlockedUntil);
        }

        [Fact]
        public void Command_Deserialization_FastJson()
        {
            string commandJson = """
            {
                "Name": "SetRelaunch",
                "Payload": {
                    "Seconds": "45"
                }
            }
            """;

            bool success = FastJson.TryDeserialize<Command>(commandJson, out var cmd);
            Assert.True(success);
            Assert.NotNull(cmd);
            Assert.Equal("SetRelaunch", cmd.Name);
            Assert.NotNull(cmd.Payload);
            Assert.Equal("45", cmd.Payload["Seconds"]);
        }

        [Fact]
        public void ServersInfo_Deserialization_FromRobloxApi()
        {
            string apiJson = """
            {
                "previousPageCursor": null,
                "nextPageCursor": "cursor123",
                "data": [
                    {
                        "id": "server-1",
                        "maxPlayers": 9,
                        "playing": 1,
                        "playerTokens": [],
                        "players": [],
                        "fps": 59.997517,
                        "ping": 24
                    },
                    {
                        "id": "server-2",
                        "maxPlayers": 12,
                        "playing": 5,
                        "playerTokens": [],
                        "players": [],
                        "fps": 60,
                        "ping": null
                    },
                    {
                        "id": "server-3",
                        "maxPlayers": 20,
                        "playing": 18,
                        "playerTokens": [],
                        "players": [],
                        "fps": "58.5"
                    }
                ]
            }
            """;

            bool success = FastJson.TryDeserialize<ServersInfo>(apiJson, out var info);
            Assert.True(success);
            Assert.NotNull(info);
            Assert.Equal(3, info.data.Count);
            Assert.Equal("server-1", info.data[0].id);
            Assert.Equal("59.997517", info.data[0].fps);
            Assert.Equal(24, info.data[0].ping);
            Assert.Equal("60", info.data[1].fps);
            Assert.Null(info.data[1].ping);
            Assert.Equal("58.5", info.data[2].fps);
            Assert.Null(info.data[2].ping);
        }
    }
}
