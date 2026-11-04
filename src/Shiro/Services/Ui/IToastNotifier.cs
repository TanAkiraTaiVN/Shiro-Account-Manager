#nullable enable

namespace Shiro.Services;

public interface IToastNotifier
{
    void ShowSuccess(string title, string message);
    void ShowError(string title, string message);
}

public sealed class ToastNotifierService : IToastNotifier
{
    public static ToastNotifierService Default { get; } = new();

    public void ShowSuccess(string title, string message) => ToastService.ShowSuccess(title, message);
    public void ShowError(string title, string message) => ToastService.ShowError(title, message);
}
