using Eryph.GuestServices.Tool.Commands;
using Eryph.GuestServices.Tool.Transport;
using Microsoft.DevTunnels.Ssh;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands.Eryph;

// egs-tool catlet upload-file <CatletId> <SourcePath> <TargetPath> [--overwrite] [--identity <path>]
//
// Uploads a file to a catlet over the eryph channel. Shares the transfer logic
// with the VM-level 'upload-file'; only the transport (eryph instead of the
// Hyper-V socket) differs.
public sealed class CatletUploadFileCommand : GuestTransferCommand<CatletUploadFileCommand.Settings>
{
    public sealed class Settings : EryphConnectionSettings
    {
        [CommandArgument(0, "<CatletId>")] public string CatletId { get; set; } = string.Empty;

        [CommandArgument(1, "<SourcePath>")] public string SourcePath { get; set; } = string.Empty;

        [CommandArgument(2, "<TargetPath>")] public string TargetPath { get; set; } = string.Empty;

        [CommandOption("--overwrite")] public bool Overwrite { get; set; }

        // BYOK: sign with this private key instead of the managed client key. The
        // matching public key must already be authorized in the guest.
        [CommandOption("--identity <PATH>")] public string? Identity { get; set; }
    }

    protected override IGuestConnector CreateConnector(Settings settings) =>
        new EryphGuestConnector(
            settings.CatletId, settings.ClientId, settings.Configuration, settings.Identity,
            writeWarning: msg => AnsiConsole.MarkupLineInterpolated($"[orange3]{msg}[/]"));

    protected override Task<int> TransferAsync(SshSession session, Settings settings) =>
        GuestFileTransfer.UploadFileAsync(session, settings.SourcePath, settings.TargetPath, settings.Overwrite);
}
