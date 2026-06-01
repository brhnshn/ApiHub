using System.Threading;
using System.Threading.Tasks;

namespace DockerPanel.Client.Security;

public interface IAuthTokenStore
{
    Task<string?> GetTokenAsync(CancellationToken cancellationToken = default);
    Task SetTokenAsync(string token, CancellationToken cancellationToken = default);
    Task RemoveTokenAsync(CancellationToken cancellationToken = default);
}
