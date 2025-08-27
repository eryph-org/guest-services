namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;

public interface IFileTransferRequestMessage
{
    string Path { get; set; }
    string FileName { get; set; }
}