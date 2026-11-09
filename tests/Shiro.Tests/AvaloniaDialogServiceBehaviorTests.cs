#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class AvaloniaDialogServiceBehaviorTests : IDisposable
    {
        private readonly Action<Action>? _originalDispatcher;

        public AvaloniaDialogServiceBehaviorTests()
        {
            _originalDispatcher = AppDispatcher.CustomDispatcher;
        }

        public void Dispose()
        {
            AppDispatcher.CustomDispatcher = _originalDispatcher;
        }

        [Fact]
        public void Default_Instance_IsNotNullAndSingleton()
        {
            var inst1 = AvaloniaDialogService.Default;
            var inst2 = AvaloniaDialogService.Default;

            Assert.NotNull(inst1);
            Assert.Same(inst1, inst2);
        }

        [Fact]
        public void MessageMethods_PostToAppDispatcher()
        {
            var postedActions = new List<Action>();
            AppDispatcher.CustomDispatcher = action => postedActions.Add(action);

            var service = AvaloniaDialogService.Default;

            service.ShowInfo("Info message", "Info Title");
            service.ShowWarning("Warning message", "Warning Title");
            service.ShowError("Error message", "Error Title");

            Assert.Equal(3, postedActions.Count);
        }

        [Fact]
        public void OpenDialogMethods_PostExpectedActionsToDispatcher()
        {
            var postedActions = new List<Action>();
            AppDispatcher.CustomDispatcher = action => postedActions.Add(action);

            var service = AvaloniaDialogService.Default;
            var account = new Account { Username = "TestUser", UserID = 1111 };

            service.OpenFastLoginDialog(account);
            service.OpenFastLoginDialog(null);
            service.OpenFastLoginDialogBatch(new[] { account });
            service.OpenBulkImportDialog();
            service.OpenAltGenDialog();
            service.OpenProxyManagerDialog();
            service.OpenAboutDialog();
            service.OpenAccountFieldsDialog(account);
            service.OpenAccountUtilsDialog(account);
            service.OpenMissingAssetsDialog(account, new long[] { 101, 102 });

            Assert.Equal(10, postedActions.Count);
        }

        [Fact]
        public void OpenAccountDialogs_NullAccountGuards_DoNotPostToDispatcher()
        {
            var postedActions = new List<Action>();
            AppDispatcher.CustomDispatcher = action => postedActions.Add(action);

            var service = AvaloniaDialogService.Default;

            service.OpenAccountFieldsDialog(null!);
            service.OpenAccountUtilsDialog(null!);
            service.OpenMissingAssetsDialog(null!, new long[] { 123 });

            Assert.Empty(postedActions);
        }

        [Fact]
        public async Task AppDispatcher_CustomDispatcher_InterceptsPostAndBackground()
        {
            int postCount = 0;
            AppDispatcher.CustomDispatcher = action =>
            {
                postCount++;
                action();
            };

            bool postRan = false;
            AppDispatcher.Post(() => postRan = true);
            Assert.True(postRan);

            bool bgRan = false;
            AppDispatcher.PostBackground(() => bgRan = true);
            Assert.True(bgRan);

            Assert.Equal(2, postCount);

            // Null action should not throw or invoke dispatcher
            AppDispatcher.Post(null!);
            AppDispatcher.PostBackground(null!);
            Assert.Equal(2, postCount);
        }

        [Fact]
        public async Task AppDispatcher_InvokeAsync_ExecutesAndReturnsValues()
        {
            AppDispatcher.CustomDispatcher = null;

            int computed = await AppDispatcher.InvokeAsync(() => 42 * 2);
            Assert.Equal(84, computed);

            string? stringResult = await AppDispatcher.InvokeAsync(() => "Hello from Dispatcher");
            Assert.Equal("Hello from Dispatcher", stringResult);

            bool actionInvoked = false;
            await AppDispatcher.InvokeAsync(() => actionInvoked = true);
            Assert.True(actionInvoked);

            Func<int>? nullFunc = null;
            int nullResult = await AppDispatcher.InvokeAsync(nullFunc!);
            Assert.Equal(0, nullResult);

            Action? nullAction = null;
            var exception = await Record.ExceptionAsync(() => AppDispatcher.InvokeAsync(nullAction!));
            Assert.Null(exception);
        }
    }
}
