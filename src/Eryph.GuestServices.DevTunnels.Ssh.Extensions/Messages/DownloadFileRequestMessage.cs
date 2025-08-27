using Microsoft.DevTunnels.Ssh.IO;
using Microsoft.DevTunnels.Ssh.Messages;
using System.Text;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;

public class DownloadFileRequestMessage : ChannelRequestMessage, IFileTransferRequestMessage
{
    public DownloadFileRequestMessage()
    {
        RequestType = EryphChannelRequestTypes.DownloadFile;
    }
    
    public string Path { get; set; } = "";

    public string FileName { get; set; } = "";

    protected override void OnRead(ref SshDataReader reader)
    {
        base.OnRead(ref reader);
        Path = reader.ReadString(Encoding.UTF8);
        FileName = reader.ReadString(Encoding.UTF8);
    }

    protected override void OnWrite(ref SshDataWriter writer)
    {
        base.OnWrite(ref writer);
        writer.Write(Path, Encoding.UTF8);
        writer.Write(FileName, Encoding.UTF8);
    }
}