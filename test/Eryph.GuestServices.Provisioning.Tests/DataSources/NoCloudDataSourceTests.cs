using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.UserData;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Eryph.GuestServices.Provisioning.Tests.DataSources;

public sealed class NoCloudDataSourceTests : IDisposable
{
    private readonly string _root;

    public NoCloudDataSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "egs-prov-nocloud-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // The seedfrom support reads through IUrlHelper; tests that don't exercise
    // seedfrom pass a real UrlHelper-backed substitute that is never called.
    private static NoCloudDataSource NewSource(IUrlHelper? urlHelper = null)
    {
        var probe = Substitute.For<IVolumeProbe>();
        return new NoCloudDataSource(
            probe,
            urlHelper ?? Substitute.For<IUrlHelper>(),
            NotOnAzure(),
            NullLogger<NoCloudDataSource>.Instance);
    }

    // A platform probe that reports we are NOT on Azure. Injected so the probe
    // result is independent of the host platform — an Azure-hosted CI agent
    // would otherwise flip the defensive Azure opt-out and short-circuit to
    // NotApplicable, masking the volume we just staged.
    private static IPlatformProbe NotOnAzure()
    {
        var platformProbe = Substitute.For<IPlatformProbe>();
        platformProbe.IsRunningOnAzure().Returns(false);
        return platformProbe;
    }

    [Fact]
    public async Task ReadAsync_returns_null_when_meta_data_missing()
    {
        var result = await NewSource().ReadAsync(_root, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ReadAsync_reads_all_fields_when_present()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"),
            "instance-id: i-abc-123\nlocal-hostname: my-host\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "user-data"),
            "#cloud-config\nhostname: my-host\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "network-config"),
            "version: 2\n");

        var result = await NewSource().ReadAsync(_root, CancellationToken.None);

        result.Should().NotBeNull();
        result!.SourceName.Should().Be("NoCloud");
        result.InstanceId.Should().Be("i-abc-123");
        result.Hostname.Should().Be("my-host");
        Encoding.UTF8.GetString(result.UserData!).Should().Contain("hostname: my-host");
        result.NetworkConfig.Should().Be("version: 2\n");
        result.MetaData.Should().ContainKey("instance-id");

        result.PlatformMetadata.Should().NotBeNull();
        result.PlatformMetadata!.LocalHostname.Should().Be("my-host");
        result.PlatformMetadata!.CloudName.Should().Be("nocloud");

        result.StructuredNetworkConfig.Should().NotBeNull();
        result.StructuredNetworkConfig!.Version.Should().Be(2);
    }

