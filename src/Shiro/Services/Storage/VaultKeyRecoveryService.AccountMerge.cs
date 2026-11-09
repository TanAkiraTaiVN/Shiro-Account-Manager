#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

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
                            bool accHasNewerToken = !string.IsNullOrWhiteSpace(acc.SecurityToken) &&
                                                    (string.IsNullOrWhiteSpace(existing.SecurityToken) || acc.LastUse > existing.LastUse);
                            if (accHasNewerToken)
                            {
                                existing.SecurityToken = acc.SecurityToken;
                                existing.Valid = acc.Valid;
                                if (acc.LastUse > existing.LastUse)
                                    existing.LastUse = acc.LastUse;
                            }
                            if (string.IsNullOrWhiteSpace(existing.Password) && !string.IsNullOrWhiteSpace(acc.Password))
                                existing.Password = acc.Password;
                            if (existing.UserID <= 0 && acc.UserID > 0)
                                existing.UserID = acc.UserID;
                            if (string.IsNullOrWhiteSpace(existing.Alias) && !string.IsNullOrWhiteSpace(acc.Alias))
                                existing.Alias = acc.Alias;
                            if (acc.Fields != null && acc.Fields.Count > 0)
                            {
                                existing.Fields ??= new Dictionary<string, string>();
                                foreach (var kvp in acc.Fields)
                                {
                                    if (!existing.Fields.TryGetValue(kvp.Key, out string? curVal) || string.IsNullOrWhiteSpace(curVal))
                                        existing.Fields[kvp.Key] = kvp.Value;
                                }
                            }
                        }
                    }
                }

                // 1. Load current AccountData.json and historical AES backups (newest first)
                var encryptedCandidates = new List<string>
                {
                    StoragePaths.AccountDataFile,
                    StoragePaths.AccountDataFile + ".backup",
                    StoragePaths.AccountDataFile + ".bak"
                };

                try
                {
                    if (Directory.Exists(StoragePaths.BackupsDirectory))
                    {
                        var historical = Directory.GetFiles(StoragePaths.BackupsDirectory, "AccountData_*.json.bak")
                            .OrderByDescending(f =>
                            {
                                try { return File.GetLastWriteTimeUtc(f); }
                                catch (Exception ex)
                                {
                                    AppServices.Logger.Debug("[VaultKeyRecoveryService] GetLastWriteTimeUtc failed", ex);
                                    return DateTime.MinValue;
                                }
                            });
                        encryptedCandidates.AddRange(historical);
                    }
                }
                catch (Exception ex)
                {
                    TraceLog($"Failed enumerating BackupsDirectory: {ex.Message}");
                }

                bool primaryDecrypted = false;
                foreach (string encFile in encryptedCandidates)
                {
                    if (!File.Exists(encFile))
                        continue;

                    try
                    {
                        byte[] raw = File.ReadAllBytes(encFile);
                        if (Cryptography.IsEncrypted(raw))
                        {
                            byte[] dec = Cryptography.Decrypt(raw, vaultKey);
                            MergeList(AccountDataSerializer.Deserialize(dec, out _));
                            if (string.Equals(encFile, StoragePaths.AccountDataFile, StringComparison.OrdinalIgnoreCase))
                                primaryDecrypted = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        TraceLog($"Failed reading encrypted account candidate '{encFile}': {ex.Message}");
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
                int validTokenCount = finalList.Count(a => !string.IsNullOrWhiteSpace(a.SecurityToken));
                if (File.Exists(StoragePaths.AccountDataFile) && !primaryDecrypted && validTokenCount == 0)
                {
                    TraceLog("Merge aborted: existing AccountData.json could not be decrypted and no valid tokens were recovered.");
                    return 1;
                }

                if (File.Exists(StoragePaths.AccountDataFile))
                {
                    try
                    {
                        Directory.CreateDirectory(StoragePaths.BackupsDirectory);
                        string preMergePath = Path.Combine(
                            StoragePaths.BackupsDirectory,
                            $"AccountData_pre_merge_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json.bak");
                        File.Copy(StoragePaths.AccountDataFile, preMergePath, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        TraceLog($"Failed to create pre-merge backup: {ex.Message}");
                    }
                }

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
