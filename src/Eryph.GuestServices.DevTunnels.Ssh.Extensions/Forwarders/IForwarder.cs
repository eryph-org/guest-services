using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;

public interface IForwarder : IDisposable
{
    Task StartAsync(SshStream stream, CancellationToken cancellation);
}
