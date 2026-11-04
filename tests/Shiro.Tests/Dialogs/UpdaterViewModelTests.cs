#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Shiro.Services;
using Shiro.Tests.TestInfrastructure;
using Shiro.ViewModels.Dialogs;
using Xunit;

namespace Shiro.Tests.Dialogs;

public sealed class UpdaterViewModelTests
{
    [Fact]
    public async Task Start_NetworkFailure_ShowsErrorPanel_DoesNotClose()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"updater-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var handler = new FakeHttpHandler(_ => throw new HttpRequestException("DNS failure"));
            using var http = new HttpClient(handler);
            var vm = new UpdaterViewModel(http, () => new Version(4, 2, 0), tempDir, _ => { });

            bool closed = false;
            vm.CloseRequested += (_, _) => closed = true;

            await vm.StartCommand.ExecuteAsync(null);

            Assert.True(vm.HasError);
            Assert.False(closed);
            Assert.Contains("DNS failure", vm.SubStatusText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Start_NoNewer_Closes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"updater-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var handler = new FakeHttpHandler()
                .Enqueue(HttpStatusCode.OK, """{"tag_name":"v4.2.0","assets":[]}""");
            using var http = new HttpClient(handler);
            var vm = new UpdaterViewModel(http, () => new Version(4, 2, 0), tempDir, _ => { });

            bool closed = false;
            vm.CloseRequested += (_, _) => closed = true;

            await vm.StartCommand.ExecuteAsync(null);

            Assert.False(vm.HasError);
            Assert.True(closed);
            Assert.Equal("100%", vm.PercentText);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Retry_AfterError_ClearsError()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"updater-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            int attempt = 0;
            var handler = new FakeHttpHandler(_ =>
            {
                attempt++;
                if (attempt == 1)
                    throw new HttpRequestException("first fail");
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            using var http = new HttpClient(handler);
            var vm = new UpdaterViewModel(http, () => new Version(4, 2, 0), tempDir, _ => { });

            await vm.StartCommand.ExecuteAsync(null);
            Assert.True(vm.HasError);

            await vm.RetryCommand.ExecuteAsync(null);
            Assert.False(vm.HasError);
            Assert.Equal(2, attempt);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }
}