    [Fact]
    public async Task ReadAsync_throws_when_instance_id_missing()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"),
            "local-hostname: just-a-host\n");

        var act = async () => await NewSource().ReadAsync(_root, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReadAsync_strips_quotes_from_values()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"),
            "instance-id: \"i-quoted\"\nlocal-hostname: 'host'\n");

        var result = await NewSource().ReadAsync(_root, CancellationToken.None);

        result!.InstanceId.Should().Be("i-quoted");
        result.Hostname.Should().Be("host");
    }

    // Regression: NoCloud previously read user-data via ReadAllTextAsync, which
    // corrupts gzipped binary content (UTF-8 invalid byte 0x8B becomes EF BF BD).
    // eryph-zero's configdrive ships gzipped multipart MIME for user-data, so a
    // text round-trip destroyed the gzip header and the pipeline silently
    // dropped every cloud-config payload.
    [Fact]
    public async Task ReadAsync_PreservesGzippedUserDataBytesExactly()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"), "instance-id: i\n");
        // Gzip of "hello"
        var gzipped = new byte[] { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A,
                                   0xCB, 0x48, 0xCD, 0xC9, 0xC9, 0x07, 0x00,
                                   0x86, 0xA6, 0x10, 0x36, 0x05, 0x00, 0x00, 0x00 };
        await File.WriteAllBytesAsync(Path.Combine(_root, "user-data"), gzipped);

        var result = await NewSource().ReadAsync(_root, CancellationToken.None);

        result!.UserData.Should().Equal(gzipped);
    }

    [Fact]
    public async Task ReadAsync_carries_vendor_data_through_when_present()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"), "instance-id: i\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "vendor-data"), "vendor payload");

        var result = await NewSource().ReadAsync(_root, CancellationToken.None);

        Encoding.UTF8.GetString(result!.VendorData!).Should().Be("vendor payload");
        result.GetVendorDataBytes().Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ProbeAsync_returns_NotApplicable_when_no_volume_matches()
    {
        var probe = Substitute.For<IVolumeProbe>();
        probe.EnumerateVolumes().Returns([]);

        var source = new NoCloudDataSource(probe, Substitute.For<IUrlHelper>(), NotOnAzure(), NullLogger<NoCloudDataSource>.Instance);

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.NotApplicable>();
    }

    [Fact]
    public async Task ProbeAsync_returns_Ready_when_volume_present_and_valid()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"), "instance-id: i-1\n");

        var probe = Substitute.For<IVolumeProbe>();
        probe.EnumerateVolumes().Returns([new MountedVolume("cidata", _root)]);

        var source = new NoCloudDataSource(probe, Substitute.For<IUrlHelper>(), NotOnAzure(), NullLogger<NoCloudDataSource>.Instance);

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.Ready>();
        ((DataSourceProbeResult.Ready)result).Data.InstanceId.Should().Be("i-1");
    }

    [Fact]
    public async Task ProbeAsync_returns_Failed_when_meta_data_is_malformed()
    {
        // No instance-id present — ReadAsync throws InvalidDataException, which the
        // probe must surface as Failed (not bubble out).
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"), "local-hostname: x\n");

        var probe = Substitute.For<IVolumeProbe>();
        probe.EnumerateVolumes().Returns([new MountedVolume("cidata", _root)]);

        var source = new NoCloudDataSource(probe, Substitute.For<IUrlHelper>(), NotOnAzure(), NullLogger<NoCloudDataSource>.Instance);

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.Failed>();
    }

    [Fact]
    public async Task OnCompletedAsync_is_a_noop()
    {
        var source = NewSource();

        var act = async () => await source.OnCompletedAsync(
            new DataSourceResult { SourceName = "NoCloud", InstanceId = "x" },
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ---- Finding 18: real YAML meta-data parsing -------------------------

    [Fact]
    public async Task ReadAsync_preserves_nested_public_keys_map()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"),
            "instance-id: iid-local01\n" +
            "local-hostname: myhost\n" +
            "public-keys:\n" +
            "  mykey: ssh-rsa AAAA...\n");

        var result = await NewSource().ReadAsync(_root, CancellationToken.None);

        // Flat scalars still extract exactly as before.
        result!.InstanceId.Should().Be("iid-local01");
        result.Hostname.Should().Be("myhost");
        // The nested map is preserved (as serialized text), not dropped.
        result.MetaData.Should().ContainKey("public-keys");
        result.MetaData["public-keys"].Should().Contain("mykey");
        result.MetaData["public-keys"].Should().Contain("ssh-rsa AAAA...");
        // ...and the key value is extracted for the default user (cloud-init's
        // get_public_ssh_keys() / normalize_pubkey_data), not just kept as text.
        result.SshPublicKeys.Should().ContainSingle().Which.Should().Be("ssh-rsa AAAA...");
    }

    [Fact]
    public async Task ReadAsync_preserves_block_scalar_without_crashing()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"),
            "instance-id: iid-local01\n" +
            "network-interfaces: |\n" +
            "  iface eth0 inet static\n" +
            "  address 10.0.0.2\n");

        var result = await NewSource().ReadAsync(_root, CancellationToken.None);

        result!.InstanceId.Should().Be("iid-local01");
        result.MetaData.Should().ContainKey("network-interfaces");
        result.MetaData["network-interfaces"].Should().Contain("iface eth0 inet static");
        result.MetaData["network-interfaces"].Should().Contain("address 10.0.0.2");
    }

    [Fact]
    public async Task ReadAsync_flat_scalars_behave_identically_to_legacy_parser()
    {
        // Regression: flat-only meta-data must keep producing the same dictionary
        // shape and the same extracted instance-id / hostname as the old line
        // splitter, so existing NoCloud seeds are unaffected by the YAML swap.
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"),
            "instance-id: i-flat-1\nlocal-hostname: flathost\n# a comment\n");

        var result = await NewSource().ReadAsync(_root, CancellationToken.None);

        result!.InstanceId.Should().Be("i-flat-1");
        result.Hostname.Should().Be("flathost");
        result.MetaData["instance-id"].Should().Be("i-flat-1");
        result.MetaData["local-hostname"].Should().Be("flathost");
        result.MetaData.Should().NotContainKey("# a comment");
    }

    // Real-world fixture: representative cidata meta-data modeled on cloud-init's
    // documented NoCloud datasource shape (see test/fixtures/nocloud/README.md).
    [Fact]
    public async Task ReadAsync_parses_real_world_nocloud_fixture()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "nocloud", "meta-data");
        File.Exists(fixturePath).Should().BeTrue("the NoCloud meta-data fixture should be copied to the output");
        File.Copy(fixturePath, Path.Combine(_root, "meta-data"));

        var result = await NewSource().ReadAsync(_root, CancellationToken.None);

        result!.InstanceId.Should().Be("iid-local01");
        result.Hostname.Should().Be("cloudimg");
        result.MetaData.Should().ContainKey("public-keys");
        result.MetaData["public-keys"].Should().Contain("mykey");
        result.MetaData["public-keys"].Should().Contain("otherkey");
        result.MetaData.Should().ContainKey("network-interfaces");
        result.MetaData["network-interfaces"].Should().Contain("iface eth0 inet static");
        // Both keys are extracted in document order (mykey then otherkey).
        // Use Equal, not BeEquivalentTo: extraction is order-preserving and we
        // want a regression to fail if that ordering ever changes.
        result.SshPublicKeys.Should().Equal(
            "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQDexamplekeydata mykey@host",
            "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleEd25519KeyData other@host");
    }

    [Fact]
    public async Task ReadAsync_extracts_public_keys_from_a_list()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"),
            "instance-id: iid-local01\n" +
            "public-keys:\n" +
            "  - ssh-rsa AAAAkeyone one@host\n" +
            "  - ssh-ed25519 AAAAkeytwo two@host\n");

        var result = await NewSource().ReadAsync(_root, CancellationToken.None);

        result!.SshPublicKeys.Should().Equal(
            "ssh-rsa AAAAkeyone one@host",
            "ssh-ed25519 AAAAkeytwo two@host");
    }

    [Fact]
    public async Task ReadAsync_extracts_a_single_scalar_public_key()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"),
            "instance-id: iid-local01\n" +
            "public-keys: ssh-rsa AAAAonlykey only@host\n");

        var result = await NewSource().ReadAsync(_root, CancellationToken.None);

        result!.SshPublicKeys.Should().ContainSingle()
            .Which.Should().Be("ssh-rsa AAAAonlykey only@host");
    }

    [Fact]
    public async Task ReadAsync_leaves_ssh_keys_null_when_no_public_keys()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"),
            "instance-id: iid-local01\nlocal-hostname: h\n");

        var result = await NewSource().ReadAsync(_root, CancellationToken.None);

        result!.SshPublicKeys.Should().BeNull();
    }

    [Theory]
    // Map whose value is itself a list (cloud-init normalize_pubkey_data dict form).
    [InlineData("name:\n- ssh-rsa A\n- ssh-rsa B", new[] { "ssh-rsa A", "ssh-rsa B" })]
    // Plain newline-joined scalar.
    [InlineData("ssh-rsa A\nssh-rsa B", new[] { "ssh-rsa A", "ssh-rsa B" })]
    // Duplicates are dropped, order preserved.
    [InlineData("- ssh-rsa A\n- ssh-rsa A\n- ssh-rsa B", new[] { "ssh-rsa A", "ssh-rsa B" })]
    public void ExtractPublicKeys_normalizes_the_cloud_init_forms(string raw, string[] expected)
    {
        NoCloudDataSource.ExtractPublicKeys(raw).Should().Equal(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractPublicKeys_returns_empty_for_blank(string? raw)
    {
        NoCloudDataSource.ExtractPublicKeys(raw).Should().BeEmpty();
    }

    // ---- Finding 17: seedfrom support -----------------------------------

    [Fact]
    public async Task ReadAsync_seedfrom_file_url_lets_seed_win()
    {
        // Local cidata with a seedfrom pointer at a sibling seed directory.
        var seedDir = Path.Combine(_root, "seed");
        Directory.CreateDirectory(seedDir);
        await File.WriteAllTextAsync(Path.Combine(seedDir, "meta-data"), "instance-id: seed-iid\n");
        await File.WriteAllTextAsync(Path.Combine(seedDir, "user-data"), "#cloud-config\nfrom: seed\n");

        var seedUrl = new Uri(seedDir + Path.DirectorySeparatorChar).AbsoluteUri; // file:///.../seed/
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"),
            $"instance-id: local-iid\nseedfrom: {seedUrl}\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "user-data"), "#cloud-config\nfrom: local\n");

        // Use a real file:// fetcher so the integration-style path is exercised.
        var urlHelper = new FileUrlHelper();
        var result = await NewSource(urlHelper).ReadAsync(_root, CancellationToken.None);

        // Seed's instance-id wins; seed's user-data lands in the result.
        result!.InstanceId.Should().Be("seed-iid");
        Encoding.UTF8.GetString(result.UserData!).Should().Contain("from: seed");
    }

    [Fact]
    public async Task ReadAsync_seedfrom_unreachable_falls_back_to_local()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"),
            "instance-id: local-iid\nlocal-hostname: localhost\nseedfrom: https://unreachable.invalid/seed/\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "user-data"), "#cloud-config\nfrom: local\n");

        var urlHelper = Substitute.For<IUrlHelper>();
        urlHelper.FetchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<byte[]>>(_ => throw new HttpRequestException("unreachable"));

        var result = await NewSource(urlHelper).ReadAsync(_root, CancellationToken.None);

        // Datasource still returns, using the local files.
        result!.InstanceId.Should().Be("local-iid");
        result.Hostname.Should().Be("localhost");
        Encoding.UTF8.GetString(result.UserData!).Should().Contain("from: local");
    }

    [Fact]
    public async Task ReadAsync_nested_seedfrom_does_not_recurse()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"),
            "instance-id: local-iid\nseedfrom: https://seed.example/seed/\n");

        var urlHelper = Substitute.For<IUrlHelper>();
        urlHelper.FetchAsync("https://seed.example/seed/meta-data", Arg.Any<CancellationToken>())
            // The seed's own meta-data also carries a seedfrom — must NOT recurse.
            .Returns(Task.FromResult(Encoding.UTF8.GetBytes(
                "instance-id: seed-iid\nseedfrom: https://other.example/again/\n")));
        urlHelper.FetchAsync("https://seed.example/seed/user-data", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Encoding.UTF8.GetBytes("#cloud-config\nfrom: seed\n")));
        urlHelper.FetchAsync("https://seed.example/seed/vendor-data", Arg.Any<CancellationToken>())
            .Returns<Task<byte[]>>(_ => throw new HttpRequestException("404"));
        urlHelper.FetchAsync("https://seed.example/seed/network-config", Arg.Any<CancellationToken>())
            .Returns<Task<byte[]>>(_ => throw new HttpRequestException("404"));

        var result = await NewSource(urlHelper).ReadAsync(_root, CancellationToken.None);

        result!.InstanceId.Should().Be("seed-iid");
        // The nested seedfrom must have been dropped, not followed.
        await urlHelper.DidNotReceive().FetchAsync(
            "https://other.example/again/meta-data", Arg.Any<CancellationToken>());
        result.MetaData.Should().NotContainKey("seedfrom");
    }

    [Fact]
    public async Task ReadAsync_seedfrom_http_user_data_lands_in_result()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "meta-data"),
            "instance-id: local-iid\nseedfrom: https://seed.example/seed/\n");

        var urlHelper = Substitute.For<IUrlHelper>();
        urlHelper.FetchAsync("https://seed.example/seed/meta-data", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Encoding.UTF8.GetBytes("instance-id: seed-iid\nlocal-hostname: seedhost\n")));
        urlHelper.FetchAsync("https://seed.example/seed/user-data", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Encoding.UTF8.GetBytes("#cloud-config\nfrom: seed-http\n")));
        urlHelper.FetchAsync("https://seed.example/seed/vendor-data", Arg.Any<CancellationToken>())
            .Returns<Task<byte[]>>(_ => throw new HttpRequestException("404"));
        urlHelper.FetchAsync("https://seed.example/seed/network-config", Arg.Any<CancellationToken>())
            .Returns<Task<byte[]>>(_ => throw new HttpRequestException("404"));

        var result = await NewSource(urlHelper).ReadAsync(_root, CancellationToken.None);

        result!.InstanceId.Should().Be("seed-iid");
        result.Hostname.Should().Be("seedhost");
        Encoding.UTF8.GetString(result.UserData!).Should().Contain("from: seed-http");
    }

    // Minimal real file:// fetcher for the seedfrom integration test. Mirrors
    // the production UrlHelper's file branch (read bytes from the local path)
    // without pulling in its HttpClient construction.
    private sealed class FileUrlHelper : IUrlHelper
    {
        public async Task<byte[]> FetchAsync(string url, CancellationToken cancellationToken)
        {
            var uri = new Uri(url);
            if (!uri.IsFile)
                throw new InvalidOperationException($"FileUrlHelper only handles file:// URLs, got '{url}'.");
            return await File.ReadAllBytesAsync(uri.LocalPath, cancellationToken);
        }
    }
}
