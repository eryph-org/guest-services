using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Eryph.GuestServices.Sockets;

public static partial class SocketFactory
{
    private const int AF_VSOCK = 40;
    private const int SOCK_STREAM = 1;
    private const int SOCK_CLOEXEC = 0x80000;

    public static Task<Socket> CreateServerSocket(ListenMode listenMode, Guid serviceId, int backLog)
    {
        Socket socket;
        if (OperatingSystem.IsWindows())
        {
            var listenId = listenMode switch
            {
                ListenMode.Any => HyperVAddresses.Wildcard,
                ListenMode.Children => HyperVAddresses.Children,
                ListenMode.Loopback => HyperVAddresses.Loopback,
                ListenMode.Parent => HyperVAddresses.Parent,
                _ => throw new ArgumentOutOfRangeException(nameof(listenMode), listenMode, "The listen mode is not supported")
            };

            socket = new Socket(HyperVConstants.AddressFamily, SocketType.Stream, HyperVConstants.ProtocolType);
            socket.Bind(new HyperVEndPoint(listenId, serviceId));
        }
        else if (OperatingSystem.IsLinux())
        {
            uint cid = listenMode switch
            {
                ListenMode.Any => unchecked((uint)-1),
                ListenMode.Loopback => 1,
                // The Hyper-V transport in Linux does not allow to limit who can connect to the socket.
                // According to the documentation, we must use VMADDR_CID_ANY = -1 to allow
                // connections from the host.
                // See https://github.com/torvalds/linux/blob/4b290aae788e06561754b28c6842e4080957d3f7/net/vmw_vsock/hyperv_transport.c#L113-L117.
                ListenMode.Parent => unchecked((uint)-1),
                _ => throw new ArgumentOutOfRangeException(nameof(listenMode), listenMode, "The listen mode is not supported")
            };
            var port = PortNumberConverter.ToPortNumber(serviceId); 
            socket = CreateVSockSocket();
            socket.Bind(new VSockEndpoint(cid, port));
        }
        else
        {
            throw new PlatformNotSupportedException("The current platform is not supported.");
        }

        socket.Listen(backLog);
        return Task.FromResult(socket);
    }

    public static async Task<Socket> CreateClientSocket(Guid vmId, Guid serviceId)
    {
        if (OperatingSystem.IsWindows())
        {
            var socket = new Socket(HyperVConstants.AddressFamily, SocketType.Stream, HyperVConstants.ProtocolType);
            // ConnectAsync() fails with an uninformative SocketException: "An invalid argument was supplied."
            await Task.Run(() => socket.Connect(new HyperVEndPoint(vmId, serviceId))).ConfigureAwait(false);
            return socket;
        }
        
        if (OperatingSystem.IsLinux())
        {
            if (vmId != HyperVAddresses.Loopback)
                throw new ArgumentException(
                    "Only well-known ID for a loopback connection is supported on Linux",
                    nameof(vmId));

            uint cid = 1;
            var port = PortNumberConverter.ToPortNumber(serviceId);
            var socket = CreateVSockSocket();
            await Task.Run(() => socket.Connect(new VSockEndpoint(cid, port))).ConfigureAwait(false);
            return socket;
        }

        throw new PlatformNotSupportedException("The current platform is not supported.");
    }

    [SupportedOSPlatform("linux")]
    private static Socket CreateVSockSocket()
    {
        int fd = CreateSocket(AF_VSOCK, SOCK_STREAM | SOCK_CLOEXEC, 0);
        if (fd == -1)
            throw new InvalidOperationException($"Failed to create socket. Error code: {Marshal.GetLastWin32Error()}");

        return new Socket(new SafeSocketHandle(fd, true));
    }

    [SupportedOSPlatform("linux")]
    [LibraryImport("libc", EntryPoint = "socket", SetLastError = true)]
    private static partial int CreateSocket(int domain, int type, int protocol);
}
