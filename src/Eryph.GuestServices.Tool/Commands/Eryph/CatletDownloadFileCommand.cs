using Eryph.GuestServices.Tool.Commands;
using Eryph.GuestServices.Tool.Transport;
using Microsoft.DevTunnels.Ssh;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Commands.Eryph;

// egs-tool catlet download-file <CatletId> <SourcePath> <TargetPath> [--overwrite] [--identity <path>]
//
// Downloads a file from a catlet over the eryph channel, sharing the transfer
// logic with the VM-level 'download-file'.
public sealed class CatletDownloadFileCommand : GuestTransferCommand<CatletDownloadFileCommand.Settings>
{
    public sealed class Settings : EryphConnectionSettings
    {
        [CommandArgument(0, "<CatletId>")] public string CatletId { get; set; } = string.Empty;

        [CommandArgument(1, "<SourcePath>")] public string SourcePath { get; set; } = string.Empty;

        [CommandArgument(2, "<TargetPath>")] public string TargetPath { get; set; } = string.Empty;

        [CommandOption("--overwrite")] public bool Overwrite { get; set; }

        [CommandOption("--identity <PATH>")] public string? Identity { get; set; }
    }

    protected override IGuestConnector CreateConnector(Settings settings) =>
        new EryphGuestConnector(
            settings.CatletId, settings.ClientId, settings.Configuration, settings.Identity,
            writeWarning: msg => AnsiConsole.MarkupLineInterpolated($"[orange3]{msg}[/]"));

    protected override Task<int> TransferAsync(SshSession session, Settings settings) =>
        GuestFileTransfer.DownloadFileAsync(session, settings.SourcePath, settings.TargetPath, settings.Overwrite);
}
