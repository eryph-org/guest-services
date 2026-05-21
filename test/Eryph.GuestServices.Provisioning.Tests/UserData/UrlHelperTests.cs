using System.Text;
using AwesomeAssertions;
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
}
