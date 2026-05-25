using System.Net;
using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.Configuration;
using Eryph.GuestServices.Provisioning.UserData;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.UserData;

public sealed class UrlHelperTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "egs-urlhelper-" + Guid.NewGuid().ToString("N"));

    public UrlHelperTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task FetchAsync_ReadsFileUrl()
    {
        var path = Path.Combine(_tempDir, "payload.txt");
        await File.WriteAllTextAsync(path, "hello urlhelper");
        var url = new Uri(path).AbsoluteUri;

        var helper = new UrlHelper(NullLogger<UrlHelper>.Instance);

        var bytes = await helper.FetchAsync(url, CancellationToken.None);

        Encoding.UTF8.GetString(bytes).Should().Be("hello urlhelper");
    }

    [Fact]
    public async Task FetchAsync_RejectsUnsupportedScheme()
    {
        var helper = new UrlHelper(NullLogger<UrlHelper>.Instance);
        var act = async () => await helper.FetchAsync("ftp://example.com/file", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task FetchAsync_UnderCap_Succeeds()
    {
        var path = Path.Combine(_tempDir, "small.bin");
        await File.WriteAllBytesAsync(path, new byte[50]);
        var url = new Uri(path).AbsoluteUri;

        var helper = new UrlHelper(NullLogger<UrlHelper>.Instance, SettingsWithCap(100));

        var bytes = await helper.FetchAsync(url, CancellationToken.None);

        bytes.Length.Should().Be(50);
    }

    [Fact]
    public async Task FetchAsync_StreamingGuard_ThrowsWhenBodyExceedsCap()
    {
        // file:// has no Content-Length, so this exercises the bounded-read
        // guard rather than the header check.
        var path = Path.Combine(_tempDir, "big.bin");
        await File.WriteAllBytesAsync(path, new byte[500]);
        var url = new Uri(path).AbsoluteUri;

        var helper = new UrlHelper(NullLogger<UrlHelper>.Instance, SettingsWithCap(100));
        var act = async () => await helper.FetchAsync(url, CancellationToken.None);

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("cap");
    }

    [Fact]
    public async Task FetchAsync_RejectsOversizedContentLength()
    {
        using var server = new LocalHttpServer(ctx =>
        {
            var body = new byte[500];
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body, 0, body.Length);
        });

        var helper = new UrlHelper(NullLogger<UrlHelper>.Instance, SettingsWithCap(100));
        var act = async () => await helper.FetchAsync(server.Url, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    private static ProvisioningSettings SettingsWithCap(long maxBytes) =>
        new() { UserData = new UserDataSettings { FetchMaxBytes = maxBytes, FetchMaxAttempts = 1 } };

    // Minimal in-process HTTP server so the HTTP fetch path (and its
    // Content-Length check) is exercised against a real socket.
    private sealed class LocalHttpServer : IDisposable
    {
        private readonly HttpListener _listener;

        public LocalHttpServer(Action<HttpListenerContext> handle)
        {
            var port = GetFreePort();
            Url = $"http://127.0.0.1:{port}/payload";
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    handle(ctx);
                    ctx.Response.Close();
                }
                catch
                {
                    // listener disposed mid-flight; nothing to do.
                }
            });
        }

        public string Url { get; }

        public void Dispose() => _listener.Close();

        private static int GetFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }
}
