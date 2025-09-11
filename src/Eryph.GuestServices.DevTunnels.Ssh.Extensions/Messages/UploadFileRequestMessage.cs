using Microsoft.DevTunnels.Ssh.IO;
using Microsoft.DevTunnels.Ssh.Messages;
using System.Text;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;

public class UploadFileRequestMessage : ChannelRequestMessage
{
    public UploadFileRequestMessage()
    {
        RequestType = EryphChannelRequestTypes.UploadFile;
    }

    /// <summary>
    /// When the <see cref="BasePath"/> is provided,
    /// the server will combine it with the <see cref="Path"/>
    /// into a full path.
    /// </summary>
    public string BasePath { get; set; } = "";

    /// <summary>
    /// When the <see cref="BasePath"/> is provided, the <see cref="Path"/>
    /// is relative to the <see cref="BasePath"/>. In this case, the
    /// <see cref="Path"/> should use <c>/</c> as the separator.
    /// </summary>
    public string Path { get; set; } = "";

    public ulong Length { get; set; }

    public bool Overwrite { get; set; }

    protected override void OnRead(ref SshDataReader reader)
    {
        base.OnRead(ref reader);
        BasePath = reader.ReadString(Encoding.UTF8);
        Path = reader.ReadString(Encoding.UTF8);
        Length = reader.ReadUInt64();
        Overwrite = reader.ReadBoolean();
    }

    protected override void OnWrite(ref SshDataWriter writer)
    {
        base.OnWrite(ref writer);
        writer.Write(BasePath, Encoding.UTF8);
        writer.Write(Path, Encoding.UTF8);
        writer.Write(Length);
        writer.Write(Overwrite);
    }
}
