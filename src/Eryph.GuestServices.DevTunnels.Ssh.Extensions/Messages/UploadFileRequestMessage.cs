using Microsoft.DevTunnels.Ssh.IO;
using Microsoft.DevTunnels.Ssh.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;

public class UploadFileRequestMessage : ChannelRequestMessage
{
    public UploadFileRequestMessage()
    {
        RequestType = EryphChannelRequestTypes.UploadFile;
    }
    
    public string Path { get; set; } = "";

    public string FileName { get; set; } = "";

    public ulong Length { get; set; }

    public bool Overwrite { get; set; }

    protected override void OnRead(ref SshDataReader reader)
    {
        base.OnRead(ref reader);
        Path = reader.ReadString(Encoding.UTF8);
        FileName = reader.ReadString(Encoding.UTF8);
        Length = reader.ReadUInt64();
        Overwrite = reader.ReadBoolean();
    }

    protected override void OnWrite(ref SshDataWriter writer)
    {
        base.OnWrite(ref writer);
        writer.Write(Path, Encoding.UTF8);
        writer.Write(FileName, Encoding.UTF8);
        writer.Write(Length);
        writer.Write(Overwrite);
    }
}
