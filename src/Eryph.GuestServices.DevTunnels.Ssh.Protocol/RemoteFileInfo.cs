namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions;

public class RemoteFileInfo
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
}