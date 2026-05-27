using System.Net;
using System.Net.Http;
using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.DataSources.OpenStack;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.DataSources;

public sealed class OpenStackMetadataClientTests
{
    private const string BaseUrl = "http://169.254.169.254";

    private static OpenStackMetadataClient NewClient(
        Func<HttpRequestMessage, int, HttpResponseMessage> respond,
        int maxAttempts = 3)
    {
        var attempt = 0;
        var handler = new FakeHandler(req =>
        {
            attempt++;
            return respond(req, attempt);
        });
        return new OpenStackMetadataClient(
            handler,
            disposeHandler: true,
            BaseUrl,
            TimeSpan.FromSeconds(5),
            maxAttempts,
            retryDelay: (_, _) => Task.CompletedTask,
            NullLogger<OpenStackMetadataClient>.Instance);
    }

    private static HttpResponseMessage Text(HttpStatusCode status, string body = "") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8) };

    [Fact]
    public async Task GetAsync_returns_body_bytes_on_200()
    {
        using var client = NewClient((_, _) => Text(HttpStatusCode.OK, "hello"));

        var bytes = await client.GetAsync("openstack/latest/meta_data.json", CancellationToken.None);

        bytes.Should().NotBeNull();
        Encoding.UTF8.GetString(bytes!).Should().Be("hello");
    }

    [Fact]
    public async Task GetAsync_returns_null_on_404_absent_optional()
    {
        using var client = NewClient((_, _) => Text(HttpStatusCode.NotFound));

        var bytes = await client.GetAsync("openstack/latest/vendor_data.json", CancellationToken.None);

        bytes.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_fails_fast_on_403_without_retry()
    {
        var calls = 0;
        using var client = NewClient((_, attempt) =>
        {
            calls = attempt;
            return Text(HttpStatusCode.Forbidden);
        });

        var act = async () => await client.GetAsync("openstack/latest/meta_data.json", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        calls.Should().Be(1, "a non-retryable 4xx must not be retried");
    }

    [Fact]
    public async Task GetAsync_retries_on_503_then_succeeds()
    {
        using var client = NewClient((_, attempt) =>
            attempt == 1 ? Text(HttpStatusCode.ServiceUnavailable) : Text(HttpStatusCode.OK, "ok"));

        var bytes = await client.GetAsync("openstack/latest/meta_data.json", CancellationToken.None);

        Encoding.UTF8.GetString(bytes!).Should().Be("ok");
    }

    [Fact]
    public async Task GetAsync_throws_after_exhausting_retries_on_persistent_5xx()
    {
        var calls = 0;
        using var client = NewClient(
            (_, attempt) => { calls = attempt; return Text(HttpStatusCode.BadGateway); },
            maxAttempts: 3);

        var act = async () => await client.GetAsync("openstack/latest/meta_data.json", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        calls.Should().Be(3);
    }

    [Fact]
    public async Task GetAsync_retries_on_connection_error()
    {
        var calls = 0;
        using var client = NewClient((_, attempt) =>
        {
            calls = attempt;
            if (attempt == 1)
                throw new HttpRequestException("connection refused");
            return Text(HttpStatusCode.OK, "recovered");
        });

        var bytes = await client.GetAsync("openstack/latest/meta_data.json", CancellationToken.None);

        Encoding.UTF8.GetString(bytes!).Should().Be("recovered");
        calls.Should().Be(2);
    }

    [Fact]
    public async Task ProbeLivenessAsync_true_on_200()
    {
        using var client = NewClient((_, _) => Text(HttpStatusCode.OK, "2018-08-27\nlatest"));

        (await client.ProbeLivenessAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task ProbeLivenessAsync_false_on_connection_error()
    {
        using var client = NewClient((_, _) => throw new HttpRequestException("no route to host"));

        (await client.ProbeLivenessAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ProbeLivenessAsync_false_on_404()
    {
        using var client = NewClient((_, _) => Text(HttpStatusCode.NotFound));

        (await client.ProbeLivenessAsync(CancellationToken.None)).Should().BeFalse();
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
