using Eryph.GuestServices.Tool.Transport;
using Microsoft.DevTunnels.Ssh;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands;

public sealed class UploadFileCommand : GuestTransferCommand<UploadFileCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<VmId>")] public Guid VmId { get; set; }

        [CommandArgument(1, "<SourcePath>")] public string SourcePath { get; set; } = string.Empty;

        [CommandArgument(2, "<TargetPath>")] public string TargetPath { get; set; } = string.Empty;

        [CommandOption("--overwrite")] public bool Overwrite { get; set; }
    }

    protected override IGuestConnector CreateConnector(Settings settings) =>
        new HyperVGuestConnector(settings.VmId);

    protected override Task<int> TransferAsync(SshSession session, Settings settings) =>
        GuestFileTransfer.UploadFileAsync(session, settings.SourcePath, settings.TargetPath, settings.Overwrite);
}
