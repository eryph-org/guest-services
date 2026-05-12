// E2E test driver for the SHELL channel of the eryph guest services.
// ssh.exe -tt cannot be redirected/captured from a script (it writes
// channel output to the Windows console buffer, not stdout). This probe
// uses Microsoft.DevTunnels.Ssh — the same library that egs-tool itself
// uses to talk to the egs SSH server — and drives the protocol directly:
// pty-req, optional env requests, shell, then pumps channel output to
// stdout for a fixed window.
//
// Usage:
//   egs-shell-probe --vm-id <guid> [--env KEY=VAL ...] [--input <line> ...]
//                   [--timeout-ms 3000] [--key-path <path>]

using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using Eryph.GuestServices.Core;
using Eryph.GuestServices.DevTunnels.Ssh.Extensions.Messages;
using Eryph.GuestServices.Sockets;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Keys;
using Microsoft.DevTunnels.Ssh.Messages;

Guid? vmId = null;
var envVars = new List<(string Name, string Value)>();
var inputLines = new List<string>();
var timeoutMs = 3000;
string? keyPath = null;

for (var i = 0; i < args.Length; i++)
{
    var flag = args[i];
    switch (flag)
    {
        case "--vm-id":
            if (!TryConsumeValue(args, ref i, out var vmIdRaw))
                return Fail($"{flag} requires a value");
            vmId = Guid.Parse(vmIdRaw);
            break;
        case "--env":
            if (!TryConsumeValue(args, ref i, out var envRaw))
                return Fail($"{flag} requires KEY=VAL");
            var pair = envRaw.Split('=', 2);
            if (pair.Length != 2)
                return Fail($"--env value must be KEY=VAL, got '{envRaw}'");
            envVars.Add((pair[0], pair[1]));
            break;
        case "--input":
            if (!TryConsumeValue(args, ref i, out var inputRaw))
                return Fail($"{flag} requires a value");
            inputLines.Add(inputRaw);
            break;
        case "--timeout-ms":
            if (!TryConsumeValue(args, ref i, out var timeoutRaw))
                return Fail($"{flag} requires a value");
            timeoutMs = int.Parse(timeoutRaw);
            break;
        case "--key-path":
            if (!TryConsumeValue(args, ref i, out var keyRaw))
                return Fail($"{flag} requires a value");
            keyPath = keyRaw;
            break;
        default:
            return Fail($"unknown argument: {flag}");
    }
}

if (vmId is null)
    return Fail("--vm-id is required");

keyPath ??= Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "eryph", "guest-services", "private", "id_egs");

if (!File.Exists(keyPath))
    return Fail($"SSH key not found at {keyPath}");

var keyBytes = await File.ReadAllBytesAsync(keyPath);
var keyPair = KeyPair.ImportKeyBytes(keyBytes);

using var clientSocket = await SocketFactory.CreateClientSocket(vmId.Value, Constants.ServiceId);
await using var clientStream = new NetworkStream(clientSocket, ownsSocket: false);

var sshConfig = new SshSessionConfiguration();
var session = new SshClientSession(sshConfig, new TraceSource("EgsShellProbe"));
session.Authenticating += (_, e) =>
{
    if (e.AuthenticationType == SshAuthenticationType.ServerPublicKey)
    {
        // Hyper-V socket isolation is sufficient — same trust model as egs-tool.
        e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal());
    }
};

await session.ConnectAsync(clientStream);
if (!await session.AuthenticateAsync(new SshClientCredentials("egs", keyPair)))
    return Fail("authentication failed");

var channel = await session.OpenChannelAsync();

// SshStream wraps the channel and handles the mandatory DataReceived /
// AdjustWindow plumbing automatically.
var stream = new SshStream(channel);

// pty-req — gives the server a sized PTY so the shell renders normally.
// (ShellService also creates the forwarder lazily on the first env or shell
// request, so pty-req is not strictly required, but providing realistic
// terminal dimensions matches what an interactive client would send.)
var ptyOk = await channel.RequestAsync(
    new TerminalRequestMessage
    {
        Term = "xterm-256color",
        Columns = 80,
        Rows = 25,
    });
if (!ptyOk)
    return Fail("server rejected pty-req");

// env requests — these flow into PtyForwarder.SetEnvironmentVariable on the
// server. Used to test the SSH-sent SHELL/SHELL_ARGS override path.
foreach (var (name, value) in envVars)
{
    var envOk = await channel.RequestAsync(
        new EnvironmentVariableRequestMessage { Name = name, Value = value });
    if (!envOk)
        return Fail($"server rejected env {name}");
}

var shellOk = await channel.RequestAsync(new ShellRequestMessage());
if (!shellOk)
    return Fail("server rejected shell");

if (inputLines.Count > 0)
{
    // Append CR+LF so PowerShell consumes each line as a complete command.
    var inputBytes = Encoding.UTF8.GetBytes(
        string.Join("\r\n", inputLines) + "\r\n");
    await stream.WriteAsync(inputBytes);
    await stream.FlushAsync();
}

// Pump channel output for the configured window. The channel will normally
// stay open until the shell exits; the cancellation cuts the read loop
// short.
var captured = new MemoryStream();
using var cts = new CancellationTokenSource(timeoutMs);
try
{
    await stream.CopyToAsync(captured, cts.Token);
}
catch (OperationCanceledException) { /* expected */ }

await Console.OpenStandardOutput().WriteAsync(captured.ToArray());
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine($"egs-shell-probe: {message}");
    return 1;
}

static bool TryConsumeValue(string[] args, ref int index, out string value)
{
    if (index + 1 >= args.Length)
    {
        value = string.Empty;
        return false;
    }
    value = args[++index];
    return true;
}
