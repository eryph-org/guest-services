using AwesomeAssertions;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions;
using Eryph.GuestServices.HvDataExchange.Guest;
using Eryph.GuestServices.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Service.Tests;

public class KvpShellSelectorTests
{
    [Fact]
    public async Task SelectAsync_KvpShellAndArgsBothSet_ReturnsThem()
    {
        var dataExchange = new StubDataExchange
        {
            External =
            {
                [Constants.ShellKey] = "pwsh.exe",
                [Constants.ShellArgsKey] = "-NoLogo -NoProfile",
            },
        };
        var selector = new KvpShellSelector(dataExchange, NullLogger<KvpShellSelector>.Instance);

        var selection = await selector.SelectAsync(EmptyEnv(), CancellationToken.None);

        selection.Should().Be(new ShellSelection("pwsh.exe", "-NoLogo -NoProfile"));
    }

    [Fact]
    public async Task SelectAsync_KvpShellOnly_ReturnsShellWithEmptyArgs()
    {
        var dataExchange = new StubDataExchange
        {
            External = { [Constants.ShellKey] = "pwsh.exe" },
        };
        var selector = new KvpShellSelector(dataExchange, NullLogger<KvpShellSelector>.Instance);

        var selection = await selector.SelectAsync(EmptyEnv(), CancellationToken.None);

        selection.Should().Be(new ShellSelection("pwsh.exe", string.Empty));
    }

    [Fact]
    public async Task SelectAsync_KvpWinsOverSshEnv()
    {
        var dataExchange = new StubDataExchange
        {
            External = { [Constants.ShellKey] = "kvp-shell.exe" },
        };
        var env = new Dictionary<string, string>
        {
            [Constants.ShellEnvName] = "env-shell.exe",
        };
        var selector = new KvpShellSelector(dataExchange, NullLogger<KvpShellSelector>.Instance);

        var selection = await selector.SelectAsync(env, CancellationToken.None);

        selection.Command.Should().Be("kvp-shell.exe");
    }

    [Fact]
    public async Task SelectAsync_NoKvp_FallsBackToSshEnv()
    {
        var dataExchange = new StubDataExchange();
        var env = new Dictionary<string, string>
        {
            [Constants.ShellEnvName] = "env-shell.exe",
            [Constants.ShellArgsEnvName] = "-i",
        };
        var selector = new KvpShellSelector(dataExchange, NullLogger<KvpShellSelector>.Instance);

        var selection = await selector.SelectAsync(env, CancellationToken.None);

        selection.Should().Be(new ShellSelection("env-shell.exe", "-i"));
    }

    [Fact]
    public async Task SelectAsync_NeitherKvpNorEnv_ReturnsPlatformDefault()
    {
        var dataExchange = new StubDataExchange();
        var selector = new KvpShellSelector(dataExchange, NullLogger<KvpShellSelector>.Instance);

        var selection = await selector.SelectAsync(EmptyEnv(), CancellationToken.None);

        selection.Should().Be(DefaultShellSelector.PlatformDefault());
    }

    [Fact]
    public async Task SelectAsync_KvpReadFails_FallsBackInsteadOfThrowing()
    {
        var dataExchange = new ThrowingDataExchange();
        var env = new Dictionary<string, string>
        {
            [Constants.ShellEnvName] = "fallback.exe",
        };
        var selector = new KvpShellSelector(dataExchange, NullLogger<KvpShellSelector>.Instance);

        var selection = await selector.SelectAsync(env, CancellationToken.None);

        selection.Command.Should().Be("fallback.exe");
    }

    [Fact]
    public async Task SelectAsync_KvpShellIsBlank_FallsThroughToEnv()
    {
        var dataExchange = new StubDataExchange
        {
            External = { [Constants.ShellKey] = "   " },
        };
        var env = new Dictionary<string, string>
        {
            [Constants.ShellEnvName] = "env-shell.exe",
        };
        var selector = new KvpShellSelector(dataExchange, NullLogger<KvpShellSelector>.Instance);

        var selection = await selector.SelectAsync(env, CancellationToken.None);

        selection.Command.Should().Be("env-shell.exe");
    }

    private static IReadOnlyDictionary<string, string> EmptyEnv() =>
        new Dictionary<string, string>();

    private sealed class StubDataExchange : IGuestDataExchange
    {
        public Dictionary<string, string> External { get; } = new();

        public Task<IReadOnlyDictionary<string, string>> GetExternalDataAsync()
            => Task.FromResult<IReadOnlyDictionary<string, string>>(External);

        public Task<IReadOnlyDictionary<string, string>> GetGuestDataAsync()
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task SetGuestValuesAsync(IReadOnlyDictionary<string, string?> values)
            => Task.CompletedTask;
    }

    private sealed class ThrowingDataExchange : IGuestDataExchange
    {
        public Task<IReadOnlyDictionary<string, string>> GetExternalDataAsync()
            => throw new InvalidOperationException("simulated KVP failure");

        public Task<IReadOnlyDictionary<string, string>> GetGuestDataAsync()
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task SetGuestValuesAsync(IReadOnlyDictionary<string, string?> values)
            => Task.CompletedTask;
    }
}
