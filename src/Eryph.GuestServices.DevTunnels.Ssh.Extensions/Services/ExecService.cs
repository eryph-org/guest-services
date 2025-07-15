using System.Diagnostics;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Services;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

[ServiceActivation(ChannelRequest = ChannelRequestTypes.Command)]
public class ExecService(SshSession session) : SshService(session)
{
    private const int BufferSize = 8192;

    protected override Task OnChannelRequestAsync(
        SshChannel channel,
        SshRequestEventArgs<ChannelRequestMessage> request,
        CancellationToken cancellation)
    {
        var execRequest = request.Request.ConvertTo<CommandRequestMessage>();
        var splitted = execRequest.Command!.Split(' ', 2);
        var command = splitted[0];
        var arguments = splitted.Length > 1 ? splitted[1] : "";

        var stream = new SshStream(channel);
        request.ResponseTask = Task.FromResult<SshMessage>(new ChannelSuccessMessage());
        request.ResponseContinuation = async _ =>
        {
            // TODO error handling and validation

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            
            
            process.Start();

            // TODO Do we need to await or cancel these tasks?
            var outputTask = process.StandardOutput.BaseStream.CopyToAsync(stream, BufferSize, request.Cancellation);
            var inputTask = stream.CopyToAsync(process.StandardInput.BaseStream, BufferSize, request.Cancellation);

            await process.WaitForExitAsync(request.Cancellation);
            await outputTask;
            await channel.CloseAsync(unchecked((uint)process.ExitCode), request.Cancellation);
        };

        return Task.CompletedTask;
    }
}
