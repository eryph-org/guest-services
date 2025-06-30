using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

[ServiceActivation(ChannelRequest = "subsystem")]
public class SubsystemService(SshSession session) : SshService(session)
{
    protected override Task OnChannelOpeningAsync(
        SshChannelOpeningEventArgs request,
        CancellationToken cancellation)
    {
        return base.OnChannelOpeningAsync(request, cancellation);
    }

    protected override Task OnChannelRequestAsync(
        SshChannel channel,
        SshRequestEventArgs<ChannelRequestMessage> request,
        CancellationToken cancellation)
    {
        var subsystemRequest = request.Request.ConvertTo<SubsystemRequestMessage>();
        var stream = new SshStream(channel);

        // TODO handle cancellation
        // TODO handle multiple subsystems
        request.ResponseTask = Task.FromResult<SshMessage>(new ChannelSuccessMessage());
        request.ResponseContinuation = async (response) =>
        {

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "pwsh.exe" : "/usr/bin/pwsh",
                    Arguments = "-sshs -NoLogo",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            process.Start();
            await Task.WhenAll(
                process.StandardOutput.BaseStream.CopyToAsync(stream, cancellation),
                stream.CopyToAsync(process.StandardInput.BaseStream, cancellation));

        };

        return Task.CompletedTask;
    }
}
