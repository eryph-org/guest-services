namespace Eryph.GuestServices.Client;

// Establishes an authenticated guest SSH session over one specific transport
// (the Hyper-V socket or the eryph channel). The transfer commands depend only
// on this abstraction, so the copy logic runs identically no matter how the
// guest is reached. Implementations throw GuestConnectionException with a
// user-facing message when the session cannot be established.
public interface IGuestConnector
{
    Task<GuestSshConnection> ConnectAsync(CancellationToken cancellation);
}
