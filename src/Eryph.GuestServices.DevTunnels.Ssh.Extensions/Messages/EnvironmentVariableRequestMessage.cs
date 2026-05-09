using System.Text;
using Microsoft.DevTunnels.Ssh.IO;
using Microsoft.DevTunnels.Ssh.Messages;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;

/// <summary>
/// Channel request that conveys a single environment variable from the
/// SSH client to the server. See RFC 4254 §6.4.
/// </summary>
public class EnvironmentVariableRequestMessage : ChannelRequestMessage
{
    public EnvironmentVariableRequestMessage()
    {
        RequestType = "env";
    }

    public string Name { get; set; } = "";

    public string Value { get; set; } = "";

    protected override void OnRead(ref SshDataReader reader)
    {
        base.OnRead(ref reader);
        Name = reader.ReadString(Encoding.ASCII);
        Value = reader.ReadString(Encoding.UTF8);
    }

    protected override void OnWrite(ref SshDataWriter writer)
    {
        base.OnWrite(ref writer);
        writer.Write(Name, Encoding.ASCII);
        writer.Write(Value, Encoding.UTF8);
    }
}
