using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Serialization;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.UserData.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.UserData.Handlers;

public sealed class CloudConfigPartHandlerTests
{
    [Fact]
    public void CanHandle_AcceptsCloudConfigMediaTypes()
    {
        var handler = new CloudConfigPartHandler(new CloudConfigSerializer(NullLogger<CloudConfigSerializer>.Instance), NullLogger<CloudConfigPartHandler>.Instance);

        handler.CanHandle(new UserDataPart("text/x-cloud-config", [], null)).Should().BeTrue();
        handler.CanHandle(new UserDataPart("text/cloud-config", [], null)).Should().BeTrue();
        handler.CanHandle(new UserDataPart("text/x-shellscript", [], null)).Should().BeFalse();
    }

    [Fact]
    public async Task ProcessAsync_MergesParsedFragmentIntoContext()
    {
        const string yaml = """
            #cloud-config
            hostname: testhost
            runcmd:
              - echo hello
            """;
        var handler = new CloudConfigPartHandler(
            new CloudConfigSerializer(NullLogger<CloudConfigSerializer>.Instance),
            NullLogger<CloudConfigPartHandler>.Instance);
        var ctx = new TestResolutionContext();
        var part = new UserDataPart("text/x-cloud-config", Encoding.UTF8.GetBytes(yaml), "user-data");

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        ctx.CloudConfig.Hostname.Should().Be("testhost");
        ctx.CloudConfig.Runcmd.Should().NotBeNull().And.HaveCount(1);
    }

    [Fact]
    public async Task ProcessAsync_HandlesUtf8BomFromWindowsPowerShellSetContent()
    {
        // Set-Content -Encoding UTF8 (Windows PowerShell 5.1) writes a UTF-8
        // BOM. Without explicit stripping in the handler, YamlDotNet sees
        // ﻿ before "#cloud-config" and rejects the document with
        // "Failed to parse cloud-config userdata" — the symptom operators
        // reported when authoring catlet user-data on Windows.
        const string yaml = """
            #cloud-config
            hostname: bom-host
            """;
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes(yaml))
            .ToArray();

        var handler = new CloudConfigPartHandler(
            new CloudConfigSerializer(NullLogger<CloudConfigSerializer>.Instance),
            NullLogger<CloudConfigPartHandler>.Instance);
        var ctx = new TestResolutionContext();
        var part = new UserDataPart("text/x-cloud-config", bytes, "user-data");

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        ctx.CloudConfig.Hostname.Should().Be("bom-host");
    }
}
