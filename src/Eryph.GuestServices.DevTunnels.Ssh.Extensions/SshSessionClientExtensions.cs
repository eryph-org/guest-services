using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using System.Text;
using System.Text.Json;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions;

public static class SshSessionClientExtensions
{
    public static async Task<int> TransferFileAsync(
        this SshSession session,
        string path,
        string fileName,
        Stream content,
        bool overwrite,
        CancellationToken cancellation)
    {
        var channel = await session.OpenChannelAsync(cancellation);
        var tcs = new TaskCompletionSource<SshChannelClosedEventArgs>();
        channel.Closed += (_, e) => tcs.SetResult(e);

        try
        {
            await channel.RequestAsync(
                new UploadFileRequestMessage()
                {
                    Path = path,
                    FileName = fileName,
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

        return unchecked((int)tcs.Task.Result.ExitStatus.GetValueOrDefault(0));
    }

    public static async Task<int> DownloadFileAsync(
        this SshSession session,
        string path,
        string fileName,
        Stream targetStream,
        CancellationToken cancellation)
    {
        var channel = await session.OpenChannelAsync(cancellation);
        var tcs = new TaskCompletionSource<SshChannelClosedEventArgs>();
        channel.Closed += (_, e) => tcs.SetResult(e);

        try
        {
            await channel.RequestAsync(
                new DownloadFileRequestMessage()
                {
                    Path = path,
                    FileName = fileName,
                    Length = 0, // Will be determined by server
                    Overwrite = false, // Not used for download
                },
                cancellation);
            var stream = new SshStream(channel);
            await stream.CopyToAsync(targetStream, cancellation);

            // The server will close the channel when the file is fully sent.
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
                ? $"The file download failed with signal {closedEvent.ExitSignal}."
                : $"The file download failed: {closedEvent.ErrorMessage}";

            throw new DownloadFileServerException(message);
        }

        return unchecked((int)tcs.Task.Result.ExitStatus.GetValueOrDefault(0));
    }

    public static async Task<(int result, List<RemoteFileInfo> files)> ListDirectoryAsync(
        this SshSession session,
        string path,
        CancellationToken cancellation)
    {
        var channel = await session.OpenChannelAsync(cancellation);
        var tcs = new TaskCompletionSource<SshChannelClosedEventArgs>();
        channel.Closed += (_, e) => tcs.SetResult(e);

        try
        {
            await channel.RequestAsync(
                new ListDirectoryRequestMessage()
                {
                    Path = path,
                },
                cancellation);

            var stream = new SshStream(channel);
            using var memoryStream = new MemoryStream();
            
            // Read data manually in chunks to avoid race condition
            const int bufferSize = 4096;
            var buffer = new byte[bufferSize];
            
            try
            {
                int bytesRead;
                while ((bytesRead = await stream.ReadAsync(buffer, 0, bufferSize, cancellation)) > 0)
                {
                    await memoryStream.WriteAsync(buffer, 0, bytesRead, cancellation);
                }
            }
            catch (ObjectDisposedException)
            {
                // Stream was closed by server, this is expected
            }

            // Wait for channel to close
            await tcs.Task.WaitAsync(cancellation);

            var closedEvent = tcs.Task.Result;
            if (closedEvent.ExitSignal is not null || closedEvent.ErrorMessage is not null)
            {
                var message = string.IsNullOrEmpty(closedEvent.ErrorMessage)
                    ? $"The directory listing failed with signal {closedEvent.ExitSignal}."
                    : $"The directory listing failed: {closedEvent.ErrorMessage}";

                throw new DownloadFileServerException(message);
            }

            var result = unchecked((int)closedEvent.ExitStatus.GetValueOrDefault(0));
            
            if (result == 0)
            {
                var jsonBytes = memoryStream.ToArray();
                if (jsonBytes.Length > 0)
                {
                    var json = Encoding.UTF8.GetString(jsonBytes);
                    try
                    {
                        var files = JsonSerializer.Deserialize<List<RemoteFileInfo>>(json, new JsonSerializerOptions 
                        { 
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
                        }) ?? new List<RemoteFileInfo>();
                        return (result, files);
                    }
                    catch (JsonException)
                    {
                        // Invalid JSON received
                        return (-1, new List<RemoteFileInfo>());
                    }
                }
            }
            
            return (result, new List<RemoteFileInfo>());
        }
        catch (Exception)
        {
            // The server might close the channel which will result in e.g.
            // an ObjectDisposedException
            if (!tcs.Task.IsCompleted)
                throw;

            var closedEvent = tcs.Task.Result;
            var result = unchecked((int)closedEvent.ExitStatus.GetValueOrDefault(0));
            return (result, new List<RemoteFileInfo>());
        }
    }
}
