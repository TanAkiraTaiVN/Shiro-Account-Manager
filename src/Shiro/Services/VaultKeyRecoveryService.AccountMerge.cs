#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Shiro.Classes;

namespace Shiro.Services
{
    internal static partial class VaultKeyRecoveryService
    {
        public static int MergeAllKnownAccountsCli()
        {
            try
            {
                StoragePaths.EnsureInitialized();
                byte[] vaultKey = StoragePaths.GetOrCreateVaultKey();
                var byUsername = new Dictionary<string, Account>(StringComparer.OrdinalIgnoreCase);

                void MergeList(IEnumerable<Account> list)
                {
                    if (list == null) return;
                    foreach (var acc in list)
                    {
                        if (acc == null || string.IsNullOrWhiteSpace(acc.Username))
                            continue;

                        string u = acc.Username.Trim();
                        if (!byUsername.TryGetValue(u, out var existing))
                        {
                            byUsername[u] = acc;
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(existing.SecurityToken) && !string.IsNullOrWhiteSpace(acc.SecurityToken))
                            {
                                existing.SecurityToken = acc.SecurityToken;
                                existing.Valid = acc.Valid;
                            }
                            if (string.IsNullOrWhiteSpace(existing.Password) && !string.IsNullOrWhiteSpace(acc.Password))
                                existing.Password = acc.Password;
                            if (existing.UserID <= 0 && acc.UserID > 0)
                                existing.UserID = acc.UserID;
                            if (string.IsNullOrWhiteSpace(existing.Alias) && !string.IsNullOrWhiteSpace(acc.Alias))
                                existing.Alias = acc.Alias;
                        }
                    }
                }

                // 1. Load current AccountData.json
                if (File.Exists(StoragePaths.AccountDataFile))
                {
                    try
                    {
                        byte[] raw = File.ReadAllBytes(StoragePaths.AccountDataFile);
                        if (Cryptography.IsEncrypted(raw))
                        {
                            byte[] dec = Cryptography.Decrypt(raw, vaultKey);
                            MergeList(AccountDataSerializer.Deserialize(dec, out _));
                        }
                    }
                    catch (Exception ex)
                    {
                        TraceLog($"Failed reading current AccountData.json candidate: {ex.Message}");
                    }
                }

                // 2. Load DPAPI backups from application data directory
                string[] dpapiFiles =
                {
                    StoragePaths.AccountDataFile + ".previous",
                    StoragePaths.AccountDataFile + ".backup.previous"
                };

                foreach (string df in dpapiFiles)
                {
                    try
                    {
                        if (!File.Exists(df)) continue;
                        byte[] raw = File.ReadAllBytes(df);
                        byte[] dec = ProtectedData.Unprotect(raw, LegacyDpapiEntropy, DataProtectionScope.CurrentUser);
                        MergeList(AccountDataSerializer.Deserialize(dec, out _));
                    }
                    catch (Exception ex)
                    {
                        TraceLog($"Failed reading DPAPI candidate '{df}': {ex.Message}");
                    }
                }

                // 3. Resolve any cached avatar UserIDs via Roblox Users API
                string avatarDir = Path.Combine(StoragePaths.CacheDirectory, "Avatars");
                if (Directory.Exists(avatarDir))
                {
                    var userIds = new List<long>();
                    foreach (string png in Directory.EnumerateFiles(avatarDir, "*.png"))
                    {
                        ReadOnlySpan<char> name = Path.GetFileNameWithoutExtension(png.AsSpan());
                        if (long.TryParse(name, out long uid) && uid > 0)
                            userIds.Add(uid);
                    }

                    if (userIds.Count > 0)
                    {
                        var http = SharedHttpClient.Get();
                        foreach (long[] batch in System.Linq.Enumerable.Chunk(userIds, 50))
                        {
                            string reqJson = "{\"userIds\":[" + string.Join(",", batch) + "],\"excludeBannedUsers\":false}";
                            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, "https://users.roblox.com/v1/users")
                            {
                                Content = new System.Net.Http.StringContent(reqJson, Encoding.UTF8, "application/json")
                            };
                            using var resp = http.Send(request);
                            if (resp.IsSuccessStatusCode)
                            {
                                using var reader = new StreamReader(resp.Content.ReadAsStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                                string body = reader.ReadToEnd();
                                using var doc = System.Text.Json.JsonDocument.Parse(body);
                                if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    foreach (var item in dataArr.EnumerateArray())
                                    {
                                        long id = item.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : 0;
                                        string? uname = item.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;
                                        if (id > 0 && !string.IsNullOrWhiteSpace(uname))
                                        {
                                            MergeList(new[]
                                            {
                                                new Account
                                                {
                                                    Username = uname,
                                                    UserID = id,
                                                    Password = string.Empty,
                                                    Group = "Default",
                                                    Valid = false
                                                }
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                var finalList = new List<Account>(byUsername.Values);
                byte[] serialized = AccountDataSerializer.Serialize(finalList, includePasswords: true);
                byte[] encrypted = Cryptography.Encrypt(serialized, vaultKey);
                StoragePaths.AtomicWriteBytes(StoragePaths.AccountDataFile, encrypted, durable: true);
                StoragePaths.AtomicWriteBytes(StoragePaths.AccountDataFile + ".backup", encrypted, durable: true);
                TraceLog($"Merged and saved {finalList.Count} accounts successfully ({encrypted.Length} bytes).");
                return 0;
            }
            catch (Exception ex)
            {
                TraceLog($"Merge error: {ex}");
                return 1;
            }
        }
    }
}
