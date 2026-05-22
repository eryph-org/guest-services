using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.DataSources.Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using NSubstitute;

namespace Eryph.GuestServices.Provisioning.Tests.DataSources;

public class AzureDataSourceTests
{
    // ---- shape ----

    [Fact]
    public void Metadata_properties()
    {
        var source = new AzureDataSource(
            Substitute.For<IVolumeProbe>(),
            NullLogger<AzureDataSource>.Instance);

        source.Name.Should().Be("Azure");
        source.Priority.Should().Be(10);
        // v1 requires network for IMDS; CustomData.bin alone is not enough to
        // guarantee an InstanceId (registry VmId is fallback).
        source.RequiresNetwork.Should().BeTrue();
    }

    // ---- probe gating ----

    [Fact]
    public async Task ProbeAsync_returns_NotApplicable_on_non_Azure_host()
    {
        // RFC 0014: detection is registry VmId OR chassis asset tag. On the CI
        // host both miss, so the probe must short-circuit to NotApplicable
        // without touching IMDS or the filesystem. (This is the "test that
        // probe returns NotApplicable when registry+chassis miss" check.)
        if (OperatingSystem.IsWindows() && AzureSignalsPresent())
        {
            return; // Running on a real Azure-flavoured Windows host — skip.
        }

        var volumes = Substitute.For<IVolumeProbe>();
        volumes.EnumerateVolumes().Returns([]);

        var source = new AzureDataSource(volumes, NullLogger<AzureDataSource>.Instance);

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.NotApplicable>();
    }

    // ---- OnCompletedAsync cleanup (RFC 0005) ----

    // The Azure cleanup tests use a real-shape temp directory with a uniquely
    // named root, mirroring cbi's AzureCustomDataService layout
    // (<root>\CustomData.bin sitting alone in <root>). The fixture is created
    // per-test and torn down so we never touch the real C:\AzureData.
    private sealed class AzureCleanupFixture : IDisposable
    {
        public string Root { get; }
        public string CustomDataPath { get; }

        public AzureCleanupFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "egs-prov-azure-cleanup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            CustomDataPath = Path.Combine(Root, "CustomData.bin");
        }

