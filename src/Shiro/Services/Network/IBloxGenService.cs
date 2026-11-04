#nullable enable
using System.Threading;
using System.Threading.Tasks;

namespace Shiro.Services;

public interface IBloxGenService
{
    string SavedApiKey { get; set; }
    string DefaultContinent { get; set; }
    string DefaultGenType { get; set; }
    Task<BloxGenAccountResult> GenerateAccountAsync(
        string? apiKey = null,
        string? genType = null,
        string? continent = null,
        CancellationToken cancellationToken = default);
    bool ImportAccountToShiro(BloxGenAccountItem alt, string groupName = "AltGen");
}
