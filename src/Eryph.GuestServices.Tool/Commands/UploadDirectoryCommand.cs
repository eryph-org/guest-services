using Eryph.GuestServices.Client;
using Eryph.GuestServices.Tool.Transport;
using Microsoft.DevTunnels.Ssh;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands;

public sealed class UploadDirectoryCommand : GuestTransferCommand<UploadDirectoryCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<VmId>")] public Guid VmId { get; set; }

        [CommandArgument(1, "<SourcePath>")] public string SourcePath { get; set; } = string.Empty;

        [CommandArgument(2, "<TargetPath>")] public string TargetPath { get; set; } = string.Empty;

        [CommandOption("--overwrite")] public bool Overwrite { get; set; }

        [CommandOption("--recursive")] public bool Recursive { get; set; }
    }

    protected override Task<IGuestConnector> CreateConnectorAsync(Settings settings) =>
        ToolGuestConnectors.CreateHyperVAsync(settings.VmId);

    protected override Task<int> TransferAsync(SshSession session, Settings settings) =>
        GuestFileTransfer.UploadDirectoryAsync(
            session, settings.SourcePath, settings.TargetPath, settings.Overwrite, settings.Recursive);
}
