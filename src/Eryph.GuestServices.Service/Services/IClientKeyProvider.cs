using Microsoft.DevTunnels.Ssh.Algorithms;

namespace Eryph.GuestServices.Service.Services;

public interface IClientKeyProvider
{
    /// <summary>
    /// Decides whether <paramref name="candidate"/> is allowed to authenticate
    /// against this guest. The authorized set is the union of the locally
    /// provisioned key (if any) and the keys delivered via Hyper-V data
    /// exchange. Both sources are consulted on every call.
    /// </summary>
    Task<bool> IsAuthorizedAsync(IKeyPair candidate);
}
