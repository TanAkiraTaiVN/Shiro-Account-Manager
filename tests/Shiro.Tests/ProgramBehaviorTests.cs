#nullable enable
using System;
using System.Reflection;
using System.Threading.Tasks;
using Shiro.Services;
using Xunit;

namespace Shiro.Tests
{
    [Collection("SerialGlobalState")]
    public class ProgramBehaviorTests
    {
        [Fact]
        public void Scale_EvaluatesAndClampsSettingsProperly()
        {
            // Unset or invalid
            AppSettings.General.RemoveProperty("WindowScale");
            Assert.Equal(1f, Program.Scale);

            // Valid scale within (0, 3]
            AppSettings.General.Set("WindowScale", "2.25");
            Assert.Equal(2.25f, Program.Scale);

            // Out-of-bounds scale (> 3f) resets to 1f and removes setting
            AppSettings.General.Set("WindowScale", "4.5");
            Assert.Equal(1f, Program.Scale);
            Assert.False(AppSettings.General.Exists("WindowScale"));
        }

        [Fact]
        public void ScaleFonts_ReflectsSettingOrDefault()
        {
            AppSettings.General.RemoveProperty("ScaleFonts");
            Assert.True(Program.ScaleFonts);

            AppSettings.General.Set("ScaleFonts", "false");
            Assert.False(Program.ScaleFonts);

            AppSettings.General.Set("ScaleFonts", "true");
            Assert.True(Program.ScaleFonts);

            AppSettings.General.RemoveProperty("ScaleFonts");
        }

        [Fact]
        public void BuildAvaloniaApp_ReturnsConfiguredAppBuilder()
        {
            var builder = Program.BuildAvaloniaApp();
            Assert.NotNull(builder);
            Assert.Equal(typeof(Shiro.AvaloniaUI.App), builder.ApplicationType);
        }

        [Fact]
        public void Logger_Property_ReturnsNonNull()
        {
            Assert.NotNull(Program.Logger);
            Assert.Same(Program.Logger, Program.Logger);
        }

        [Theory]
        [InlineData("Execution context was destroyed while executing script", true)]
        [InlineData("Response body is unavailable for redirect responses from server", true)]
        [InlineData("Target closed in browser context", true)]
        [InlineData("Session closed by peer", true)]
        [InlineData("Target.detachedFromTarget was received", true)]
        [InlineData("Fatal database connection timeout", false)]
        public void IsBenignTransientException_EvaluatesMessages(string message, bool expected)
        {
            var method = typeof(Program).GetMethod("IsBenignTransientException", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var ex = new InvalidOperationException(message);
            bool actual = (bool)method!.Invoke(null, new object[] { ex, message })!;
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void IsBenignTransientException_CanceledExceptions_ReturnTrue()
        {
            var method = typeof(Program).GetMethod("IsBenignTransientException", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var taskCanceled = new TaskCanceledException("task was canceled");
            bool isTaskCanceled = (bool)method!.Invoke(null, new object[] { taskCanceled, taskCanceled.Message })!;
            Assert.True(isTaskCanceled);

            var opCanceled = new OperationCanceledException("operation was canceled");
            bool isOpCanceled = (bool)method!.Invoke(null, new object[] { opCanceled, opCanceled.Message })!;
            Assert.True(isOpCanceled);
        }

        [Fact]
        public void LogUnhandled_HandlesVariousExceptionsSafely()
        {
            var method = typeof(Program).GetMethod("LogUnhandled", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            // Null exception
            var exNull = Record.Exception(() => method!.Invoke(null, new object?[] { "TestSource", null }));
            Assert.Null(exNull);

            // Transient exception
            var exTransient = Record.Exception(() => method!.Invoke(null, new object[] { "Task", new OperationCanceledException("canceled") }));
            Assert.Null(exTransient);

            // AggregateException
            var aggregate = new AggregateException("Outer aggregate", new InvalidOperationException("Inner real reason\r\nwith newlines"));
            var exAggregate = Record.Exception(() => method!.Invoke(null, new object[] { "AppDomain", aggregate }));
            Assert.Null(exAggregate);
        }
    }
}