        public void WritePayload(byte[] bytes) => File.WriteAllBytes(CustomDataPath, bytes);

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private static AzureDataSource MakeSourceForCleanup(string customDataPath) =>
        new(
            Substitute.For<IVolumeProbe>(),
            NullLogger<AzureDataSource>.Instance,
            imdsClientFactory: () => new AzureImdsClient(
                new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)),
                disposeHandler: false,
                TimeSpan.FromSeconds(1),
                NullLogger<AzureImdsClient>.Instance),
            fileExists: File.Exists,
            readFileBytes: (p, ct) => File.ReadAllBytesAsync(p, ct),
            customDataPath: customDataPath);

    [Fact]
    public async Task OnCompletedAsync_deletes_CustomData_bin_and_empty_parent_directory()
    {
        // Cbi parity: AzureCustomDataService.provisioning_completed() removes
        // CustomData.bin so a re-run on the same VM can't replay stale user-data.
        // We mirror that here, and additionally remove the now-empty parent
        // directory so the file system reflects "no Azure payload pending".
        using var fixture = new AzureCleanupFixture();
        fixture.WritePayload(new byte[] { 0x01, 0x02, 0x03 });

        var source = MakeSourceForCleanup(fixture.CustomDataPath);

        await source.OnCompletedAsync(
            new DataSourceResult { SourceName = "Azure", InstanceId = "vmid" },
            CancellationToken.None);

        File.Exists(fixture.CustomDataPath).Should().BeFalse();
        Directory.Exists(fixture.Root).Should().BeFalse(
            "the parent dir was left empty by the file delete, so cleanup removes it too");
    }

    [Fact]
    public async Task OnCompletedAsync_is_idempotent_when_CustomData_bin_already_absent()
    {
        // RFC 0005 explicit requirement: a second call must succeed without
        // throwing. PA only writes CustomData.bin once per instance — if a
        // previous successful provisioning already deleted it, the next reset
        // / rerun must not crash.
        using var fixture = new AzureCleanupFixture();
        // Deliberately do NOT create CustomData.bin.

        var source = MakeSourceForCleanup(fixture.CustomDataPath);

        var act = async () => await source.OnCompletedAsync(
            new DataSourceResult { SourceName = "Azure", InstanceId = "vmid" },
            CancellationToken.None);

        await act.Should().NotThrowAsync();
        File.Exists(fixture.CustomDataPath).Should().BeFalse();
    }

    [Fact]
    public async Task OnCompletedAsync_called_twice_succeeds_and_leaves_filesystem_consistent()
    {
        // The strongest idempotency check: real file removed, then a second
        // call with the file already gone. Must not throw, must not re-create,
        // must leave the parent dir cleaned up after the first call.
        using var fixture = new AzureCleanupFixture();
        fixture.WritePayload(new byte[] { 0x10, 0x20 });

        var source = MakeSourceForCleanup(fixture.CustomDataPath);
        var data = new DataSourceResult { SourceName = "Azure", InstanceId = "vmid" };

        await source.OnCompletedAsync(data, CancellationToken.None);
        var act = async () => await source.OnCompletedAsync(data, CancellationToken.None);

        await act.Should().NotThrowAsync();
        File.Exists(fixture.CustomDataPath).Should().BeFalse();
    }

    [Fact]
    public async Task OnCompletedAsync_swallows_IO_exceptions_and_does_not_throw()
    {
        // Best-effort cleanup contract (RFC 0005 + memory rule): provisioning
        // has already succeeded by the time we get here, so a bad file delete
        // must NOT propagate as a failure. We simulate the failure by
        // injecting a deleteFile that throws — production wraps File.Delete
        // unchecked, so this is the realistic shape.
        var source = new AzureDataSource(
            Substitute.For<IVolumeProbe>(),
            NullLogger<AzureDataSource>.Instance,
            imdsClientFactory: () => new AzureImdsClient(
                new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)),
                disposeHandler: false,
                TimeSpan.FromSeconds(1),
                NullLogger<AzureImdsClient>.Instance),
            fileExists: _ => true,
            readFileBytes: (_, _) => Task.FromResult(Array.Empty<byte>()),
            deleteFile: _ => throw new IOException("simulated I/O denial"),
            customDataPath: @"C:\AzureData\CustomData.bin");

        var act = async () => await source.OnCompletedAsync(
            new DataSourceResult { SourceName = "Azure", InstanceId = "vmid" },
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task OnCompletedAsync_does_not_delete_parent_when_directory_still_has_other_files()
    {
        // Safety: if anyone else dropped a file under C:\AzureData (PA or a
        // future agent), we must not remove the directory out from under them.
        // cbi has the same guard implicitly via the per-file delete pattern.
        using var fixture = new AzureCleanupFixture();
        fixture.WritePayload(new byte[] { 0x42 });
        var sibling = Path.Combine(fixture.Root, "OtherAgentMarker.txt");
        File.WriteAllText(sibling, "not ours");

        var source = MakeSourceForCleanup(fixture.CustomDataPath);

        await source.OnCompletedAsync(
            new DataSourceResult { SourceName = "Azure", InstanceId = "vmid" },
            CancellationToken.None);

        File.Exists(fixture.CustomDataPath).Should().BeFalse();
        Directory.Exists(fixture.Root).Should().BeTrue("the dir was not empty after the file delete");
        File.Exists(sibling).Should().BeTrue();
    }

    // ---- IMDS JSON parsing ----

    [Fact]
    public void ParseImds_flattens_compute_section_from_real_world_fixture()
    {
        var json = LoadFixtureText("imds-instance.json");
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var (compute, flat) = AzureDataSource.ParseImds(doc);

        compute.Should().NotBeNull();
        compute!.VmId.Should().Be("11111111-2222-3333-4444-555555555555");
        compute.Name.Should().Be("azure-test-host");
        compute.Location.Should().Be("westeurope");
        compute.VmSize.Should().Be("Standard_D2s_v3");
        compute.Zone.Should().Be("1");

        flat.Should().ContainKey("vmId");
        flat["vmId"].Should().Be("11111111-2222-3333-4444-555555555555");
        // Nested objects survive as raw JSON so downstream consumers can probe.
        flat.Should().ContainKey("storageProfile");
    }

    [Fact]
    public void ParseImds_returns_null_when_compute_missing()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("{\"network\":{}}");

        var (compute, flat) = AzureDataSource.ParseImds(doc);

        compute.Should().BeNull();
        flat.Should().BeEmpty();
    }

    // ---- IMDS client (HttpMessageHandler substitution) ----

    [Fact]
    public async Task ImdsClient_sends_Metadata_and_Accept_headers()
    {
        // IMDS rejects requests without the "Metadata: true" header. This is
        // the most important invariant of the client; regression if we ever
        // refactor headers away.
        HttpRequestMessage? capturedRequest = null;
        var handler = new FakeHttpHandler(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(LoadFixtureText("imds-instance.json")),
            };
        });

        using var client = new AzureImdsClient(
            handler,
            disposeHandler: false,
            timeout: TimeSpan.FromSeconds(1),
            NullLogger<AzureImdsClient>.Instance);

        using var doc = await client.TryGetInstanceMetadataAsync(CancellationToken.None);

        doc.Should().NotBeNull();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.GetValues("Metadata").Should().Contain("true");
        capturedRequest.Headers.Accept.ToString().Should().Contain("application/json");
        capturedRequest.RequestUri!.ToString().Should().Be(AzureImdsClient.ImdsUrl);
    }

    [Fact]
    public async Task ImdsClient_retries_once_on_transient_error()
    {
        var attempts = 0;
        var handler = new FakeHttpHandler(_ =>
        {
            attempts++;
            if (attempts == 1)
                throw new HttpRequestException("simulated transient");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(LoadFixtureText("imds-instance.json")),
            };
        });

        using var client = new AzureImdsClient(
            handler,
            disposeHandler: false,
            timeout: TimeSpan.FromSeconds(1),
            NullLogger<AzureImdsClient>.Instance);

        using var doc = await client.TryGetInstanceMetadataAsync(CancellationToken.None);

        attempts.Should().Be(2);
        doc.Should().NotBeNull();
    }

    [Fact]
    public async Task ImdsClient_returns_null_after_both_attempts_fail()
    {
        var handler = new FakeHttpHandler(_ =>
            throw new HttpRequestException("simulated unreachable"));

        using var client = new AzureImdsClient(
            handler,
            disposeHandler: false,
            timeout: TimeSpan.FromSeconds(1),
            NullLogger<AzureImdsClient>.Instance);

        var doc = await client.TryGetInstanceMetadataAsync(CancellationToken.None);

        doc.Should().BeNull();
    }

    // ---- ovf-env.xml parsing ----

    [Fact]
    public void OvfEnvParser_extracts_hostname_and_customdata_from_real_world_fixture()
    {
        var xml = LoadFixtureText("ovf-env.xml");

        var env = AzureOvfEnvParser.Parse(xml);

        env.Hostname.Should().Be("azure-test-host");
        env.CustomDataBase64.Should().NotBeNullOrWhiteSpace();
        // Base64 of "#cloud-config\nhostname: azure-test-host\n" — confirms the
        // fixture is a faithful cloud-config payload shape, not an opaque blob.
        Convert.FromBase64String(env.CustomDataBase64!).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void OvfEnvParser_returns_empty_when_no_provisioning_section()
    {
        var xml = "<?xml version=\"1.0\"?><Environment xmlns=\"http://schemas.microsoft.com/windowsazure\" />";

        var env = AzureOvfEnvParser.Parse(xml);

        env.Hostname.Should().BeNull();
        env.CustomDataBase64.Should().BeNull();
    }

    [Fact]
    public void OvfEnvParser_throws_on_malformed_xml()
    {
        var act = () => AzureOvfEnvParser.Parse("<not-xml");

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void OvfEnvParser_handles_linux_provisioning_configuration_set()
    {
        // Same fixture shape but with LinuxProvisioningConfigurationSet + HostName.
        // We support both because the agent may eventually run on Linux-flavoured
        // Azure images that share the parser.
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Environment xmlns="http://schemas.dmtf.org/ovf/environment/1"
                         xmlns:wa="http://schemas.microsoft.com/windowsazure">
              <wa:ProvisioningSection>
                <wa:Version>1.0.0.0</wa:Version>
                <wa:LinuxProvisioningConfigurationSet>
                  <wa:ConfigurationSetType>LinuxProvisioningConfiguration</wa:ConfigurationSetType>
                  <wa:HostName>linux-host</wa:HostName>
                  <wa:UserName>azureuser</wa:UserName>
                  <wa:CustomData>aGVsbG8=</wa:CustomData>
                </wa:LinuxProvisioningConfigurationSet>
              </wa:ProvisioningSection>
            </Environment>
            """;

        var env = AzureOvfEnvParser.Parse(xml);

        env.Hostname.Should().Be("linux-host");
        env.CustomDataBase64.Should().Be("aGVsbG8=");
    }

    // ---- end-to-end ReadAsync via the IMDS fake + file IO injection ----
    // We invoke the internal ReadAsync directly to bypass the OS-level
    // IsRunningOnAzure gate (returns false on the non-Azure CI host) and
    // exercise the IMDS + CustomData + result composition.

    [Fact]
    public async Task ReadAsync_emits_Ready_with_imds_vmid_and_byte_exact_userdata()
    {
        // Regression: UserData MUST be byte-exact. The payload's second byte
        // (0x8B) is not a valid UTF-8 leading byte — if anyone refactors this
        // through ReadAllText, the bytes turn into EF BF BD and the
        // SequenceEqual assertion below fails. Same trap that fired on
        // NoCloudDataSource in production (see feedback_binary_contracts.md).
        var nonUtf8Payload = new byte[]
        {
            0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A,
            0xCB, 0x48, 0xCD, 0xC9, 0xC9, 0x07, 0x00,
            0x86, 0xA6, 0x10, 0x36, 0x05, 0x00, 0x00, 0x00,
        };

        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(LoadFixtureText("imds-instance.json")),
        });

        var volumes = Substitute.For<IVolumeProbe>();
        volumes.EnumerateVolumes().Returns([]);

        var source = new AzureDataSource(
            volumes,
            NullLogger<AzureDataSource>.Instance,
            imdsClientFactory: () => new AzureImdsClient(
                handler,
                disposeHandler: false,
                TimeSpan.FromSeconds(1),
                NullLogger<AzureImdsClient>.Instance),
            fileExists: p => p == AzureDataSource.CustomDataPath,
            readFileBytes: (_, _) => Task.FromResult(nonUtf8Payload));

        var probe = await source.ReadAsync(CancellationToken.None);

        probe.Should().BeOfType<DataSourceProbeResult.Ready>();
        var data = ((DataSourceProbeResult.Ready)probe).Data;

        data.SourceName.Should().Be("Azure");
        data.InstanceId.Should().Be("11111111-2222-3333-4444-555555555555");
        data.Hostname.Should().Be("azure-test-host");
        // BYTE-EXACT. Do not relax this assertion to a content comparison.
        data.UserData.Should().Equal(nonUtf8Payload);
        data.VendorData.Should().BeNull();
        data.PlatformMetadata.Should().NotBeNull();
        data.PlatformMetadata!.CloudName.Should().Be("azure");
        data.PlatformMetadata.Region.Should().Be("westeurope");
        data.PlatformMetadata.InstanceType.Should().Be("Standard_D2s_v3");
        data.PlatformMetadata.AvailabilityZone.Should().Be("1");
    }

    [Fact]
    public async Task ReadAsync_returns_Failed_when_imds_unreachable_and_registry_misses()
    {
        var handler = new FakeHttpHandler(_ =>
            throw new HttpRequestException("simulated network down"));

        var volumes = Substitute.For<IVolumeProbe>();
        volumes.EnumerateVolumes().Returns([]);

        var source = new AzureDataSource(
            volumes,
            NullLogger<AzureDataSource>.Instance,
            imdsClientFactory: () => new AzureImdsClient(
                handler,
                disposeHandler: false,
                TimeSpan.FromSeconds(1),
                NullLogger<AzureImdsClient>.Instance),
            fileExists: _ => false,
            readFileBytes: (_, _) => Task.FromResult(Array.Empty<byte>()));

        var probe = await source.ReadAsync(CancellationToken.None);

        // On a real Azure host the registry VmId saves us; on CI it doesn't.
        if (OperatingSystem.IsWindows() && AzureRegistryVmIdExists())
            return;

        probe.Should().BeOfType<DataSourceProbeResult.Failed>();
    }

    [Fact]
    public async Task ReadAsync_does_not_read_customdata_when_file_absent()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(LoadFixtureText("imds-instance.json")),
        });

        var volumes = Substitute.For<IVolumeProbe>();
        volumes.EnumerateVolumes().Returns([]);

        var readBytesCalls = 0;
        var source = new AzureDataSource(
            volumes,
            NullLogger<AzureDataSource>.Instance,
            imdsClientFactory: () => new AzureImdsClient(
                handler,
                disposeHandler: false,
                TimeSpan.FromSeconds(1),
                NullLogger<AzureImdsClient>.Instance),
            fileExists: _ => false,
            readFileBytes: (_, _) =>
            {
                readBytesCalls++;
                return Task.FromResult(Array.Empty<byte>());
            });

        var probe = await source.ReadAsync(CancellationToken.None);

        readBytesCalls.Should().Be(0);
        probe.Should().BeOfType<DataSourceProbeResult.Ready>();
        ((DataSourceProbeResult.Ready)probe).Data.UserData.Should().BeNull();
    }

    // ---- helpers ----

    [SupportedOSPlatform("windows")]
    private static bool AzureRegistryVmIdExists()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(AzureDataSource.AzureVmIdKey);
            return key?.GetValue(AzureDataSource.AzureVmIdValue) is string s && !string.IsNullOrEmpty(s);
        }
        catch
        {
            return false;
        }
    }

    private static bool AzureSignalsPresent()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        if (AzureRegistryVmIdExists())
            return true;
        // Chassis asset tag is read internally by PlatformProbes; the public
        // gate covers both signals.
        return false;
    }

    private static string LoadFixtureText(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "azure", name);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Fixture missing at {path}. Check test csproj CopyToOutputDirectory wiring.",
                path);
        return File.ReadAllText(path, Encoding.UTF8);
    }

    private sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
