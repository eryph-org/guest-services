using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Services;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

[ServiceActivation(ChannelRequest = EryphChannelRequestTypes.DownloadFile)]
public class DownloadFileService(SshSession session) : FileTransferServiceBase<DownloadFileRequestMessage>(session)
{
    protected override string RequestType => EryphChannelRequestTypes.DownloadFile;
    protected override FileTransferDirection Direction => FileTransferDirection.Download;
}