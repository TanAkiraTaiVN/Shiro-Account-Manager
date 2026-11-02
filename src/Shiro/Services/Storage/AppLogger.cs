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
        private const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB
        private readonly string _loggerName;
        private readonly string _logDirectory;
        private readonly string _logFilePath;
        private readonly string _backupFilePath;
        private readonly Channel<string> _channel;
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

            _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
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

            string text = message?.ToString() ?? string.Empty;
            if (exception != null)
                text = $"{text}{Environment.NewLine}{exception}";

            text = LogRedactor.Redact(text);

            int threadId = Environment.CurrentManagedThreadId;
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss,fff");
            string line = $"{timestamp} [{threadId}] {level,-5} {_loggerName} - {text}";

            _channel.Writer.TryWrite(line);
        }

        private async Task ProcessLogQueueAsync(CancellationToken cancellationToken)
        {
            StreamWriter? writer = null;
            long currentLength = 0;

            try
            {
                OpenLogFile(ref writer, ref currentLength);

                ChannelReader<string> reader = _channel.Reader;
                while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (reader.TryRead(out string? line) && line != null)
                    {
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

                    if (writer != null)
                    {
                        await writer.FlushAsync().ConfigureAwait(false);
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
            _cts.Cancel();

            try
            {
                _writerTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException) { }
            catch (AggregateException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"AppLogger writer wait on dispose threw: {ex.Message}");
            }

            _cts.Dispose();
        }
    }
}
