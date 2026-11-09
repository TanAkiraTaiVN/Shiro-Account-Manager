#nullable enable
using System.Collections.Generic;
using System.Linq;
using Shiro.Services;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Dialogs
{
    public sealed class FastLoginQueueTests
    {
        [Fact]
        public void Ctor_SkipsNulls()
        {
            var a1 = new Account { Username = "u1", UserID = 1 };
            var a2 = new Account { Username = "u2", UserID = 2 };
            var queue = new FastLoginQueue(new Account?[] { null, a1, null, a2 }, _ => { });

            Assert.Equal(2, queue.Count);
            Assert.Same(a1, queue.Current);
        }

        [Fact]
        public void Title_Single_vs_Progress()
        {
            var a1 = new Account { Username = "alice", UserID = 1, Password = "pw" };
            var a2 = new Account { Username = "bob", UserID = 2 };

            var single = new FastLoginQueue(new[] { a1 }, _ => { });
            Assert.Equal(LocalizationService.Format("FastLogin.DialogSingleTitle", "alice"), single.Title);
            Assert.Equal(LocalizationService.Get("FastLogin.AutoFillUserPassTip"), single.Subtitle);

            var multi = new FastLoginQueue(new[] { a1, a2 }, _ => { });
            Assert.Equal(LocalizationService.Format("FastLogin.DialogProgressTitle", "alice", 1, 2), multi.Title);
            Assert.True(multi.MoveNext());
            Assert.Equal(LocalizationService.Format("FastLogin.DialogProgressTitle", "bob", 2, 2), multi.Title);
            Assert.Equal(LocalizationService.Get("FastLogin.AutoFillUserOnlyTip"), multi.Subtitle);
        }

        [Fact]
        public void HasNext_FalseOnLast()
        {
            var a1 = new Account { Username = "u1", UserID = 1 };
            var a2 = new Account { Username = "u2", UserID = 2 };
            var queue = new FastLoginQueue(new[] { a1, a2 }, _ => { });

            Assert.True(queue.HasNext);
            Assert.True(queue.MoveNext());
            Assert.False(queue.HasNext);
            Assert.False(queue.MoveNext());
        }

        [Fact]
        public void MoveTo_PersistsRemainingIds()
        {
            var a1 = new Account { Username = "u1", UserID = 10 };
            var a2 = new Account { Username = "u2", UserID = 20 };
            var a3 = new Account { Username = "u3", UserID = 30 };

            long[]? persisted = null;
            var queue = new FastLoginQueue(new[] { a1, a2, a3 }, ids => persisted = ids.ToArray());

            Assert.True(queue.MoveTo(1));
            Assert.Equal(1, queue.Index);
            Assert.Same(a2, queue.Current);
            Assert.NotNull(persisted);
            Assert.Equal(new long[] { 20, 30 }, persisted!);
        }

        [Fact]
        public void MoveTo_OutOfRange_ReturnsFalse()
        {
            var a1 = new Account { Username = "u1", UserID = 10 };
            var queue = new FastLoginQueue(new[] { a1 }, _ => { });

            Assert.False(queue.MoveTo(-1));
            Assert.False(queue.MoveTo(1));
            Assert.Equal(0, queue.Index);
        }
    }
}
