using System.Text;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Core.Logging;

/// <summary>
/// Minimal, dependency-free <see cref="ILoggerProvider"/> that appends the
/// agent's operational log to a single file (see <see cref="AgentPaths.LogFile"/>).
/// <para>
/// It exists so the support bundle (<c>collect-logs</c>) captures the agent's
/// own diagnostics. Without it the service's only sink is the Windows Event Log
/// (wired by <c>AddWindowsService()</c>) / the systemd journal, which the
/// bundle never reads — so a bundle from a guest that ran no user-data scripts
/// contained nothing but <c>state.json</c> + <c>version.txt</c> (issue #45).
/// </para>
/// <para>
/// This is a global agent concern, not a provisioning one: it captures
/// everything the <c>egs-service</c> process logs (remote access included), so
/// it lives in Core and takes an explicit path rather than reaching into any
/// feature's path helper.
/// </para>
/// <para>
/// Volume is low (a few hundred lines per boot) so a lock-guarded
/// open-append-close per line is sufficient and keeps the file unlocked
/// between writes. A size cap with a single <c>.1</c> backup keeps the file
/// from growing without bound across reboots.
/// </para>
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    // 5 MiB before rolling. One backup is kept (agent.log.1); the previous
    // backup is overwritten. Hundreds of lines per boot means this spans many
    // boots before rolling, which is what a support engineer wants.
    private const long DefaultMaxBytes = 5 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _filePath;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private bool _disposed;

    public FileLoggerProvider(string filePath, long maxBytes = DefaultMaxBytes)
    {
        _filePath = filePath;
        _maxBytes = maxBytes;

        // Create the log directory eagerly so the file (and the directory
        // collect-logs harvests) exist even on a guest that never ran a
        // user-data script. Best-effort: a failure here must not take down
        // the host's logging pipeline.
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    /// <summary>
    /// Appends a fully-formatted line (including its trailing newline) to the
    /// log file. Serialised across loggers and best-effort: any I/O failure is
    /// swallowed so a transient file lock can never crash the agent.
    /// </summary>
    internal void Append(string line)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            try
            {
                RollIfNeeded();

                // FileShare.ReadWrite so a concurrent reader (or a second
                // egs-service process — e.g. an operator `run` while the
                // service is up) does not get an access violation. Lines are
                // short and rare, so interleaving across processes is benign.
                using var stream = new FileStream(
                    _filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Utf8NoBom);
                writer.Write(line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void RollIfNeeded()
    {
        var info = new FileInfo(_filePath);
        if (!info.Exists || info.Length < _maxBytes)
            return;

        var backup = _filePath + ".1";
        try
        {
            if (File.Exists(backup))
                File.Delete(backup);
            File.Move(_filePath, backup);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }
}
