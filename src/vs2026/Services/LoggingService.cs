using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PipelinesExplorer.VisualStudio.Services;

/// <summary>
/// Log levels supported by <see cref="LoggingService"/>. Mirrors the VS Code
/// client's <c>LoggingService</c> so the two extensions produce comparable logs.
/// </summary>
public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
    None,
}

/// <summary>
/// Thread-safe logger that writes timestamped, level-prefixed entries both to
/// a rolling log file under <c>%LocalAppData%\PipelinesExplorer\logs</c> and to
/// <see cref="Trace"/> (so the messages appear in the Visual Studio debugger
/// output window). An optional Visual Studio output channel can be wired up
/// later via <see cref="AttachSink"/> once the extensibility runtime is
/// available; until then messages are still captured to disk.
/// </summary>
public sealed class LoggingService : IDisposable
{
    private readonly System.Threading.Lock _gate = new();
    private readonly StreamWriter? _file;
    private readonly string? _logFilePath;
    private LogLevel _level = LogLevel.Info;
    private Action<string>? _sink;

    public LoggingService(string channelName = "Pipelines Explorer")
    {
        ChannelName = channelName;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PipelinesExplorer",
                "logs");
            Directory.CreateDirectory(dir);
            _logFilePath = Path.Combine(dir, $"extension-{DateTime.Now:yyyyMMdd}.log");
            _file = new StreamWriter(
                new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }
        catch
        {
            // Logging must never throw. Fall back to Trace only.
            _file = null;
        }
    }

    public string ChannelName { get; }

    /// <summary>Path of the on-disk log file, or <c>null</c> if the file could not be opened.</summary>
    public string? LogFilePath => _logFilePath;

    public LogLevel Level
    {
        get => _level;
        set => _level = value;
    }

    /// <summary>
    /// Attach a sink (typically an <c>OutputWindow</c> writer) that receives
    /// every formatted log line going forward. Pass <c>null</c> to detach.
    /// </summary>
    public void AttachSink(Action<string>? sink) => Volatile.Write(ref _sink, sink);

    public void Debug(string message, object? data = null) => Write(LogLevel.Debug, message, data);
    public void Info(string message, object? data = null) => Write(LogLevel.Info, message, data);
    public void Warn(string message, object? data = null) => Write(LogLevel.Warn, message, data);

    public void Error(string message, Exception? error = null)
    {
        if (_level == LogLevel.None)
        {
            return;
        }

        WriteLine(Format(LogLevel.Error, message));
        if (error is not null)
        {
            WriteLine(Format(LogLevel.Error, error.Message));
            if (!string.IsNullOrEmpty(error.StackTrace))
            {
                WriteLine(error.StackTrace!);
            }
        }
    }

    public void Error(string message, string error)
    {
        if (_level == LogLevel.None)
        {
            return;
        }

        WriteLine(Format(LogLevel.Error, message));
        WriteLine(error);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _file?.Dispose();
        }
    }

    private void Write(LogLevel level, string message, object? data)
    {
        if (!ShouldLog(level))
        {
            return;
        }

        WriteLine(Format(level, message));
        if (data is not null)
        {
            try
            {
                WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                WriteLine(Format(LogLevel.Warn, $"Failed to serialize log payload: {ex.Message}"));
            }
        }
    }

    private bool ShouldLog(LogLevel level) => level >= _level;

    private static string Format(LogLevel level, string message)
    {
        var label = level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Info => "INFO",
            LogLevel.Warn => "WARN",
            LogLevel.Error => "ERROR",
            _ => level.ToString().ToUpperInvariant(),
        };
        return $"[\"{label}\" - {DateTime.Now:HH:mm:ss}] {message}";
    }

    private void WriteLine(string line)
    {
        Trace.WriteLine(line);
        Action<string>? sink = Volatile.Read(ref _sink);
        try
        {
            sink?.Invoke(line);
        }
        catch
        {
            // Sinks must never break logging.
        }

        if (_file is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                _file.WriteLine(line);
            }
            catch
            {
                // Swallow IO errors silently.
            }
        }
    }
}
