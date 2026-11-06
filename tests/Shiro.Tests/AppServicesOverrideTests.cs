#nullable enable
using System;
using NSubstitute;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("StaticSeams")]
    public class AppServicesOverrideTests
    {
        [Fact]
        public void Override_ReplacesService_WithinScope()
        {
            var fakeToast = Substitute.For<IToastNotifier>();
            var originalToast = AppServices.Toasts;

            Assert.NotSame(fakeToast, originalToast);

            using (AppServices.Override<IToastNotifier>(fakeToast))
            {
                Assert.Same(fakeToast, AppServices.Toasts);
            }

            Assert.Same(originalToast, AppServices.Toasts);
        }

        [Fact]
        public void Override_RestoresPrevious_WhenScopeDisposedAfterException()
        {
            var fakeToast = Substitute.For<IToastNotifier>();
            var prev = AppServices.Toasts;

            try
            {
                using (AppServices.Override<IToastNotifier>(fakeToast))
                {
                    Assert.Same(fakeToast, AppServices.Toasts);
                    throw new InvalidOperationException("Simulated exception within override scope");
                }
            }
            catch (InvalidOperationException ex) when (ex.Message == "Simulated exception within override scope")
            {
                // Expected
            }

            Assert.Same(prev, AppServices.Toasts);
        }

        [Fact]
        public void Override_Nested_RestoresInLifoOrder()
        {
            var originalToast = AppServices.Toasts;
            var originalBloxGen = AppServices.BloxGen;
            var fakeToast = Substitute.For<IToastNotifier>();
            var fakeBloxGen = Substitute.For<IBloxGenService>();

            var outer = AppServices.Override<IToastNotifier>(fakeToast);
            var inner = AppServices.Override<IBloxGenService>(fakeBloxGen);

            Assert.Same(fakeToast, AppServices.Toasts);
            Assert.Same(fakeBloxGen, AppServices.BloxGen);

            inner.Dispose();
            Assert.Same(fakeToast, AppServices.Toasts);
            Assert.Same(originalBloxGen, AppServices.BloxGen);

            outer.Dispose();
            Assert.Same(originalToast, AppServices.Toasts);
            Assert.Same(originalBloxGen, AppServices.BloxGen);
        }

        [Fact]
        public void Override_DisposedOutOfOrder_Throws()
        {
            var fakeToast = Substitute.For<IToastNotifier>();
            var fakeBloxGen = Substitute.For<IBloxGenService>();

            var outer = AppServices.Override<IToastNotifier>(fakeToast);
            var inner = AppServices.Override<IBloxGenService>(fakeBloxGen);

            try
            {
                Assert.Throws<InvalidOperationException>(() => outer.Dispose());
            }
            finally
            {
                // Cleanup in correct LIFO order so tests don't poison static state
                inner.Dispose();
                outer.Dispose();
            }
        }
    }
}
