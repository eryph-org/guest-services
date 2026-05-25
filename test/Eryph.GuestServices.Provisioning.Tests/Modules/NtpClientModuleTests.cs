using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Modules;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using CloudConfigModel = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Modules;

public sealed class NtpClientModuleTests
{
    [Fact]
    public async Task No_ntp_block_skips_OS_call_entirely()
    {
        // Absent block must not touch w32time — operators may have configured
        // it manually and the agent should not override that on every boot.
        var os = Substitute.For<IWindowsOs>();
        var module = new NtpClientModule(NullLogger<NtpClientModule>.Instance);

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel()),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.DidNotReceive().ConfigureNtpClientAsync(
            Arg.Any<bool>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Servers_and_pools_are_merged_in_input_order()
    {
        // Cloud-init keeps servers/pools distinct; Windows w32time does not.
        // Pin the merge order (servers first, pools second) so log diffs are
        // stable.
        var os = Substitute.For<IWindowsOs>();
        var module = new NtpClientModule(NullLogger<NtpClientModule>.Instance);

        var config = new CloudConfigModel
        {
            Ntp = new NtpConfig
            {
                Servers = ["time.windows.com", "time.nist.gov"],
                Pools = ["pool.ntp.org"],
            },
        };

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).ConfigureNtpClientAsync(
            true,
            Arg.Is<IReadOnlyList<string>>(p =>
                p.Count == 3
                && p[0] == "time.windows.com"
                && p[1] == "time.nist.gov"
                && p[2] == "pool.ntp.org"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Enabled_false_passes_through_to_OS_with_empty_peer_list()
    {
        var os = Substitute.For<IWindowsOs>();
        var module = new NtpClientModule(NullLogger<NtpClientModule>.Instance);
        var config = new CloudConfigModel
        {
            Ntp = new NtpConfig { Enabled = false, Servers = ["ignored.example.com"] },
        };

        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        result.Should().BeOfType<ModuleOutcome.Completed>();
        await os.Received(1).ConfigureNtpClientAsync(
            false,
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Empty_and_whitespace_peers_are_filtered_out()
    {
        // Real cloud-configs sometimes carry leading/trailing whitespace from
        // multi-line YAML scalars. We don't want those landing as empty
        // entries in the w32tm manual peer list.
        var os = Substitute.For<IWindowsOs>();
        var module = new NtpClientModule(NullLogger<NtpClientModule>.Instance);
        var config = new CloudConfigModel
        {
            Ntp = new NtpConfig
            {
                Servers = ["  time.windows.com  ", "", "   "],
                Pools = ["pool.ntp.org"],
            },
        };

        await module.ApplyAsync(
            ResolvedUserData.Empty(config),
            new TestModuleContext(os),
            CancellationToken.None);

        await os.Received(1).ConfigureNtpClientAsync(
            true,
            Arg.Is<IReadOnlyList<string>>(p =>
                p.Count == 2 && p[0] == "time.windows.com" && p[1] == "pool.ntp.org"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OS_exception_surfaces_as_module_failed()
    {
        var os = Substitute.For<IWindowsOs>();
        os.ConfigureNtpClientAsync(
            Arg.Any<bool>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("w32tm failed"));

        var module = new NtpClientModule(NullLogger<NtpClientModule>.Instance);
        var result = await module.ApplyAsync(
            ResolvedUserData.Empty(new CloudConfigModel { Ntp = new NtpConfig { Servers = ["x"] } }),
            new TestModuleContext(os),
            CancellationToken.None);

        var failed = result.Should().BeOfType<ModuleOutcome.Failed>().Subject;
        failed.Reason.Should().Contain("w32tm failed");
    }
}
