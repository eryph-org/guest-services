using System.Text;
using Microsoft.Extensions.Logging;

namespace Eryph.GuestServices.Core.Logging;

/// <summary>
/// The <see cref="ILogger"/> returned by <see cref="FileLoggerProvider"/>.
/// Formats one line per event as
/// <c>2026-06-12 14:03:01.123 +02:00 [INF] Category[eventId] message</c>,
/// with the exception (if any) appended on following lines. Level filtering is
/// left to the host's <c>LoggerFactory</c> (driven by the <c>Logging</c>
/// section of appsettings.json), which only forwards events that pass the
/// configured rules.
/// </summary>
internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly FileLoggerProvider _provider;

    public FileLogger(string category, FileLoggerProvider provider)
    {
        _category = category;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        ArgumentNullException.ThrowIfNull(formatter);

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception is null)
            return;

        var builder = new StringBuilder();
        builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
        builder.Append(" [").Append(ShortLevel(logLevel)).Append("] ");
        builder.Append(_category);
        if (eventId.Id != 0)
            builder.Append('[').Append(eventId.Id).Append(']');
        builder.Append(' ').Append(message);
        if (exception is not null)
            builder.Append(Environment.NewLine).Append(exception);
        builder.Append(Environment.NewLine);

        _provider.Append(builder.ToString());
    }

    private static string ShortLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };
}
