#nullable enable
using System.Threading.Tasks;

namespace Shiro.Services
{
    public enum PromptSeverity
    {
        Info,
        Warning,
        Error
    }

    public interface IUserPrompt
    {
        Task<bool> ConfirmAsync(string message, string title);
        Task AlertAsync(string message, string title, PromptSeverity severity);
    }
}
