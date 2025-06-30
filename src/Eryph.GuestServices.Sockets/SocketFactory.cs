using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.Sockets;

public static partial class SocketFactory
{
    private const int AF_VSOCK = 40;
    private const int SOCK_STREAM = 1;
    private const int SOCK_CLOEXEC = 0x80000;

    public static Task<Socket> CreateServerSocket(ListenMode listenMode, Guid serviceId, int backLog)
    {
        var listenId = listenMode switch
        {
            ListenMode.Any => HyperVAddresses.Wildcard,
            ListenMode.Children => HyperVAddresses.Children,
            ListenMode.Loopback => HyperVAddresses.Loopback,
            ListenMode.Parent => HyperVAddresses.Parent,
            _ => throw new ArgumentOutOfRangeException(nameof(listenMode), listenMode, "The listen mode is not supported")
        };

        Socket socket;
        if (OperatingSystem.IsWindows())
        { 
            socket = new Socket(HyperVConstants.AddressFamily, SocketType.Stream, HyperVConstants.ProtocolType);
            socket.Bind(new HyperVEndPoint(listenId, serviceId));
        }
        else if (OperatingSystem.IsLinux())
        {
            uint cid = listenMode switch
            {
                ListenMode.Any => unchecked((uint)-1),
                ListenMode.Loopback => 1,
                // TODO fix me
                ListenMode.Parent => 1,
                _ => throw new ArgumentOutOfRangeException(nameof(listenMode), listenMode, "The listen mode is not supported")
            };
            uint port = PortNumberConverter.ToPortNumber(serviceId); 
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
        var socket = new Socket(HyperVConstants.AddressFamily, SocketType.Stream, HyperVConstants.ProtocolType);
        await socket.ConnectAsync(new HyperVEndPoint(vmId, serviceId)).ConfigureAwait(false);
        return socket;
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


