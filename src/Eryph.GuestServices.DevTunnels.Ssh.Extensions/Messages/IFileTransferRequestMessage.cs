namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;

public interface IFileTransferRequestMessage
{
    string Path { get; set; }
    string FileName { get; set; }
    ulong Length { get; set; }
    bool Overwrite { get; set; }
}