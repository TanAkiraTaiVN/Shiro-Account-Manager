#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Shiro.Services
{
    public interface IAppLogger
    {
        void Debug(object message);
        void Debug(object message, Exception exception);
        void Info(object message);
        void Info(object message, Exception exception);
        void Warn(object message);
        void Warn(object message, Exception exception);
        void Error(object message);
        void Error(object message, Exception exception);

        bool IsDebugEnabled { get; }
        bool IsInfoEnabled { get; }
        bool IsWarnEnabled { get; }
        bool IsErrorEnabled { get; }
    }

    /// <summary>
    /// High-performance, non-blocking asynchronous rolling file logger.
    /// Replaces legacy log4net with zero external dependencies and channel-based I/O.
    /// </summary>
    public sealed class AppLogger : IAppLogger, IDisposable
    {
        internal readonly record struct LogEntry(
            DateTime Timestamp,
            int ThreadId,
            string Level,
            string MessageText,
            Exception? Exception);

        private const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB
        private readonly string _loggerName;
        private readonly string _logDirectory;
        private readonly string _logFilePath;
        private readonly string _backupFilePath;
        private readonly Channel<LogEntry> _channel;
        private readonly CancellationTokenSource _cts;
        private readonly Task _writerTask;
        private readonly UTF8Encoding _utf8NoBom = new UTF8Encoding(false);
        private static readonly int _newLineByteCount = Encoding.UTF8.GetByteCount(Environment.NewLine);
        private int _disposed;

        public bool IsDebugEnabled => true;
        public bool IsInfoEnabled => true;
        public bool IsWarnEnabled => true;
        public bool IsErrorEnabled => true;

        public AppLogger(string logDirectory, string loggerName = "Account Manager")
        {
            _loggerName = loggerName ?? "Account Manager";
            _logDirectory = logDirectory;
            _logFilePath = Path.Combine(_logDirectory, "log.txt");
            _backupFilePath = Path.Combine(_logDirectory, "log.txt.1");

            Directory.CreateDirectory(_logDirectory);

            _channel = Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            _cts = new CancellationTokenSource();
            _writerTask = Task.Run(() => ProcessLogQueueAsync(_cts.Token));

            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        public void Debug(object? message) => Enqueue("DEBUG", message, null);
        public void Debug(object? message, Exception? exception) => Enqueue("DEBUG", message, exception);

        public void Info(object? message) => Enqueue("INFO", message, null);
        public void Info(object? message, Exception? exception) => Enqueue("INFO", message, exception);

        public void Warn(object? message) => Enqueue("WARN", message, null);
        public void Warn(object? message, Exception? exception) => Enqueue("WARN", message, exception);

        public void Error(object? message) => Enqueue("ERROR", message, null);
        public void Error(object? message, Exception? exception) => Enqueue("ERROR", message, exception);

        private void Enqueue(string level, object? message, Exception? exception)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            string text = message as string ?? message?.ToString() ?? string.Empty;
            var entry = new LogEntry(
                DateTime.Now,
                Environment.CurrentManagedThreadId,
                level,
                text,
                exception);

            _channel.Writer.TryWrite(entry);
        }

        internal static string FormatLogLine(in LogEntry entry, string loggerName)
        {
            string text = entry.Exception != null
                ? $"{entry.MessageText}{Environment.NewLine}{entry.Exception}"
                : entry.MessageText;

            text = LogRedactor.Redact(text);
            string timestamp = entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss,fff");
            return $"{timestamp} [{entry.ThreadId}] {entry.Level,-5} {loggerName} - {text}";
        }

        private async Task ProcessLogQueueAsync(CancellationToken cancellationToken)
        {
            StreamWriter? writer = null;
            long currentLength = 0;

            try
            {
                OpenLogFile(ref writer, ref currentLength);

                ChannelReader<LogEntry> reader = _channel.Reader;
                while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (reader.TryRead(out LogEntry entry))
                    {
                        // A single I/O failure (disk full, file locked by AV, rotation race) must not
                        // terminate the reader loop: the channel is unbounded, so a dead reader would
                        // silently drop all future logs and grow memory without limit.
                        try
                        {
                            string line = FormatLogLine(in entry, _loggerName);
                            if (writer == null)
                                OpenLogFile(ref writer, ref currentLength);

                            int encodedByteCount = _utf8NoBom.GetByteCount(line) + _newLineByteCount;
                            if (currentLength + encodedByteCount > MaxFileSizeBytes)
                            {
                                RotateFile(ref writer, ref currentLength);
                            }

                            if (writer != null)
                            {
                                await writer.WriteLineAsync(line).ConfigureAwait(false);
                                currentLength += encodedByteCount;
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            SafeExecuteHelper.Try(() => writer?.Dispose());
                            writer = null;
                        }
                    }

                    if (writer != null)
                    {
                        try { await writer.FlushAsync().ConfigureAwait(false); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            SafeExecuteHelper.Try(() => writer?.Dispose());
                            writer = null;
                        }
                    }
                }
            }
            catch
            {
                // Never crash the background logging task
            }
            finally
            {
                writer?.Dispose();
            }
        }

        private void OpenLogFile(ref StreamWriter? writer, ref long currentLength)
        {
            try
            {
                writer?.Dispose();
                var fileInfo = new FileInfo(_logFilePath);
                currentLength = fileInfo.Exists ? fileInfo.Length : 0;
                var stream = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                writer = new StreamWriter(stream, _utf8NoBom) { AutoFlush = false };
            }
            catch
            {
                writer = null;
                currentLength = 0;
            }
        }

        private void RotateFile(ref StreamWriter? writer, ref long currentLength)
        {
            try
            {
                writer?.Dispose();
                writer = null;

                if (File.Exists(_backupFilePath))
                {
                    File.Delete(_backupFilePath);
                }

                if (File.Exists(_logFilePath))
                {
                    File.Move(_logFilePath, _backupFilePath);
                }
            }
            catch
            {
                // Ignored during rotation collision
            }

            OpenLogFile(ref writer, ref currentLength);
        }

        private void OnProcessExit(object? sender, EventArgs e)
        {
            Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            _channel.Writer.TryComplete();

            try
            {
                if (!_writerTask.Wait(TimeSpan.FromSeconds(2)))
                {
                    _cts.Cancel();
                    _writerTask.Wait(TimeSpan.FromMilliseconds(500));
                }
            }
            catch (OperationCanceledException) { /* canceled on dispose */ }
            catch (AggregateException) { /* writer task canceled */ }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"AppLogger writer wait on dispose threw: {ex.Message}");
            }

            _cts.Dispose();
        }
    }
}
