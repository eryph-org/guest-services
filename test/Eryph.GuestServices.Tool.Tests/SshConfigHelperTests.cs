using System.Text.RegularExpressions;
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
        SshConfigHelper.UserSshPathOverride = null;
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
        // it into multiple tokens. The selectors are quoted too (defensive). A
        // BYOK path outside the user profile cannot be '~'-anchored, but is still
        // forward-slashed so the MSYS ssh in embedded shells can open it.
        content.Should().Contain("IdentityFile \"C:/Users/Jane Doe/.ssh/id_eryph\"");
        content.Should().Contain($"ProxyCommand egs-tool.exe eryph proxy {catletId} "
            + "--configuration \"config-b\" --client-id \"client-a\"");
    }

    [Fact]
    public async Task EnsureCatletConfigAsync_KeyUnderUserProfile_EmitsTildeAnchoredForwardSlashPath()
    {
        // The managed catlet key lives under the user profile. It must be emitted
        // as a '~'-anchored, forward-slash path so the SAME config loads it under
        // both native Windows ssh.exe and the MSYS ssh of an embedded Git-Bash,
        // whose path handling rejects a 'C:\...' IdentityFile.
        var catletId = Guid.NewGuid().ToString();
        var keyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "Local", ".eryph", "guest-services", "private", "id_egs");

        await SshConfigHelper.EnsureCatletConfigAsync(catletId, "web", "default", keyPath);

        var content = await File.ReadAllTextAsync(
            Path.Combine(SshConfigHelper.CatletSshConfigPath, $"{catletId}.config"));

        content.Should().Contain(
            "IdentityFile \"~/AppData/Local/.eryph/guest-services/private/id_egs\"");
    }

    [Fact]
    public void BuildIncludeBlock_EmitsTildeAnchoredForwardSlashIncludes_ForMsysSshCompatibility()
    {
        // The config root resolves under the user profile (the LocalAppData root
        // in production, the temp override here), so both include lines must be
        // '~'-anchored with forward slashes and carry no backslash that the MSYS
        // glob() would fail to expand.
        var block = SshConfigHelper.BuildIncludeBlock();

        var includeLines = block.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("Include ", StringComparison.Ordinal))
            .ToList();

        includeLines.Should().HaveCount(2);
        includeLines.Should().OnlyContain(l => l.StartsWith("Include \"~/", StringComparison.Ordinal));
        includeLines.Should().OnlyContain(l => !l.Contains('\\'));
        includeLines.Should().Contain(l => l.EndsWith("/catlet.d/*\"", StringComparison.Ordinal));
        includeLines.Should().Contain(l => l.EndsWith("/vm.d/*\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnsureVmConfigAsync_KeyFilePath_IsForwardSlashedAndQuoted()
    {
        // The VM-config IdentityFile must be portabilized too (same fix as the
        // catlet path). The ProgramData key is outside the profile, so it is
        // forward-slashed (not '~'-anchored) and stays quoted.
        var vmId = Guid.NewGuid();

        await SshConfigHelper.EnsureVmConfigAsync(
            vmId, alias: null, @"C:\ProgramData\eryph\guest-services\private\id_egs");

        var content = await File.ReadAllTextAsync(
            Path.Combine(SshConfigHelper.VmSshConfigPath, $"{vmId}.config"));

        content.Should().Contain(
            "IdentityFile \"C:/ProgramData/eryph/guest-services/private/id_egs\"");
    }

    [Fact]
    public async Task EnsureSshConfigAsync_RewritesExistingBlock_ToPortableIncludes()
    {
        // The riskiest path: an existing ~/.ssh/config that already carries an
        // (old, backslash) eryph block is upgraded in place. The eryph block must
        // be replaced with the portable one and the surrounding user config must
        // be preserved untouched.
        var sshDir = Path.Combine(_root, "dot-ssh");
        Directory.CreateDirectory(sshDir);
        SshConfigHelper.UserSshPathOverride = sshDir;
        var configPath = Path.Combine(sshDir, "config");

        const string userBefore = "Host myserver\n    HostName 10.0.0.5\n";
        const string userAfter = "Host other\n    HostName 10.0.0.9\n";
        var staleBlock =
            "# ------ eryph guest services ------\n"
            + "Include \"C:\\Users\\Someone\\AppData\\Local\\.eryph\\guest-services\\ssh\\vm.d\\*\"\n"
            + "# ------ eryph guest services ------";
        await File.WriteAllTextAsync(configPath, $"{userBefore}{staleBlock}\n{userAfter}");

        await SshConfigHelper.EnsureSshConfigAsync();

        var result = await File.ReadAllTextAsync(configPath);
        // User stanzas on both sides survive.
        result.Should().Contain(userBefore).And.Contain(userAfter);
        // The stale backslash include is gone, replaced by the portable form.
        result.Should().NotContain("\\*\"");
        result.Should().Contain("Include \"~/")
            .And.Contain("/catlet.d/*\"").And.Contain("/vm.d/*\"");
        // Exactly one eryph block remains (two border markers).
        Regex.Matches(result, Regex.Escape("# ------ eryph guest services ------"))
            .Count.Should().Be(2);
    }

    [Fact]
    public void ToPortableSshPath_PathOutsideProfile_OnlyForwardSlashed()
    {
        // Cannot be '~'-anchored (different/absent profile), but must still drop
        // backslashes so native ssh.exe is happy and the MSYS open() can resolve it.
        SshConfigHelper.ToPortableSshPath(@"C:\ProgramData\eryph\guest-services\private\id_egs")
            .Should().Be("C:/ProgramData/eryph/guest-services/private/id_egs");
    }

    [Fact]
    public void ToPortableSshPath_PathUnderProfile_TildeAnchored()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        SshConfigHelper.ToPortableSshPath(Path.Combine(profile, "AppData", "Local", "x"))
            .Should().Be("~/AppData/Local/x");
    }

    [Fact]
    public void ToPortableSshPath_ForwardSlashPathUnderProfile_StillTildeAnchored()
    {
        // A BYOK '--identity' value may already use forward slashes; it must still
        // be recognized as profile-relative and '~'-anchored, not just slashed.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            .Replace('\\', '/');
        SshConfigHelper.ToPortableSshPath($"{profile}/.ssh/id_byok")
            .Should().Be("~/.ssh/id_byok");
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
