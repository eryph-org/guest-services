using AwesomeAssertions;
using Eryph.GuestServices.CloudConfig;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Eryph.GuestServices.Provisioning.Tests.Windows;

public sealed class DryRunWindowsOsTests
{
    private static (DryRunWindowsOs sut, IWindowsOs inner) Build()
    {
        var inner = Substitute.For<IWindowsOs>();
        var sut = new DryRunWindowsOs(inner, NullLogger<DryRunWindowsOs>.Instance);
        return (sut, inner);
    }

    // ---- reads pass through to the inner ----

    [Fact]
    public async Task GetComputerNameAsync_delegates_to_inner()
    {
        var (sut, inner) = Build();
        inner.GetComputerNameAsync(Arg.Any<CancellationToken>()).Returns("HOST");

        var result = await sut.GetComputerNameAsync(CancellationToken.None);

        result.Should().Be("HOST");
        await inner.Received().GetComputerNameAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LocalUserExistsAsync_delegates_to_inner()
    {
        var (sut, inner) = Build();
        inner.LocalUserExistsAsync("alice", Arg.Any<CancellationToken>()).Returns(true);

        var exists = await sut.LocalUserExistsAsync("alice", CancellationToken.None);

        exists.Should().BeTrue();
        await inner.Received().LocalUserExistsAsync("alice", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LocalGroupExistsAsync_delegates_to_inner()
    {
        var (sut, inner) = Build();
        inner.LocalGroupExistsAsync("admins", Arg.Any<CancellationToken>()).Returns(true);

        var exists = await sut.LocalGroupExistsAsync("admins", CancellationToken.None);

        exists.Should().BeTrue();
        await inner.Received().LocalGroupExistsAsync("admins", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TranslateUnixPath_delegates_to_inner()
    {
        var (sut, inner) = Build();
        inner.TranslateUnixPath("/etc/test").Returns(@"C:\etc\test");

        var translated = sut.TranslateUnixPath("/etc/test");

        translated.Should().Be(@"C:\etc\test");
    }

    // ---- writes are intercepted; the inner is never called ----

    [Fact]
    public async Task SetComputerNameAsync_returns_AlreadySet_and_skips_inner()
    {
        var (sut, inner) = Build();

        var result = await sut.SetComputerNameAsync("NEW-NAME", CancellationToken.None);

        result.Should().Be(SetComputerNameResult.AlreadySet);
        await inner.DidNotReceiveWithAnyArgs().SetComputerNameAsync(default!, default);
    }

    [Fact]
    public async Task CreateLocalUserAsync_skips_inner()
    {
        var (sut, inner) = Build();

        await sut.CreateLocalUserAsync(new LocalUserSpec { Name = "alice" }, CancellationToken.None);

        await inner.DidNotReceiveWithAnyArgs().CreateLocalUserAsync(default!, default);
    }

    [Fact]
    public async Task UpdateLocalUserAsync_skips_inner()
    {
        var (sut, inner) = Build();

        await sut.UpdateLocalUserAsync(new LocalUserSpec { Name = "alice" }, CancellationToken.None);

        await inner.DidNotReceiveWithAnyArgs().UpdateLocalUserAsync(default!, default);
    }

    [Fact]
    public async Task SetLocalUserPasswordAsync_skips_inner()
    {
        var (sut, inner) = Build();

        await sut.SetLocalUserPasswordAsync("alice", "secret", true, CancellationToken.None);

        await inner.DidNotReceiveWithAnyArgs()
            .SetLocalUserPasswordAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task CreateLocalGroupAsync_skips_inner()
    {
        var (sut, inner) = Build();

        await sut.CreateLocalGroupAsync("g", CancellationToken.None);

        await inner.DidNotReceiveWithAnyArgs().CreateLocalGroupAsync(default!, default);
    }

    [Fact]
    public async Task AddUserToGroupAsync_skips_inner()
    {
        var (sut, inner) = Build();

        await sut.AddUserToGroupAsync("u", "g", CancellationToken.None);

        await inner.DidNotReceiveWithAnyArgs().AddUserToGroupAsync(default!, default!, default);
    }

    [Fact]
    public async Task EnsureUserInAdministratorsAsync_skips_inner()
    {
        var (sut, inner) = Build();

        await sut.EnsureUserInAdministratorsAsync("u", CancellationToken.None);

        await inner.DidNotReceiveWithAnyArgs().EnsureUserInAdministratorsAsync(default!, default);
    }

    [Fact]
    public async Task EnsureDirectoryAsync_skips_inner()
    {
        var (sut, inner) = Build();

        await sut.EnsureDirectoryAsync(@"C:\foo", CancellationToken.None);

        await inner.DidNotReceiveWithAnyArgs().EnsureDirectoryAsync(default!, default);
    }

    [Fact]
    public async Task WriteFileAsync_skips_inner()
    {
        var (sut, inner) = Build();

        await sut.WriteFileAsync(@"C:\foo.txt", [1, 2, 3], append: false, CancellationToken.None);

        await inner.DidNotReceiveWithAnyArgs().WriteFileAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task SetFileOwnerAsync_skips_inner()
    {
        var (sut, inner) = Build();

        await sut.SetFileOwnerAsync(@"C:\foo", "owner", CancellationToken.None);

        await inner.DidNotReceiveWithAnyArgs().SetFileOwnerAsync(default!, default!, default);
    }

    [Fact]
    public async Task SetUserSshAuthorizedKeysAsync_skips_inner()
    {
        var (sut, inner) = Build();

        await sut.SetUserSshAuthorizedKeysAsync("u", ["ssh-ed25519 AAA"], CancellationToken.None);

        await inner.DidNotReceiveWithAnyArgs()
            .SetUserSshAuthorizedKeysAsync(default!, default!, default);
    }

    [Fact]
    public async Task RunShellCommandAsync_returns_success_and_skips_inner()
    {
        var (sut, inner) = Build();

        var result = await sut.RunShellCommandAsync("echo hi", CancellationToken.None);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().BeEmpty();
        result.StdErr.Should().BeEmpty();
        await inner.DidNotReceiveWithAnyArgs().RunShellCommandAsync(default!, default);
    }

    [Fact]
    public async Task RunArgvCommandAsync_returns_success_and_skips_inner()
    {
        var (sut, inner) = Build();

        var result = await sut.RunArgvCommandAsync(["echo", "hi"], CancellationToken.None);

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().BeEmpty();
        result.StdErr.Should().BeEmpty();
        await inner.DidNotReceiveWithAnyArgs().RunArgvCommandAsync(default!, default);
    }

    // ---- networking writes (IPv6 / routes / DNS search) skip inner ----

    [Fact]
    public async Task EnableDhcp6Async_skips_inner()
    {
        var (sut, inner) = Build();
        await sut.EnableDhcp6Async(7, CancellationToken.None);
        await inner.DidNotReceiveWithAnyArgs().EnableDhcp6Async(default, default);
    }

    [Fact]
    public async Task DisableDhcp6Async_skips_inner()
    {
        var (sut, inner) = Build();
        await sut.DisableDhcp6Async(7, CancellationToken.None);
        await inner.DidNotReceiveWithAnyArgs().DisableDhcp6Async(default, default);
    }

    [Fact]
    public async Task SetStaticIpv6AddressesAsync_skips_inner()
    {
        var (sut, inner) = Build();
        await sut.SetStaticIpv6AddressesAsync(7, ["2001:db8::1/64"], CancellationToken.None);
        await inner.DidNotReceiveWithAnyArgs().SetStaticIpv6AddressesAsync(default, default!, default);
    }

    [Fact]
    public async Task SetIpv6DefaultGatewayAsync_skips_inner()
    {
        var (sut, inner) = Build();
        await sut.SetIpv6DefaultGatewayAsync(7, "2001:db8::254", CancellationToken.None);
        await inner.DidNotReceiveWithAnyArgs().SetIpv6DefaultGatewayAsync(default, default!, default);
    }

    [Fact]
    public async Task SetInterfaceRoutesAsync_skips_inner()
    {
        var (sut, inner) = Build();
        var routes = new[] { new NetworkRoute { To = "10.0.0.0/24", Via = "192.168.0.1" } };
        await sut.SetInterfaceRoutesAsync(7, routes, CancellationToken.None);
        await inner.DidNotReceiveWithAnyArgs().SetInterfaceRoutesAsync(default, default!, default);
    }

    [Fact]
    public async Task SetDnsSearchSuffixesAsync_skips_inner()
    {
        var (sut, inner) = Build();
        await sut.SetDnsSearchSuffixesAsync(7, ["a.com"], CancellationToken.None);
        await inner.DidNotReceiveWithAnyArgs().SetDnsSearchSuffixesAsync(default, default!, default);
    }
}
