using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Forwarders;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Services;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Services;

[ServiceActivation(ChannelRequest = EryphChannelRequestTypes.ListDirectory)]
public class ListDirectoryService(SshSession session)
    : FileServiceBase<ListDirectoryRequestMessage, ListDirectoryForwarder>(session)
{
    protected override string RequestType => EryphChannelRequestTypes.ListDirectory;

    protected override ListDirectoryForwarder CreateForwarder(ListDirectoryRequestMessage request)
    {
        return new ListDirectoryForwarder(request.Path);
    }
}
