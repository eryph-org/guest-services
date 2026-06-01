using AwesomeAssertions;
using Eryph.GuestServices.Tool;

namespace Eryph.GuestServices.Tool.Tests;

public class SshConfigHelperTests : IDisposable
{
    private readonly string _root;

    public SshConfigHelperTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "egs-ssh-config-tests", Guid.NewGuid().ToString("N"));
        SshConfigHelper.RootPathOverride = _root;
    }

    public void Dispose()
    {
        SshConfigHelper.RootPathOverride = null;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
    }

    [Fact]
    public async Task EnsureVmConfigAsync_AliasReusedForAnotherVm_LeavesAliasInExactlyOneFile()
    {
        var vmA = Guid.NewGuid();
        var vmB = Guid.NewGuid();

        await SshConfigHelper.EnsureVmConfigAsync(vmA, "shared", @"C:\key");
        await SshConfigHelper.EnsureVmConfigAsync(vmB, "shared", @"C:\key");

        var owners = await FindHostFilesContainingAliasAsync(SshConfigHelper.VmSshConfigPath, "shared");

        // The alias must resolve to a single host; the most recent writer (vmB)
        // owns it. Without dedup both files carry 'Host shared' and OpenSSH
        // first-match-wins picks an arbitrary (often stale) VM.
        owners.Should().ContainSingle()
            .Which.Should().Be(Path.Combine(SshConfigHelper.VmSshConfigPath, $"{vmB}.config"));
    }

    [Fact]
    public async Task EnsureVmConfigAsync_AliasReusedForAnotherVm_OldVmKeepsItsHyperVAlias()
    {
        var vmA = Guid.NewGuid();
        var vmB = Guid.NewGuid();

        await SshConfigHelper.EnsureVmConfigAsync(vmA, "shared", @"C:\key");
        await SshConfigHelper.EnsureVmConfigAsync(vmB, "shared", @"C:\key");

        // Stripping the stolen alias must not orphan the old VM: it stays
        // reachable through its unique <vmId>.hyper-v.alt host.
        var oldHostLine = await ReadHostLineAsync(
            Path.Combine(SshConfigHelper.VmSshConfigPath, $"{vmA}.config"));
        oldHostLine.Should().NotBeNull();
        oldHostLine!.Split(' ').Should().Contain($"{vmA}.hyper-v.alt")
            .And.NotContain("shared");
    }

    [Fact]
    public async Task EnsureVmConfigAsync_AliasCollidesWithCatlet_AliasIsRemovedFromCatletConfig()
    {
        var catletVmId = Guid.NewGuid();
        // Project "default" yields the bare "<name>.eryph.alt" alias.
        await SshConfigHelper.EnsureCatletConfigAsync(
            "catlet-1", "web", "default", catletVmId, @"C:\key");

        var vmId = Guid.NewGuid();
        await SshConfigHelper.EnsureVmConfigAsync(vmId, "web.eryph.alt", @"C:\key");

        var catletOwners = await FindHostFilesContainingAliasAsync(
            SshConfigHelper.CatletSshConfigPath, "web.eryph.alt");
        var vmOwners = await FindHostFilesContainingAliasAsync(
            SshConfigHelper.VmSshConfigPath, "web.eryph.alt");

        catletOwners.Should().BeEmpty();
        vmOwners.Should().ContainSingle();
    }

    [Fact]
    public async Task EnsureVmConfigAsync_SameVmRewritten_RemainsSingleFile()
    {
        var vmId = Guid.NewGuid();

        await SshConfigHelper.EnsureVmConfigAsync(vmId, "shared", @"C:\key");
        await SshConfigHelper.EnsureVmConfigAsync(vmId, "shared", @"C:\key");

        Directory.GetFiles(SshConfigHelper.VmSshConfigPath, "*.config").Should().ContainSingle();
        var owners = await FindHostFilesContainingAliasAsync(SshConfigHelper.VmSshConfigPath, "shared");
        owners.Should().ContainSingle();
    }

    private static async Task<IReadOnlyList<string>> FindHostFilesContainingAliasAsync(
        string directory,
        string alias)
    {
        if (!Directory.Exists(directory))
            return [];

        var result = new List<string>();
        foreach (var file in Directory.GetFiles(directory, "*.config"))
        {
            var hostLine = await ReadHostLineAsync(file);
            if (hostLine is not null && hostLine.Split(' ').Contains(alias))
                result.Add(file);
        }

        return result;
    }

    private static async Task<string?> ReadHostLineAsync(string file)
    {
        var lines = await File.ReadAllLinesAsync(file);
        return lines.FirstOrDefault(l => l.StartsWith("Host ", StringComparison.Ordinal));
    }
}
