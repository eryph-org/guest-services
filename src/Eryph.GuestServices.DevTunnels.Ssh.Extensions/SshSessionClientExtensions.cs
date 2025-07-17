using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Microsoft.DevTunnels.Ssh;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions;

public static class SshSessionClientExtensions
{
    public static async Task<uint> TransferFileAsync(
        this SshSession session,
        string path,
        Stream content,
        bool overwrite,
        CancellationToken cancellation)
    {
        var channel = await session.OpenChannelAsync(cancellation);
        await channel.RequestAsync(
            new UploadFileRequestMessage()
            {
                Path = path,
                Length = (ulong)content.Length,
                Overwrite = overwrite,
            },
            cancellation);
        var stream = new SshStream(channel);
        await content.CopyToAsync(stream, cancellation);

        // The server will close the channel when the file is fully written.
        // Hence, we just wait for the channel to close.
        var tcs = new TaskCompletionSource<uint?>();
        // TODO error handling for channel close
        channel.Closed += (_, e) => tcs.SetResult(e.ExitStatus);
        var result = await tcs.Task.WaitAsync(cancellation);

        return result.GetValueOrDefault(0);
    }
}
