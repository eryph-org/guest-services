using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Reporting;
using Eryph.GuestServices.Provisioning.Reporting.Events;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Eryph.GuestServices.Provisioning.Tests.Reporting;

public sealed class ReportingDispatcherTests
{
    private static ReportingEvent.Progress MakeEvent() =>
        new("hello") { Origin = "test" };

    [Fact]
    public async Task EmitAsync_publishes_to_all_applicable_handlers()
    {
        var a = Substitute.For<IReportingHandler>();
        a.IsApplicable.Returns(true);
        var b = Substitute.For<IReportingHandler>();
        b.IsApplicable.Returns(true);

        var dispatcher = new ReportingDispatcher([a, b], NullLogger<ReportingDispatcher>.Instance);
        var evt = MakeEvent();

        await dispatcher.EmitAsync(evt, CancellationToken.None);

        await a.Received(1).PublishAsync(evt, Arg.Any<CancellationToken>());
        await b.Received(1).PublishAsync(evt, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmitAsync_skips_non_applicable_handlers()
    {
        var on = Substitute.For<IReportingHandler>();
        on.IsApplicable.Returns(true);
        var off = Substitute.For<IReportingHandler>();
        off.IsApplicable.Returns(false);

        var dispatcher = new ReportingDispatcher([on, off], NullLogger<ReportingDispatcher>.Instance);
        var evt = MakeEvent();

        await dispatcher.EmitAsync(evt, CancellationToken.None);

        await on.Received(1).PublishAsync(evt, Arg.Any<CancellationToken>());
        await off.DidNotReceive().PublishAsync(Arg.Any<ReportingEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmitAsync_swallows_handler_exception_and_continues_with_others()
    {
        var bad = Substitute.For<IReportingHandler>();
        bad.IsApplicable.Returns(true);
        bad.PublishAsync(Arg.Any<ReportingEvent>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("boom"));

        var good = Substitute.For<IReportingHandler>();
        good.IsApplicable.Returns(true);

        var dispatcher = new ReportingDispatcher([bad, good], NullLogger<ReportingDispatcher>.Instance);
        var evt = MakeEvent();

        var act = async () => await dispatcher.EmitAsync(evt, CancellationToken.None);

        await act.Should().NotThrowAsync();
        await good.Received(1).PublishAsync(evt, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmitAsync_propagates_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = Substitute.For<IReportingHandler>();
        handler.IsApplicable.Returns(true);
        handler.PublishAsync(Arg.Any<ReportingEvent>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException(cts.Token));

        var dispatcher = new ReportingDispatcher([handler], NullLogger<ReportingDispatcher>.Instance);

        var act = async () => await dispatcher.EmitAsync(MakeEvent(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task EmitAsync_with_no_handlers_is_a_noop()
    {
        var dispatcher = new ReportingDispatcher([], NullLogger<ReportingDispatcher>.Instance);

        var act = async () => await dispatcher.EmitAsync(MakeEvent(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
