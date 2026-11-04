#nullable enable
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net.WebSockets;

namespace Shiro.Nexus;

public interface INexusHost
{
    ObservableCollection<ControlledAccount> Accounts { get; }
    ConcurrentDictionary<WebSocket, ControlledAccount> ContextList { get; }
    void RefreshAccount(ControlledAccount account);
    void LogMessage(string message);
    void EmitMessage(string message, bool toAll);
}

public static class NexusHost
{
    public static INexusHost? Current { get; set; }
}
