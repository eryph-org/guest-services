namespace Eryph.GuestServices.Tool.Transport;

// A connection or authentication failure whose message is already shaped for the
// operator. Both transports (the Hyper-V socket and the eryph channel) throw it,
// so the shared transfer command renders one consistent error without each
// transport having to know about the console. Public to match IGuestConnector,
// whose ConnectAsync contract documents this as the thrown failure type.
public sealed class GuestConnectionException(string message) : Exception(message);
