using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Services;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

[ServiceActivation(ChannelRequest = EryphChannelRequestTypes.UploadFile)]
public class UploadFileService(SshSession session)
    : FileServiceBase<UploadFileRequestMessage, UploadFileForwarder>(session)
{
    protected override string RequestType => EryphChannelRequestTypes.UploadFile;
    
    protected override UploadFileForwarder CreateForwarder(UploadFileRequestMessage request)
    {
        return new UploadFileForwarder(request.BasePath, request.Path, request.Length, request.Overwrite);
    }
}
