using System.Diagnostics;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Services;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

[ServiceActivation(ChannelRequest = "subsystem")]
public class SubsystemService(SshSession session) : SshService(session)
{
    protected override Task OnChannelRequestAsync(
        SshChannel channel,
        SshRequestEventArgs<ChannelRequestMessage> request,
        CancellationToken cancellation)
    {
        var subsystemRequest = request.Request.ConvertTo<SubsystemRequestMessage>();
        if (subsystemRequest.Name != "powershell")
        {
            request.ResponseTask = Task.FromResult<SshMessage>(new ChannelFailureMessage());
            return Task.CompletedTask;
        }

        var stream = new SshStream(channel);
        request.ResponseTask = Task.FromResult<SshMessage>(new ChannelSuccessMessage());
        request.ResponseContinuation = async (response) =>
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "pwsh.exe" : "/usr/bin/pwsh",
                Arguments = "-sshs -NoLogo",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();
            await Task.WhenAll(
                process.StandardOutput.BaseStream.CopyToAsync(stream, request.Cancellation),
                stream.CopyToAsync(process.StandardInput.BaseStream, request.Cancellation));
        };

        return Task.CompletedTask;
    }
}
