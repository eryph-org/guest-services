using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.UserData.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.UserData.Handlers;

public sealed class BoothookPartHandlerTests
{
    [Fact]
    public async Task ProcessAsync_CapturesBoothookBytes()
    {
        var handler = new BoothookPartHandler(NullLogger<BoothookPartHandler>.Instance);
        var body = Encoding.UTF8.GetBytes("#cloud-boothook\necho early\n");
        var part = new UserDataPart("text/cloud-boothook", body, "boothook-1");
        var ctx = new TestResolutionContext();

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        ctx.Boothooks.Should().ContainSingle();
        ctx.Boothooks[0].Filename.Should().Be("boothook-1");
        ctx.Boothooks[0].Body.Should().Equal(body);
    }
}
