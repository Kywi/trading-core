using Serilog;
using Serilog.Events;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Log
{
    public static class Logger
    {
        private static string? _logsDirectory;     // folder only, no file

        public static void InitializeLogger(string? dataFolder = null)
        {
            var baseFolder = string.IsNullOrWhiteSpace(dataFolder)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : dataFolder;
            _logsDirectory = Path.Combine(baseFolder, "Logs");
            Directory.CreateDirectory(_logsDirectory);
            Directory.CreateDirectory(Path.Combine(_logsDirectory, "Errors"));

            SwapLogger(LogEventLevel.Debug, applyMicrosoftOverride: true);
            RegisterShutdownHook();
        }

        /// <summary>
        /// Replaces the global Serilog logger atomically and disposes the prior
        /// instance so its file handles and buffered writes are released.
        /// Concurrent writers either complete on the old logger or proceed on
        /// the new one - there is no silent-logger window between the two.
        /// </summary>
        private static void SwapLogger(LogEventLevel minimumLevel, bool applyMicrosoftOverride)
        {
            var newLogger = BuildLogger(minimumLevel, applyMicrosoftOverride);
            var previous = Serilog.Log.Logger;
            Serilog.Log.Logger = newLogger;
            (previous as IDisposable)?.Dispose();
        }

        private static Serilog.Core.Logger BuildLogger(LogEventLevel minimumLevel, bool applyMicrosoftOverride)
        {
            // Serilog rolls to "log-2025-07-07.txt", "log-2025-07-08.txt", ...
            const string template = "log-.txt";
            const string errorTemplate = "error-.txt";

            var errorsDirectory = Path.Combine(_logsDirectory!, "Errors");

            var config = new LoggerConfiguration().MinimumLevel.Is(minimumLevel);
#if DEBUG
            if (applyMicrosoftOverride)
                config = config.MinimumLevel.Override("Microsoft", LogEventLevel.Information);
#endif

            return config
                .WriteTo.File(
                    Path.Combine(_logsDirectory!, template),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 10,
                    fileSizeLimitBytes: 100 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: true)
                .WriteTo.File(
                    Path.Combine(errorsDirectory, errorTemplate),
                    restrictedToMinimumLevel: LogEventLevel.Warning,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    fileSizeLimitBytes: 100 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: true)
                .CreateLogger();
        }

        /// <summary>
        /// Flushes and closes the underlying Serilog logger. Call on graceful
        /// shutdown to guarantee buffered log entries reach disk.
        /// </summary>
        public static void Shutdown()
        {
            Serilog.Log.CloseAndFlush();
        }

        private static int _shutdownHookRegistered;

        private static void RegisterShutdownHook()
        {
            if (System.Threading.Interlocked.Exchange(ref _shutdownHookRegistered, 1) == 1)
                return;

            AppDomain.CurrentDomain.ProcessExit += (_, _) => Serilog.Log.CloseAndFlush();
            AppDomain.CurrentDomain.UnhandledException += (_, _) => Serilog.Log.CloseAndFlush();
        }

        /// <summary>
        /// Raises the minimum log level to <see cref="LogEventLevel.Warning"/>
        /// so per-tick Information/Debug messages are discarded during
        /// high-throughput backtest replay.
        /// </summary>
        public static void SuppressVerboseLogging()
        {
            if (string.IsNullOrEmpty(_logsDirectory))
                return;

            SwapLogger(LogEventLevel.Warning, applyMicrosoftOverride: false);
        }

        /// <summary>
        /// Restores the default <see cref="LogEventLevel.Debug"/> minimum level
        /// after backtest replay completes.
        /// </summary>
        public static void RestoreVerboseLogging()
        {
            if (string.IsNullOrEmpty(_logsDirectory))
                return;

            SwapLogger(LogEventLevel.Debug, applyMicrosoftOverride: true);
        }

        public static void LogInformation(string message) => Serilog.Log.Information(message);
        public static void LogInformation(string messageTemplate, params object?[] propertyValues)
            => Serilog.Log.Information(messageTemplate, propertyValues);

        public static void LogWarning(string message) => Serilog.Log.Warning(message);
        public static void LogWarning(string messageTemplate, params object?[] propertyValues)
            => Serilog.Log.Warning(messageTemplate, propertyValues);
        public static void LogWarning(Exception ex, string messageTemplate, params object?[] propertyValues)
            => Serilog.Log.Warning(ex, messageTemplate, propertyValues);

        public static void LogError(string message) => Serilog.Log.Error(message);
        public static void LogError(Exception ex, string message) => Serilog.Log.Error(ex, message);
        public static void LogError(string messageTemplate, params object?[] propertyValues)
            => Serilog.Log.Error(messageTemplate, propertyValues);
        public static void LogError(Exception ex, string messageTemplate, params object?[] propertyValues)
            => Serilog.Log.Error(ex, messageTemplate, propertyValues);

        /// <summary>Opens the most recent log file in the user's default text editor.</summary>
        public static Task OpenCurrentLogFileAsync()
        {
            try
            {
                var filePath = ResolveLatestLogPath();
                if (filePath == null)
                {
                    Serilog.Log.Warning("No log file found to open.");
                    return Task.CompletedTask;
                }

                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to open log file.");
            }

            return Task.CompletedTask;
        }

        /// <summary>Opens the Logs folder in the user's default file-explorer.</summary>
        public static Task OpenLogsFolderAsync()
        {
            if (string.IsNullOrEmpty(_logsDirectory))
            {
                Serilog.Log.Warning("Logs folder requested before InitializeLogger was called.");
                return Task.CompletedTask;
            }

            try
            {
                Process.Start(new ProcessStartInfo(_logsDirectory) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to open Logs folder.");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Returns the newest log file in the Logs directory by last-write time,
        /// or null if no log files are present. Enumerates on every call so it
        /// stays correct across Serilog's daily rollover.
        /// </summary>
        private static string? ResolveLatestLogPath()
        {
            if (string.IsNullOrEmpty(_logsDirectory) || !Directory.Exists(_logsDirectory))
                return null;

            return new DirectoryInfo(_logsDirectory)
                .EnumerateFiles("log-*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
    }
}