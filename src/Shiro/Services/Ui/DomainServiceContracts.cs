#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public interface IAccountWorkflowService
    {
        Task<bool> ValidateSessionAsync(Account account, CancellationToken cancellationToken = default);
    }

    public interface IBrowserService
    {
        string? FindSystemChromiumExecutable();
    }

    public interface IGameInfoService
    {
        Task<string?> ResolveUniverseIdAsync(string placeId, CancellationToken cancellationToken = default);
    }

    public interface IDiscordRpcService
    {
        bool IsEnabled { get; }
    }

    public interface IWebhookNotificationService
    {
        bool IsConfigured { get; }
    }

    public interface IAutoReconnectService
    {
        bool IsEnabled { get; }
    }

    public interface IRobloxProcessService
    {
        int GetRunningClientCount();
    }

    public interface IRobloxWindowService
    {
        void TileRunningWindows();
    }

    public interface IRobloxAudioService
    {
        bool IsMuted { get; }
    }

    public interface IAntiAfkService
    {
        bool IsEnabled { get; }
    }

    public interface IMemoryOptimizer
    {
        void TrimWorkingSets();
    }

    public interface IStreamerModeService
    {
        bool IsEnabled { get; }
    }

    public interface ILocalizationService
    {
        string CurrentLanguage { get; }
        string this[string key] { get; }
    }

    public interface IToastService
    {
        void ShowInfo(string message);
    }
}
