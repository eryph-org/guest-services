using AwesomeAssertions;
using Eryph.GuestServices.Tool;

namespace Eryph.GuestServices.Tool.Tests;

[Collection(nameof(SshConfigCollection))]
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
        oldHostLine!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Should().Contain($"{vmA}.hyper-v.alt")
            .And.NotContain("shared");
    }

    [Fact]
    public async Task EnsureVmConfigAsync_DoesNotStripAnotherVmsHyperVAliasOrDeleteItsConfig()
    {
        var vmA = Guid.NewGuid();
        await SshConfigHelper.EnsureVmConfigAsync(vmA, alias: null, @"C:\key");

        // Pathological: user passes VM A's unique canonical token as VM B's
        // alias. The sweep must not strip A's only identity nor delete its
        // config, which would orphan A.
        var vmB = Guid.NewGuid();
        await SshConfigHelper.EnsureVmConfigAsync(vmB, $"{vmA}.hyper-v.alt", @"C:\key");

        var aPath = Path.Combine(SshConfigHelper.VmSshConfigPath, $"{vmA}.config");
        File.Exists(aPath).Should().BeTrue();
        var aHostLine = await ReadHostLineAsync(aPath);
        aHostLine!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Should().Contain($"{vmA}.hyper-v.alt");
    }

    [Fact]
    public async Task EnsureVmConfigAsync_StripsAliasFromTabSeparatedHandEditedHostLine()
    {
        // A hand-edited config using a tab after Host and between aliases must
        // still be detected and de-duplicated.
        Directory.CreateDirectory(SshConfigHelper.VmSshConfigPath);
        var staleVm = Guid.NewGuid();
        var stalePath = Path.Combine(SshConfigHelper.VmSshConfigPath, $"{staleVm}.config");
        await File.WriteAllTextAsync(
            stalePath,
            $"Host\tshared\t{staleVm}.hyper-v.alt\n    HostName {staleVm}.hyper-v.alt\n");

        var newVm = Guid.NewGuid();
        await SshConfigHelper.EnsureVmConfigAsync(newVm, "shared", @"C:\key");

        var owners = await FindHostFilesContainingAliasAsync(SshConfigHelper.VmSshConfigPath, "shared");
        owners.Should().ContainSingle()
            .Which.Should().Be(Path.Combine(SshConfigHelper.VmSshConfigPath, $"{newVm}.config"));

        // The stale file keeps its unique token, just loses the stolen alias.
        var staleHost = await ReadHostLineAsync(stalePath);
        staleHost!.Should().Contain($"{staleVm}.hyper-v.alt");
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

    [Theory]
    [InlineData("web.eryph.alt", true)]
    [InlineData("some-vm.hyper-v.alt", true)]
    [InlineData("00000000-0000-0000-0000-000000000000.hyper-v.alt", true)]
    [InlineData("WEB.ERYPH.ALT", true)]
    [InlineData("myalias", false)]
    [InlineData("alias.local", false)]
    [InlineData("eryph.alt", false)]
    public void IsReservedAlias_DetectsGeneratedNamespaces(string alias, bool expected)
    {
        SshConfigHelper.IsReservedAlias(alias).Should().Be(expected);
    }

    [Theory]
    [InlineData("myalias")]
    [InlineData("web-01.local")]
    [InlineData("my_alias")]
    public void GetAliasValidationError_AcceptsPlainAliases(string alias)
    {
        SshConfigHelper.GetAliasValidationError(alias).Should().BeNull();
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has\ttab")]
    [InlineData("line\ninject")]
    [InlineData("a#comment")]
    [InlineData("!negated")]
    [InlineData("glob*")]
    [InlineData("question?")]
    [InlineData("web.eryph.alt")]
    [InlineData("x.hyper-v.alt")]
    public void GetAliasValidationError_RejectsInvalidAliases(string alias)
    {
        SshConfigHelper.GetAliasValidationError(alias).Should().NotBeNull();
    }

    [Fact]
    public async Task EnsureVmConfigAsync_AliasReusedWithDifferentCasing_LeavesAliasInExactlyOneFile()
    {
        var vmA = Guid.NewGuid();
        var vmB = Guid.NewGuid();

        await SshConfigHelper.EnsureVmConfigAsync(vmA, "shared", @"C:\key");
        await SshConfigHelper.EnsureVmConfigAsync(vmB, "SHARED", @"C:\key");

        var lowerOwners = await FindHostFilesContainingAliasAsync(SshConfigHelper.VmSshConfigPath, "shared");
        var upperOwners = await FindHostFilesContainingAliasAsync(SshConfigHelper.VmSshConfigPath, "SHARED");

        // Case-only difference must still be treated as the same alias.
        lowerOwners.Should().BeEmpty();
        upperOwners.Should().ContainSingle()
            .Which.Should().Be(Path.Combine(SshConfigHelper.VmSshConfigPath, $"{vmB}.config"));
    }

    [Fact]
    public async Task EnsureCatletConfigAsync_QuotesIdentityFileAndConnectionSelectors()
    {
        var catletId = Guid.NewGuid().ToString();

        await SshConfigHelper.EnsureCatletConfigAsync(
            catletId,
            "web",
            "default",
            @"C:\Users\Jane Doe\.ssh\id_eryph",
            clientId: "client-a",
            configurationName: "config-b");

        var content = await File.ReadAllTextAsync(
            Path.Combine(SshConfigHelper.CatletSshConfigPath, $"{catletId}.config"));

        // The key path can contain spaces and must be quoted, otherwise ssh splits
        // it into multiple tokens. The selectors are quoted too (defensive).
        content.Should().Contain("IdentityFile \"C:\\Users\\Jane Doe\\.ssh\\id_eryph\"");
        content.Should().Contain($"ProxyCommand egs-tool.exe eryph proxy {catletId} "
            + "--configuration \"config-b\" --client-id \"client-a\"");
    }

    [Theory]
    [InlineData("bad config", null)]
    [InlineData(null, "bad\"client")]
    [InlineData(null, @"bad\client")]
    public async Task EnsureCatletConfigAsync_UnsafeSelector_Throws(
        string? configurationName, string? clientId)
    {
        var act = () => SshConfigHelper.EnsureCatletConfigAsync(
            Guid.NewGuid().ToString(), "web", "default", @"C:\key", clientId, configurationName);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EnsureCatletConfigAsync_NoSelectors_OmitsSelectorOptions()
    {
        var catletId = Guid.NewGuid().ToString();

        await SshConfigHelper.EnsureCatletConfigAsync(
            catletId, "web", "default", @"C:\key");

        var content = await File.ReadAllTextAsync(
            Path.Combine(SshConfigHelper.CatletSshConfigPath, $"{catletId}.config"));

        content.Should().Contain($"ProxyCommand egs-tool.exe eryph proxy {catletId}");
        content.Should().NotContain("--configuration");
        content.Should().NotContain("--client-id");
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData(@"..\evil")]
    [InlineData("a/b")]
    [InlineData("has space")]
    [InlineData("")]
    public async Task EnsureCatletConfigAsync_UnsafeCatletId_Throws(string catletId)
    {
        var act = () => SshConfigHelper.EnsureCatletConfigAsync(
            catletId, "web", "default", @"C:\key");

        await act.Should().ThrowAsync<ArgumentException>();
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
            // Tokenize like production (split on any whitespace, drop the leading
            // "Host" keyword). The alias match is intentionally case-sensitive so
            // the casing tests can assert the exact surviving casing.
            if (hostLine is not null
                && hostLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1).Contains(alias))
                result.Add(file);
        }

        return result;
    }

    private static async Task<string?> ReadHostLineAsync(string file)
    {
        var lines = await File.ReadAllLinesAsync(file);
        return lines.FirstOrDefault(l =>
        {
            var trimmed = l.TrimStart();
            return trimmed.Length > 4
                && trimmed.StartsWith("Host", StringComparison.OrdinalIgnoreCase)
                && char.IsWhiteSpace(trimmed[4]);
        });
    }
}
