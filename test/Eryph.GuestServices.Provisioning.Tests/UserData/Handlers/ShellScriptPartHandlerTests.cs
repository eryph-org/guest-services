using System.Text;
using AwesomeAssertions;
using Eryph.GuestServices.Provisioning.UserData;
using Eryph.GuestServices.Provisioning.UserData.Handlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eryph.GuestServices.Provisioning.Tests.UserData.Handlers;

public sealed class ShellScriptPartHandlerTests
{
    [Fact]
    public async Task ProcessAsync_CapturesPowerShellScript()
    {
        var handler = new ShellScriptPartHandler(NullLogger<ShellScriptPartHandler>.Instance);
        var body = Encoding.UTF8.GetBytes("#ps1\nWrite-Host hello\n");
        var part = new UserDataPart("text/x-shellscript", body, "boot.ps1");
        var ctx = new TestResolutionContext();

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        ctx.Scripts.Should().ContainSingle();
        ctx.Scripts[0].Kind.Should().Be(ScriptKind.PowerShell);
        ctx.Scripts[0].Filename.Should().Be("boot.ps1");
        ctx.Scripts[0].Body.Should().Equal(body);
    }

    // POSIX shebang on a Windows guest: detector resolves to ScriptKind.Other
    // (no POSIX shell available). Part is still captured — the module logs
    // and skips it.
    [Fact]
    public async Task ProcessAsync_BashShebangOnWindows_CapturesAsOther()
    {
        var handler = new ShellScriptPartHandler(NullLogger<ShellScriptPartHandler>.Instance);
        var body = Encoding.UTF8.GetBytes("#!/bin/bash\necho hello\n");
        var part = new UserDataPart("text/x-shellscript", body, null);
        var ctx = new TestResolutionContext();

        await handler.ProcessAsync(part, ctx, CancellationToken.None);

        ctx.Scripts.Should().ContainSingle();
        ctx.Scripts[0].Kind.Should().Be(ScriptKind.Other);
    }
}
