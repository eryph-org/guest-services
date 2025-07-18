using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;

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
        var tcs = new TaskCompletionSource<SshChannelClosedEventArgs>();
        // TODO error handling for channel close
        channel.Closed += (_, e) => tcs.SetResult(e);

        try
        {
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
            await tcs.Task.WaitAsync(cancellation);
        }
        catch (Exception)
        {
            // The server might close the channel which will result in e.g.
            // an ObjectDisposedException
            if (!tcs.Task.IsCompleted)
                throw;
        }

        var closedEvent = tcs.Task.Result;
        if (closedEvent.ExitSignal is not null || closedEvent.ErrorMessage is not null)
        {
            var message = string.IsNullOrEmpty(closedEvent.ErrorMessage)
                ? $"The file transfer failed with signal {closedEvent.ExitSignal}."
                : $"The file transfer failed: {closedEvent.ErrorMessage}";

            throw new UploadFileServerException(message);
        }

        return tcs.Task.Result.ExitStatus.GetValueOrDefault(0);
    }
}
