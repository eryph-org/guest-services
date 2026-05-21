using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Serialization;
using Eryph.GuestServices.Provisioning.Stages;
using Eryph.GuestServices.Provisioning.State;
using Eryph.GuestServices.Provisioning.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using CloudConfig = global::Eryph.GuestServices.CloudConfig.CloudConfig;

namespace Eryph.GuestServices.Provisioning.Tests.Stages;

public sealed class StageRunnerTests
{
    [Fact]
    public async Task RunAsync_returns_NoDataSource_when_locator_returns_null()
    {
        var locator = Substitute.For<IDataSourceLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<DataSourceResult?>(null));

        var runner = BuildRunner(locator: locator);
        var result = await runner.RunAsync(CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.NoDataSource>();
    }

    [Fact]
    public async Task RunAsync_runs_handlers_in_stage_order_and_reports_completed()
    {
        var data = MakeData("i-1");
        var reporter = Substitute.For<IHostStatusReporter>();
        var stateStore = new InMemoryStateStore();

        var trace = new List<string>();
        var handlers = new IHandler[]
        {
            new HostnameRecordingHandler(trace),
            new FilesRecordingHandler(trace),
            new UsersRecordingHandler(trace),
        };

        var runner = BuildRunner(
            locator: LocatorReturning(data),
            stateStore: stateStore,
            handlers: handlers,
            reporter: reporter);

        var result = await runner.RunAsync(CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.Success>();
        trace.Should().Equal("hostname", "users", "files");

        await reporter.Received().ReportStartedAsync("i-1", Arg.Any<CancellationToken>());
        await reporter.Received().ReportCompletedAsync(Arg.Any<CancellationToken>());

        stateStore.Current!.CompletedStages.Should().Contain("Discovery");
        stateStore.Current.CompletedStages.Should().Contain("Hostname");
        stateStore.Current.CompletedStages.Should().Contain("Users");
        stateStore.Current.CompletedStages.Should().Contain("Files");
        stateStore.Current.CompletedStages.Should().Contain("Commands");
        stateStore.Current.CompletedStages.Should().Contain("Finalize");
        stateStore.Current.CompletedHandlers.Should().HaveCount(3);
    }

    [Fact]
    public async Task RunAsync_skips_handlers_already_completed()
    {
        var data = MakeData("i-1");
        var stateStore = new InMemoryStateStore
        {
            Current = new ProvisioningState
            {
                InstanceId = "i-1",
                CompletedHandlers = [typeof(HostnameRecordingHandler).FullName!],
            },
        };

        var trace = new List<string>();
        var handlers = new IHandler[]
        {
            new HostnameRecordingHandler(trace),
            new UsersRecordingHandler(trace),
        };

        var runner = BuildRunner(LocatorReturning(data), stateStore: stateStore, handlers: handlers);
        await runner.RunAsync(CancellationToken.None);

        trace.Should().Equal("users");
    }

    [Fact]
    public async Task RunAsync_completed_instance_resumes_without_running_handlers_and_still_reports_completed()
    {
        var data = MakeData("i-1");
        var reporter = Substitute.For<IHostStatusReporter>();

        var trace = new List<string>();
        var handlers = new IHandler[]
        {
            new HostnameRecordingHandler(trace),
            new UsersRecordingHandler(trace),
            new FilesRecordingHandler(trace),
        };

        var allStageNames = Enum.GetNames(typeof(Stage));
        var allHandlerKeys = handlers.Select(h => h.GetType().FullName!);

        var stateStore = new InMemoryStateStore
        {
            Current = new ProvisioningState
            {
                InstanceId = "i-1",
                CompletedStages = new HashSet<string>(allStageNames, StringComparer.Ordinal),
                CompletedHandlers = new HashSet<string>(allHandlerKeys, StringComparer.Ordinal),
            },
        };

        var runner = BuildRunner(LocatorReturning(data), stateStore: stateStore, handlers: handlers, reporter: reporter);
        var result = await runner.RunAsync(CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.Success>();
        trace.Should().BeEmpty("all handlers were already completed in a prior run");
        await reporter.Received().ReportCompletedAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_resets_state_when_instance_id_changes()
    {
        var data = MakeData("new-instance");
        var stateStore = new InMemoryStateStore
        {
            Current = new ProvisioningState
            {
                InstanceId = "old-instance",
                CompletedHandlers = [typeof(HostnameRecordingHandler).FullName!],
            },
        };

        var trace = new List<string>();
        var handlers = new IHandler[] { new HostnameRecordingHandler(trace) };

        var runner = BuildRunner(LocatorReturning(data), stateStore: stateStore, handlers: handlers);
        await runner.RunAsync(CancellationToken.None);

        trace.Should().Equal("hostname");
        stateStore.Resets.Should().Be(1);
        stateStore.Current!.InstanceId.Should().Be("new-instance");
    }

    [Fact]
    public async Task RunAsync_returns_RebootRequested_and_records_handler_completed()
    {
        var data = MakeData("i-1");
        var stateStore = new InMemoryStateStore();
        var reporter = Substitute.For<IHostStatusReporter>();

        var trace = new List<string>();
        var handlers = new IHandler[]
        {
            new RebootingHandler("needs-reboot"),
            new UsersRecordingHandler(trace),
        };

        var runner = BuildRunner(LocatorReturning(data), stateStore: stateStore, handlers: handlers, reporter: reporter);
        var result = await runner.RunAsync(CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.RebootRequested>()
            .Which.Reason.Should().Be("needs-reboot");
        trace.Should().BeEmpty();
        stateStore.Current!.CompletedHandlers.Should().Contain(typeof(RebootingHandler).FullName!);
        stateStore.Current.RebootCount.Should().Be(1);
        await reporter.Received().ReportRebootPendingAsync("needs-reboot", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_returns_Failed_when_handler_returns_failed()
    {
        var data = MakeData("i-1");
        var reporter = Substitute.For<IHostStatusReporter>();
        var stateStore = new InMemoryStateStore();

        var handlers = new IHandler[] { new FailingHandler("boom") };

        var runner = BuildRunner(LocatorReturning(data), stateStore: stateStore, handlers: handlers, reporter: reporter);
        var result = await runner.RunAsync(CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.Failed>().Which.Reason.Should().Be("boom");
        await reporter.Received().ReportFailedAsync(Arg.Is<string>(s => s.Contains("boom")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_returns_Failed_when_handler_throws()
    {
        var data = MakeData("i-1");
        var reporter = Substitute.For<IHostStatusReporter>();
        var handlers = new IHandler[] { new ThrowingHandler() };

        var runner = BuildRunner(LocatorReturning(data), handlers: handlers, reporter: reporter);
        var result = await runner.RunAsync(CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.Failed>();
        await reporter.Received().ReportFailedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_returns_Failed_when_userdata_cannot_be_parsed()
    {
        var data = MakeData("i-1", userData: "not valid yaml maybe");
        var serializer = Substitute.For<ICloudConfigSerializer>();
        serializer.Deserialize(Arg.Any<string>()).Throws(new InvalidOperationException("bad yaml"));
        var reporter = Substitute.For<IHostStatusReporter>();

        var runner = BuildRunner(LocatorReturning(data), serializer: serializer, reporter: reporter);
        var result = await runner.RunAsync(CancellationToken.None);

        result.Should().BeOfType<StageRunOutcome.Failed>();
        await reporter.Received().ReportFailedAsync(Arg.Is<string>(s => s.StartsWith("userdata-parse")), Arg.Any<CancellationToken>());
    }

    private static IDataSourceLocator LocatorReturning(DataSourceResult result)
    {
        var locator = Substitute.For<IDataSourceLocator>();
        locator.LocateAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<DataSourceResult?>(result));
        return locator;
    }

    private static DataSourceResult MakeData(string instanceId, string? userData = null) => new()
    {
        SourceName = "test",
        InstanceId = instanceId,
        UserData = userData ?? "",
    };

    private static StageRunner BuildRunner(
        IDataSourceLocator? locator = null,
        ICloudConfigSerializer? serializer = null,
        IStateStore? stateStore = null,
        IHandler[]? handlers = null,
        IHostStatusReporter? reporter = null)
    {
        locator ??= Substitute.For<IDataSourceLocator>();
        serializer ??= new FakeSerializer();
        stateStore ??= new InMemoryStateStore();
        reporter ??= Substitute.For<IHostStatusReporter>();

        return new StageRunner(
            locator,
            serializer,
            stateStore,
            handlers ?? [],
            reporter,
            Substitute.For<IWindowsOs>(),
            NullLogger<StageRunner>.Instance);
    }

    private sealed class FakeSerializer : ICloudConfigSerializer
    {
        public global::Eryph.GuestServices.CloudConfig.CloudConfig Deserialize(string yaml) => new();
    }

    private sealed class InMemoryStateStore : IStateStore
    {
        public ProvisioningState? Current { get; set; }
        public int Resets { get; private set; }

        public Task<ProvisioningState?> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Current);

        public Task SaveAsync(ProvisioningState state, CancellationToken cancellationToken)
        {
            Current = state;
            return Task.CompletedTask;
        }

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            Current = null;
            Resets++;
            return Task.CompletedTask;
        }
    }

    [Stage(Stage.Hostname)]
    internal sealed class HostnameRecordingHandler(List<string> trace) : IHandler
    {
        public Task<HandlerOutcome> ApplyAsync(global::Eryph.GuestServices.CloudConfig.CloudConfig config, IHandlerContext context, CancellationToken cancellationToken)
        {
            trace.Add("hostname");
            return Task.FromResult(HandlerOutcome.Ok());
        }
    }

    [Stage(Stage.Users)]
    internal sealed class UsersRecordingHandler(List<string> trace) : IHandler
    {
        public Task<HandlerOutcome> ApplyAsync(global::Eryph.GuestServices.CloudConfig.CloudConfig config, IHandlerContext context, CancellationToken cancellationToken)
        {
            trace.Add("users");
            return Task.FromResult(HandlerOutcome.Ok());
        }
    }

    [Stage(Stage.Files)]
    internal sealed class FilesRecordingHandler(List<string> trace) : IHandler
    {
        public Task<HandlerOutcome> ApplyAsync(global::Eryph.GuestServices.CloudConfig.CloudConfig config, IHandlerContext context, CancellationToken cancellationToken)
        {
            trace.Add("files");
            return Task.FromResult(HandlerOutcome.Ok());
        }
    }

    [Stage(Stage.Hostname)]
    private sealed class RebootingHandler(string reason) : IHandler
    {
        public Task<HandlerOutcome> ApplyAsync(global::Eryph.GuestServices.CloudConfig.CloudConfig config, IHandlerContext context, CancellationToken cancellationToken) =>
            Task.FromResult(HandlerOutcome.Reboot(reason));
    }

    [Stage(Stage.Hostname)]
    private sealed class FailingHandler(string reason) : IHandler
    {
        public Task<HandlerOutcome> ApplyAsync(global::Eryph.GuestServices.CloudConfig.CloudConfig config, IHandlerContext context, CancellationToken cancellationToken) =>
            Task.FromResult(HandlerOutcome.Fail(reason));
    }

    [Stage(Stage.Hostname)]
    private sealed class ThrowingHandler : IHandler
    {
        public Task<HandlerOutcome> ApplyAsync(global::Eryph.GuestServices.CloudConfig.CloudConfig config, IHandlerContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}
