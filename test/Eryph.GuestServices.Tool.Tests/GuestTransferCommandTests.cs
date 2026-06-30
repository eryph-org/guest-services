using AwesomeAssertions;
using Eryph.GuestServices.Client;
using Eryph.GuestServices.Tool.Commands;
using Microsoft.DevTunnels.Ssh;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Tests;

// The shared base every file/directory transfer command (VM and catlet) runs
// through. These pin its error handling so a future refactor of the catch blocks
// cannot silently swallow failures or run the transfer on a broken connection.
public class GuestTransferCommandTests
{
    [Fact]
    public async Task ExecuteAsync_ConnectorThrowsGuestConnectionException_ReturnsMinusOneAndSkipsTransfer()
    {
        var command = new TestCommand(new ThrowingConnector(
            new GuestConnectionException("the authentication failed")));

        var result = await command.ExecuteAsync(null!, new TestCommand.Settings());

        result.Should().Be(-1);
        command.TransferInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ConnectorThrowsUnexpectedException_ReturnsMinusOneAndSkipsTransfer()
    {
        var command = new TestCommand(new ThrowingConnector(new InvalidOperationException("boom")));

        var result = await command.ExecuteAsync(null!, new TestCommand.Settings());

        result.Should().Be(-1);
        command.TransferInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ConnectorFactoryThrowsGuestConnectionException_ReturnsMinusOneAndSkipsTransfer()
    {
        // Key resolution now runs in CreateConnectorAsync (before ConnectAsync), so
        // a failure there — e.g. "No SSH key found" from ToolGuestConnectors — must
        // be caught by the same try/catch and never reach the transfer.
        var command = new FactoryThrowingCommand(
            new GuestConnectionException("No SSH key found. Have you run the initialize command?"));

        var result = await command.ExecuteAsync(null!, new FactoryThrowingCommand.Settings());

        result.Should().Be(-1);
        command.TransferInvoked.Should().BeFalse();
    }

    private sealed class ThrowingConnector(Exception exception) : IGuestConnector
    {
        public Task<GuestSshConnection> ConnectAsync(CancellationToken cancellation) =>
            throw exception;
    }

    // Mirrors the real (async) ToolGuestConnectors factory failing during key /
    // connection resolution: a faulted task from CreateConnectorAsync.
    private sealed class FactoryThrowingCommand(Exception exception)
        : GuestTransferCommand<FactoryThrowingCommand.Settings>
    {
        public bool TransferInvoked { get; private set; }

        public sealed class Settings : CommandSettings;

        protected override Task<IGuestConnector> CreateConnectorAsync(Settings settings) =>
            Task.FromException<IGuestConnector>(exception);

        protected override Task<int> TransferAsync(SshSession session, Settings settings)
        {
            TransferInvoked = true;
            return Task.FromResult(0);
        }
    }

    private sealed class TestCommand(IGuestConnector connector)
        : GuestTransferCommand<TestCommand.Settings>
    {
        public bool TransferInvoked { get; private set; }

        public sealed class Settings : CommandSettings;

        protected override Task<IGuestConnector> CreateConnectorAsync(Settings settings) =>
            Task.FromResult(connector);

        protected override Task<int> TransferAsync(SshSession session, Settings settings)
        {
            TransferInvoked = true;
            return Task.FromResult(0);
        }
    }
}
