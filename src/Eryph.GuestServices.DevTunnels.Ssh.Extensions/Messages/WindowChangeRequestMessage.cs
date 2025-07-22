using Microsoft.DevTunnels.Ssh.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.DevTunnels.Ssh.IO;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;

public class WindowChangeRequestMessage : ChannelRequestMessage
{
    public WindowChangeRequestMessage()
    {
        RequestType = "window-change";
    }
    public uint Width { get; set; }

    public uint Height { get; set; }

    public uint WidthPixels { get; set; }

    public uint HeightPixels { get; set; }

    protected override void OnRead(ref SshDataReader reader)
    {
        base.OnRead(ref reader);
        Width = reader.ReadUInt32();
        Height = reader.ReadUInt32();
        WidthPixels = reader.ReadUInt32();
        HeightPixels = reader.ReadUInt32();
    }
    protected override void OnWrite(ref SshDataWriter writer)
    {
        base.OnWrite(ref writer);
        writer.Write(Width);
        writer.Write(Height);
        writer.Write(WidthPixels);
        writer.Write(HeightPixels);
    }
}
