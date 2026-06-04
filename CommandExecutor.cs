using System.Diagnostics;
using System.Text;

namespace FfmpegCommander
{
    internal class CommandExecutor
    {
        internal sealed class CommandExecutionResult
        {
            public int ExitCode { get; init; }
            public string StdOut { get; init; } = string.Empty;
            public string StdErr { get; init; } = string.Empty;

            // Optional convenience; keeps stdout/stderr separately as source of truth.
            public string Combined =>
                string.IsNullOrEmpty(StdErr) ? StdOut :
                string.IsNullOrEmpty(StdOut) ? StdErr :
                StdOut + Environment.NewLine + StdErr;
        }

        public virtual Task<CommandExecutionResult> ExecuteAsync(string fullCommand, CancellationToken ct = default)
        {
            // Delegate to the streaming implementation with no per-line callbacks.
            // (Test fakes override this method directly, so this delegation does not affect them.)
            return ExecuteStreamingAsync(fullCommand, onStdoutLine: null, onStderrLine: null, ct);
        }

        /// <summary>
        /// Runs the command and invokes <paramref name="onStdoutLine"/>/<paramref name="onStderrLine"/>
        /// line-by-line as output arrives, while still aggregating the full stdout/stderr into the returned
        /// result. Preserves cancellation behavior (kills the process tree on cancel).
        /// </summary>
        /// <remarks>
        /// IMPORTANT: the callbacks may be invoked on background (thread-pool) threads raised by
        /// Process.OutputDataReceived/ErrorDataReceived. The UI layer is responsible for marshalling
        /// to its own UI thread.
        /// </remarks>
        public virtual async Task<CommandExecutionResult> ExecuteStreamingAsync(
            string fullCommand,
            Action<string>? onStdoutLine,
            Action<string>? onStderrLine,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(fullCommand))
                throw new ArgumentException("Command must not be null/empty.", nameof(fullCommand));

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/C " + fullCommand,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            // OutputDataReceived/ErrorDataReceived deliver one line at a time (without the newline),
            // and a final event with Data == null when the stream closes. We aggregate AND stream.
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null)
                    return;

                lock (stdoutBuilder)
                    stdoutBuilder.AppendLine(e.Data);

                onStdoutLine?.Invoke(e.Data);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                    return;

                lock (stderrBuilder)
                    stderrBuilder.AppendLine(e.Data);

                onStderrLine?.Invoke(e.Data);
            };

            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Failed to start process.");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                using var _ = ct.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Best-effort cancellation; ignore race/permission errors.
                    }
                });

                await process.WaitForExitAsync(ct).ConfigureAwait(false);

                // WaitForExitAsync returns once the process exits, but the async read events may still
                // be in flight. A parameterless WaitForExit() flushes remaining buffered output and
                // ensures all *DataReceived handlers have run.
                process.WaitForExit();

                string stdout;
                string stderr;
                lock (stdoutBuilder)
                    stdout = stdoutBuilder.ToString();
                lock (stderrBuilder)
                    stderr = stderrBuilder.ToString();

                return new CommandExecutionResult
                {
                    ExitCode = process.ExitCode,
                    StdOut = stdout,
                    StdErr = stderr
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }
}