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

        [Fact]
        public void GeneralSettings_DelegatesDynamically_WhenActiveIniFileChangesAfterInit()
        {
            var prevIni = AccountManager.IniSettings;
            var prevGeneral = AccountManager.General;
            try
            {
                _ = AppServices.ServiceProvider; // Force initialization before swapping IniFile

                var iniA = new IniFile();
                iniA.Section("General").Set("ShowPresence", "false");
                AccountManager.IniSettings = iniA;
                AccountManager.General = iniA.Section("General");
                Assert.False(AppServices.GeneralSettings.Get<bool>("ShowPresence", true));
                Assert.False(AppServices.GeneralSettings.GetBool("ShowPresence", true));

                var iniB = new IniFile();
                iniB.Section("General").Set("ShowPresence", "true");
                iniB.Section("General").Set("JoinDelay", "12");
                iniB.Section("General").Set("Ratio", "1.5");
                AccountManager.IniSettings = iniB;
                AccountManager.General = iniB.Section("General");
                Assert.True(AppServices.GeneralSettings.Get<bool>("ShowPresence", false));
                Assert.True(AppServices.GeneralSettings.GetBool("ShowPresence", false));
                Assert.Equal(12, AppServices.GeneralSettings.GetInt("JoinDelay", 0));
                Assert.Equal(1.5m, AppServices.GeneralSettings.GetDecimal("Ratio", 0m));

                AppServices.GeneralSettings.Set("CustomFlag", "enabled");
                Assert.True(AppServices.GeneralSettings.Exists("CustomFlag"));
                Assert.Equal("enabled", AppServices.GeneralSettings.Get("CustomFlag"));
                Assert.Equal("enabled", iniB.Section("General").Get("CustomFlag"));

                AppServices.GeneralSettings.RemoveProperty("CustomFlag");
                Assert.False(AppServices.GeneralSettings.Exists("CustomFlag"));
                Assert.False(iniB.Section("General").Exists("CustomFlag"));
            }
            finally
            {
                AccountManager.IniSettings = prevIni;
                AccountManager.General = prevGeneral;
            }
        }

        [Fact]
        public void Override_DisposesScopedServiceProvider_OnScopeExit()
        {
            var previousProvider = AppServices.ServiceProvider;
            var mockSettings = Substitute.For<ISettingsService>();
            mockSettings.Get<bool>("ShowPresence", Arg.Any<bool>()).Returns(false);

            IServiceProvider scopedProvider;
            using (AppServices.Override<ISettingsService>(mockSettings))
            {
                scopedProvider = AppServices.ServiceProvider;
                Assert.Same(mockSettings, AppServices.GeneralSettings);
                Assert.False(AppServices.GeneralSettings.Get<bool>("ShowPresence", true));
            }

            Assert.Same(previousProvider, AppServices.ServiceProvider);
            Assert.NotSame(mockSettings, AppServices.GeneralSettings);
            Assert.Throws<ObjectDisposedException>(() => scopedProvider.GetService(typeof(ISettingsService)));
        }
    }
}
