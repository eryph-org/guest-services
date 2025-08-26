using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Services;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

[ServiceActivation(ChannelRequest = EryphChannelRequestTypes.UploadFile)]
public class UploadFileService(SshSession session) : FileTransferServiceBase<UploadFileRequestMessage>(session)
{
    protected override string RequestType => EryphChannelRequestTypes.UploadFile;
    protected override FileTransferDirection Direction => FileTransferDirection.Upload;
}
