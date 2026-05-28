using Microsoft.DevTunnels.Ssh.Algorithms;

namespace Eryph.GuestServices.Service.Services;

public interface IClientKeyProvider
{
    /// <summary>
    /// Decides whether <paramref name="candidate"/> is allowed to authenticate
    /// against this guest. The authorized set is the union of the locally
    /// provisioned key (if any) and the keys delivered via Hyper-V data
    /// exchange. Implementations may short-circuit — typical: return true
    /// immediately when the provisioned key matches, return false without
    /// consulting KVP when the operator has disabled the KVP-auth flag
    /// (see <c>KvpAuthEnabled</c>).
    /// </summary>
    Task<bool> IsAuthorizedAsync(IKeyPair candidate);
}
