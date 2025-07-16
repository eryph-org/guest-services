using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Messages;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions;

public static class SshSessionClientExtensions
{
    public static async Task TransferFileAsync(
        this SshSession session,
        string path,
        Stream content,
        CancellationToken cancellation)
    {
        var channel = await session.OpenChannelAsync(cancellation);
        await channel.RequestAsync(new FileTransferRequestMessage()
        {
            Path = path,
            Length = (ulong)content.Length,
        }, cancellation);
        var stream = new SshStream(channel);
        await content.CopyToAsync(stream, cancellation);

        var tcs = new TaskCompletionSource();

        channel.Closed += (s, e) => tcs.SetResult();

        await tcs.Task;
    }
}
