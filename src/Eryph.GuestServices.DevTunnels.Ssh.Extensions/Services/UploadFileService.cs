using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Services;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

[ServiceActivation(ChannelRequest = EryphChannelRequestTypes.UploadFile)]
public class UploadFileService(SshSession session) : FileTransferServiceBase<UploadFileRequestMessage>(session)
{
    protected override string RequestType => EryphChannelRequestTypes.UploadFile;
    
    protected override IDisposable CreateForwarder(UploadFileRequestMessage request)
    {
        return new UploadFileForwarder(request.Path, request.FileName, request.Length, request.Overwrite);
    }
    
    protected override async Task StartForwarderAsync(IDisposable forwarder, SshStream stream, CancellationToken cancellation)
    {
        var uploadForwarder = (UploadFileForwarder)forwarder;
        await uploadForwarder.StartAsync(stream, cancellation);
    }
}
