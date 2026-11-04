using Avalonia;
using Avalonia.Headless;
using Shiro.Tests.Ui;

[assembly: AvaloniaTestApplication(typeof(HeadlessTestApp))]

namespace Shiro.Tests.Ui
{
    public class HeadlessTestApp
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<Shiro.AvaloniaUI.App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions
                {
                    UseHeadlessDrawing = true
                });
    }
}
