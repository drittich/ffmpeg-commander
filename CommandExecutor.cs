using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace em
{
    internal sealed class CommandExecutor
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

        public async Task<CommandExecutionResult> ExecuteAsync(string fullCommand, CancellationToken ct = default)
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

            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Failed to start process.");

                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();

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

                // Ensure streams are fully drained.
                string stdout = await stdoutTask.ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);

                return new CommandExecutionResult
                {
                    ExitCode = process.ExitCode,
                    StdOut = stdout ?? string.Empty,
                    StdErr = stderr ?? string.Empty
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }
}