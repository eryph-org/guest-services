using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.DataSources.OpenStack;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.DataSources;

public sealed class OpenStackMetadataDataSourceTests
{
    private const string BaseUrl = "http://169.254.169.254";

    // Builds a datasource whose HTTP client is backed by a path-routing handler.
    // The platform gates are injected so the read path runs on a non-OpenStack host.
    private static OpenStackMetadataDataSource NewDataSource(
        Func<string, HttpResponseMessage> route,
        bool isOpenStack = true,
        bool isAzure = false)
    {
        return new OpenStackMetadataDataSource(
            NullLogger<OpenStackMetadataDataSource>.Instance,
            clientFactory: () => new OpenStackMetadataClient(
                new RoutingHandler(route),
                disposeHandler: true,
                BaseUrl,
                TimeSpan.FromSeconds(5),
                maxAttempts: 2,
                retryDelay: (_, _) => Task.CompletedTask,
                NullLogger<OpenStackMetadataClient>.Instance),
            isRunningOnOpenStack: () => isOpenStack,
            isRunningOnAzure: () => isAzure);
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    [Fact]
    public void Metadata_properties()
    {
        var source = NewDataSource(_ => NotFound());

        source.Name.Should().Be("OpenStack");
        source.Priority.Should().Be(50);
        source.RequiresNetwork.Should().BeTrue();
    }

    [Fact]
    public async Task ProbeAsync_NotApplicable_when_not_on_openstack()
    {
        // DMI gate fails -> we must not touch the link-local address at all.
        var touched = false;
        var source = NewDataSource(_ => { touched = true; return Ok(""); }, isOpenStack: false);

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.NotApplicable>();
        touched.Should().BeFalse("the HTTP service must not be probed when the DMI gate fails");
    }

    [Fact]
    public async Task ProbeAsync_NotApplicable_when_on_azure()
    {
        var source = NewDataSource(_ => Ok(""), isOpenStack: true, isAzure: true);

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.NotApplicable>();
    }

    [Fact]
    public async Task ProbeAsync_WaitForReady_when_metadata_service_unreachable()
    {
        // DMI says OpenStack, but the link-local endpoint isn't answering yet.
        var source = NewDataSource(_ => throw new HttpRequestException("no route to host"));

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.WaitForReady>();
    }

    [Fact]
    public async Task ProbeAsync_Ready_when_metadata_present()
    {
        var source = NewDataSource(path => path switch
        {
            "/openstack" => Ok("2018-08-27\nlatest"),
            "/openstack/2018-08-27/meta_data.json" =>
                Ok("{\"uuid\":\"http-instance-1\",\"hostname\":\"sim-host\"}"),
            _ => NotFound(),
        });

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.Ready>();
        var data = ((DataSourceProbeResult.Ready)result).Data;
        data.SourceName.Should().Be("OpenStack");
        data.InstanceId.Should().Be("http-instance-1");
        data.Hostname.Should().Be("sim-host");
        data.PlatformMetadata!.CloudName.Should().Be("openstack");
        data.PlatformMetadata.Subplatform.Should().Be("metadata-service");
    }

    [Fact]
    public async Task ProbeAsync_NotApplicable_when_reachable_but_no_meta_data()
    {
        // Liveness answers, but every version's meta_data.json is 404.
        var source = NewDataSource(path =>
            path == "/openstack" ? Ok("latest") : NotFound());

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.NotApplicable>();
    }

    [Fact]
    public async Task ProbeAsync_Failed_when_meta_data_malformed()
    {
        var source = NewDataSource(path => path switch
        {
            "/openstack" => Ok("2018-08-27"),
            "/openstack/2018-08-27/meta_data.json" => Ok("{ not valid json "),
            _ => NotFound(),
        });

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.Failed>();
    }

    // ---- vendor_data.json conversion (cloud-init convert_vendordata) ----

    private static HttpResponseMessage MetaOk() =>
        Ok("{\"uuid\":\"vd-instance\",\"hostname\":\"vd-host\"}");

    private static async Task<DataSourceResult> ReadyDataWithVendorAsync(string vendorDataJsonBody)
    {
        var source = NewDataSource(path => path switch
        {
            "/openstack" => Ok("2018-08-27"),
            "/openstack/2018-08-27/meta_data.json" => MetaOk(),
            "/openstack/2018-08-27/vendor_data.json" => Ok(vendorDataJsonBody),
            _ => NotFound(),
        });
        var result = await source.ProbeAsync(CancellationToken.None);
        result.Should().BeOfType<DataSourceProbeResult.Ready>();
        return ((DataSourceProbeResult.Ready)result).Data;
    }

    [Fact]
    public async Task ProbeAsync_extracts_vendor_data_from_cloud_init_key()
    {
        // The common deployer shape: vendor_data.json = {"cloud-init": "<payload>"}.
        const string vendorCfg = "#cloud-config\nwrite_files:\n  - path: C:\\v.txt\n    content: from-vendor\n";
        var body = "{\"cloud-init\":" + JsonSerializer.Serialize(vendorCfg) + "}";

        var data = await ReadyDataWithVendorAsync(body);

        Encoding.UTF8.GetString(data.GetVendorDataBytes()).Should().Be(vendorCfg);
    }

    [Fact]
    public async Task ProbeAsync_uses_bare_json_string_vendor_data_as_is()
    {
        const string vendorCfg = "#cloud-config\npackages: [vim]\n";
        var body = JsonSerializer.Serialize(vendorCfg); // a JSON string literal

        var data = await ReadyDataWithVendorAsync(body);

        Encoding.UTF8.GetString(data.GetVendorDataBytes()).Should().Be(vendorCfg);
    }

    [Fact]
    public async Task ProbeAsync_treats_arbitrary_vendor_data_object_as_no_payload()
    {
        // An arbitrary metadata object with no "cloud-init" key carries nothing
        // runnable — the real nova default (vendor_data.json = {}) is this case.
        var data = await ReadyDataWithVendorAsync("{\"some\":\"metadata\",\"n\":1}");

        data.VendorData.Should().BeNull();
    }

    // ---- real-world fixture served over HTTP (test/fixtures/configdrive-openstack) ----
    // Proves the shared OpenStackMetadataReader processes real nova metadata over
    // the HTTP transport identically to the disk transport. Same fixture, same
    // sanitized values as RealWorldOpenStackConfigDriveTests.

    private const string SanitizedUuid = "facade00-0000-4000-8000-000000000001";
    private const string SanitizedHostname = "egs-fixture.novalocal";
    private const string SanitizedPublicKey =
        "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5FIXTUREKEY egs@fixture";

    [Fact]
    public async Task ProbeAsync_reads_the_real_nova_metadata_over_http()
    {
        var fixtureDir = Path.Combine(
            AppContext.BaseDirectory, "fixtures", "configdrive-openstack", "openstack", "2018-08-27");

        var source = NewDataSource(path =>
        {
            if (path == "/openstack")
                return Ok("2018-08-27\nlatest");

            const string prefix = "/openstack/2018-08-27/";
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                var file = Path.Combine(fixtureDir, path[prefix.Length..]);
                if (File.Exists(file))
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(File.ReadAllBytes(file)),
                    };
            }

            return NotFound();
        });

        var result = await source.ProbeAsync(CancellationToken.None);

        result.Should().BeOfType<DataSourceProbeResult.Ready>();
        var data = ((DataSourceProbeResult.Ready)result).Data;
        data.SourceName.Should().Be("OpenStack");
        data.InstanceId.Should().Be(SanitizedUuid);
        data.Hostname.Should().Be(SanitizedHostname);
        data.PlatformMetadata!.Subplatform.Should().Be("metadata-service");
        data.PlatformMetadata.AvailabilityZone.Should().Be("nova");

        data.SshPublicKeys.Should().NotBeNullOrEmpty();
        data.SshPublicKeys.Should().AllBe(SanitizedPublicKey);

        Encoding.UTF8.GetString(data.GetUserDataBytes()).Should().StartWith("#cloud-config");

        data.NetworkConfig.Should().NotBeNullOrWhiteSpace();
        IsJsonObjectWith(data.NetworkConfig!, "links").Should().BeTrue();
    }

    private static bool IsJsonObjectWith(string text, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(propertyName, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class RoutingHandler(Func<string, HttpResponseMessage> route)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(route(request.RequestUri!.AbsolutePath));
    }
}
