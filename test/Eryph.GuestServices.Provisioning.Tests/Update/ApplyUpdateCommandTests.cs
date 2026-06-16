using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Cli;

namespace Eryph.GuestServices.Provisioning.Tests.Update;

public sealed class ApplyUpdateCommandTests
{
    [Fact]
    public void CopyDirectory_copies_nested_tree_including_recurring_dir_names()
    {
        // ApplyUpdateCommand is [SupportedOSPlatform("windows")]; the copy logic
        // is platform-agnostic but the analyzer guards the call site.
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), "egs-copydir-" + Guid.NewGuid().ToString("N")[..8]);
        // A path where the source dir name ("bin") recurs further down — the old
        // string.Replace(source, dest) rewrite would mangle this.
        var src = Path.Combine(root, "bin");
        var dst = Path.Combine(root, "out");
        try
        {
            Directory.CreateDirectory(Path.Combine(src, "bin", "sub"));
            File.WriteAllText(Path.Combine(src, "egs-service.exe"), "top");
            File.WriteAllText(Path.Combine(src, "bin", "sub", "nested.dll"), "deep");

            ApplyUpdateCommand.CopyDirectory(src, dst);

            File.ReadAllText(Path.Combine(dst, "egs-service.exe")).Should().Be("top");
            File.ReadAllText(Path.Combine(dst, "bin", "sub", "nested.dll")).Should().Be("deep");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
