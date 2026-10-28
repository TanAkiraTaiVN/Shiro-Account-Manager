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

                // 2. Load DPAPI backups
                string[] dpapiFiles =
                {
                    StoragePaths.AccountDataFile + ".previous",
                    StoragePaths.AccountDataFile + ".backup.previous",
                    @"D:\$RECYCLE.BIN\S-1-5-21-3175979420-2019744931-1257509645-1001\$RHJYIIZ.2\AccountData.json",
                    @"D:\$RECYCLE.BIN\S-1-5-21-3175979420-2019744931-1257509645-1001\$RHJYIIZ.2\AccountData.json.backup"
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

                // 3. Load passwords from Recycle Bin text file if present
                var passwordMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string txtFile = @"D:\$RECYCLE.BIN\S-1-5-21-3175979420-2019744931-1257509645-1001\$RAZH0RO.txt";
                if (File.Exists(txtFile))
                {
                    foreach (string line in File.ReadLines(txtFile))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;
                        ReadOnlySpan<char> trimmed = line.AsSpan().Trim();
                        if (trimmed.IsEmpty || !trimmed.Contains(':'))
                            continue;
                        // Skip lines that have space annotations like "roblox khoá acc"
                        int spaceIdx = trimmed.IndexOf(' ');
                        ReadOnlySpan<char> token = spaceIdx >= 0 ? trimmed.Slice(0, spaceIdx) : trimmed;
                        int colon = token.IndexOf(':');
                        if (colon > 0 && colon < token.Length - 1)
                        {
                            string u = token.Slice(0, colon).Trim().ToString();
                            string p = token.Slice(colon + 1).Trim().ToString();
                            passwordMap[u] = p;
                        }
                    }
                }

                // 4. Resolve all cached avatar UserIDs via Roblox Users API
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
                        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                        foreach (long[] batch in System.Linq.Enumerable.Chunk(userIds, 50))
                        {
                            string reqJson = "{\"userIds\":[" + string.Join(",", batch) + "],\"excludeBannedUsers\":false}";
                            using var content = new System.Net.Http.StringContent(reqJson, Encoding.UTF8, "application/json");
                            using var resp = http.PostAsync("https://users.roblox.com/v1/users", content).GetAwaiter().GetResult();
                            if (resp.IsSuccessStatusCode)
                            {
                                string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                                using var doc = System.Text.Json.JsonDocument.Parse(body);
                                if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    foreach (var item in dataArr.EnumerateArray())
                                    {
                                        long id = item.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : 0;
                                        string? uname = item.TryGetProperty("name", out var nProp) ? nProp.GetString() : null;
                                        if (id > 0 && !string.IsNullOrWhiteSpace(uname))
                                        {
                                            passwordMap.TryGetValue(uname, out string? pw);
                                            if (string.IsNullOrEmpty(pw) && uname.StartsWith("AD_Jackpot", StringComparison.OrdinalIgnoreCase))
                                                pw = "0346172240a";

                                            MergeList(new[]
                                            {
                                                new Account
                                                {
                                                    Username = uname,
                                                    UserID = id,
                                                    Password = pw ?? string.Empty,
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

                // Also add any remaining AD_Jackpot accounts from passwordMap
                foreach (var kvp in passwordMap)
                {
                    if (!byUsername.ContainsKey(kvp.Key))
                    {
                        byUsername[kvp.Key] = new Account
                        {
                            Username = kvp.Key,
                            Password = kvp.Value,
                            Group = "Default",
                            Valid = false
                        };
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
