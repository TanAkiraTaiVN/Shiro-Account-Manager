#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shiro.Classes;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public class AccountStoreConcurrencyTests : IDisposable
    {
        public AccountStoreConcurrencyTests()
        {
            AccountManager.ReplaceAccountsList(new List<Account>());
        }

        public void Dispose()
        {
            AccountManager.ReplaceAccountsList(new List<Account>());
        }

        [Fact]
        public async Task AccountStore_ConcurrentAddAndSnapshot_DoesNotThrow()
        {
            AccountManager.ReplaceAccountsList(new List<Account>());

            using var cts = new CancellationTokenSource();
            var exceptions = new ConcurrentBag<Exception>();

            var readers = new Task[2];
            for (int r = 0; r < readers.Length; r++)
            {
                readers[r] = Task.Run(() =>
                {
                    try
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            var snapshot = AccountManager.GetAccountsSnapshot();
                            _ = snapshot.Count;
                            Thread.Yield();
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });
            }

            var writers = new Task[4];
            for (int w = 0; w < writers.Length; w++)
            {
                int writerIndex = w;
                writers[w] = Task.Run(() =>
                {
                    try
                    {
                        for (int i = 0; i < 500; i++)
                        {
                            var acc = new Account
                            {
                                Username = $"User_{writerIndex}_{i}",
                                UserID = (writerIndex * 10000) + i + 1,
                                SecurityToken = "token"
                            };
                            AccountManager.AddAccountThreadSafe(acc);
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                });
            }

            await Task.WhenAll(writers);
            cts.Cancel();
            await Task.WhenAll(readers);

            Assert.Empty(exceptions);
            Assert.Equal(2000, AccountManager.AccountCount);
        }
    }
}
