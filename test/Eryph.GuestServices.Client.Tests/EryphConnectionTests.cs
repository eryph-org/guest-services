using AwesomeAssertions;

namespace Eryph.GuestServices.Client.Tests;

public class EryphConnectionTests
{
    [Theory]
    [InlineData("https://localhost:8000/compute", "catlets/abc/guest-services/ssh-channel/connect",
        "https://localhost:8000/compute/v1/catlets/abc/guest-services/ssh-channel/connect")]
    [InlineData("https://localhost:8000/compute/", "/catlets/abc",
        "https://localhost:8000/compute/v1/catlets/abc")]
    public void BuildComputeUri_inserts_version_segment_and_normalizes_slashes(
        string endpoint, string relativePath, string expected)
    {
        // For() ignores credentials for URI building; pass null to exercise the
        // owned URI logic in isolation without constructing a real credential.
        var connection = EryphConnection.For(null!, new Uri(endpoint));

        connection.BuildComputeUri(relativePath).AbsoluteUri.Should().Be(expected);
    }

    [Fact]
    public void For_exposes_the_supplied_compute_endpoint()
    {
        var endpoint = new Uri("https://eryph.example/compute");

        var connection = EryphConnection.For(null!, endpoint);

        connection.ComputeEndpoint.Should().Be(endpoint);
    }
}
