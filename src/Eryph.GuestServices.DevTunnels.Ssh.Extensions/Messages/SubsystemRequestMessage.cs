using Microsoft.DevTunnels.Ssh.IO;
using Microsoft.DevTunnels.Ssh.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;

public class SubsystemRequestMessage : ChannelRequestMessage
{
    public SubsystemRequestMessage()
    {
        RequestType = "Subsystem";
    }

    public string? Name { get; set; }

    protected override void OnRead(ref SshDataReader reader)
    {
        base.OnRead(ref reader);
        Name = reader.ReadString(Encoding.ASCII);
    }

    protected override void OnWrite(ref SshDataWriter writer)
    {
        base.OnWrite(ref writer);
        writer.Write(Name ?? "", Encoding.ASCII);
    }
}
