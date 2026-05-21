using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Handlers;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Serialization;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.State;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Eryph.GuestServices.Provisioning.Tests.Integration;

public sealed class EndToEndProvisioningTests : IDisposable
{
    private readonly string _stateDirectory = Path.Combine(
        Path.GetTempPath(),
        "egs-provisioning-it-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory))
            Directory.Delete(_stateDirectory, recursive: true);
    }

    [Fact]
    public async Task RunAsync_RealisticCloudConfig_AppliesAllHandlers()
    {
        const string yaml =
            """
            #cloud-config
            hostname: testhost
            users:
              - name: admin
                passwd: hunter2
                groups: [ Administrators ]
                ssh_authorized_keys:
                  - ssh-ed25519 AAAA-admin-key
            ssh_authorized_keys:
              - ssh-ed25519 AAAA-top-level-key
            write_files:
              - path: /etc/motd
                content: hello eryph
                permissions: '0644'
            runcmd:
              - echo first
              - [ echo, argv-form ]
            """;

        var locator = Substitute.For<IDataSourceLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>())
            .Returns(new DataSourceResult
            {
                SourceName = "test",
                InstanceId = "instance-1",
                Hostname = "testhost",
                UserData = yaml,
            });

        var windowsOs = Substitute.For<IWindowsOs>();
        windowsOs.GetComputerNameAsync(Arg.Any<CancellationToken>()).Returns("OLDNAME");
        windowsOs.SetComputerNameAsync("testhost", Arg.Any<CancellationToken>())
            .Returns(SetComputerNameResult.SetWithRebootPending);
        windowsOs.LocalUserExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        windowsOs.LocalGroupExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        windowsOs.TranslateUnixPath(Arg.Any<string>())
            .Returns(ci => @"C:\" + ((string)ci[0]).TrimStart('/').Replace('/', '\\'));
        windowsOs.RunShellCommandAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));
        windowsOs.RunArgvCommandAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new RunCommandResult(0, "", ""));

        var stateStore = new FileStateStore(NullLogger<FileStateStore>.Instance, _stateDirectory);
        var serializer = new CloudConfigSerializer();
        var reporter = Substitute.For<IHostStatusReporter>();

        var handlers = new IHandler[]
        {
            new SetHostnameHandler(NullLogger<SetHostnameHandler>.Instance),
            new UsersGroupsHandler(NullLogger<UsersGroupsHandler>.Instance),
            new SetPasswordsHandler(NullLogger<SetPasswordsHandler>.Instance),
            new SshAuthorizedKeysHandler(NullLogger<SshAuthorizedKeysHandler>.Instance),
            new WriteFilesHandler(NullLogger<WriteFilesHandler>.Instance),
            new RuncmdHandler(NullLogger<RuncmdHandler>.Instance),
        };

        var runner = new StageRunner(
            locator,
            serializer,
            stateStore,
            handlers,
            reporter,
            windowsOs,
            NullLogger<StageRunner>.Instance);

        var outcome = await runner.RunAsync(CancellationToken.None);

        outcome.Should().BeOfType<StageRunOutcome.RebootRequested>(
            "SetComputerNameAsync returning SetWithRebootPending requests reboot");

        var state = await stateStore.LoadAsync(CancellationToken.None);
        state.Should().NotBeNull();
        state!.InstanceId.Should().Be("instance-1");
        state.CompletedHandlers.Should().Contain(typeof(SetHostnameHandler).FullName!);

        await windowsOs.Received(1).SetComputerNameAsync("testhost", Arg.Any<CancellationToken>());

        // Second run after the (simulated) reboot — hostname handler is already completed,
        // remaining handlers should run.
        windowsOs.ClearReceivedCalls();
        windowsOs.SetComputerNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SetComputerNameResult.AlreadySet);

        var outcome2 = await runner.RunAsync(CancellationToken.None);
        outcome2.Should().BeOfType<StageRunOutcome.Success>();

        await windowsOs.DidNotReceive().SetComputerNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await windowsOs.Received().CreateLocalUserAsync(
            Arg.Is<LocalUserSpec>(s => s.Name == "admin"),
            Arg.Any<CancellationToken>());
        await windowsOs.Received().SetLocalUserPasswordAsync(
            "admin", "hunter2", false, Arg.Any<CancellationToken>());
        await windowsOs.Received().AddUserToGroupAsync(
            "admin", "Administrators", Arg.Any<CancellationToken>());
        await windowsOs.Received().SetUserSshAuthorizedKeysAsync(
            "admin",
            Arg.Is<IReadOnlyList<string>>(k => k.Count == 1 && k[0] == "ssh-ed25519 AAAA-admin-key"),
            Arg.Any<CancellationToken>());
        await windowsOs.Received().WriteFileAsync(
            @"C:\etc\motd",
            Arg.Is<byte[]>(b => System.Text.Encoding.UTF8.GetString(b) == "hello eryph"),
            false,
            Arg.Any<CancellationToken>());
        await windowsOs.Received().RunShellCommandAsync("echo first", Arg.Any<CancellationToken>());
        await windowsOs.Received().RunArgvCommandAsync(
            Arg.Is<IReadOnlyList<string>>(a => a.Count == 2 && a[0] == "echo" && a[1] == "argv-form"),
            Arg.Any<CancellationToken>());

        await reporter.Received().ReportCompletedAsync(Arg.Any<CancellationToken>());
    }
}
