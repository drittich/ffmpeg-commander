using System;
using System.Threading;
using System.Threading.Tasks;

namespace em
{
    public sealed class CommandSessionResult
    {
        public bool ShutdownRequested { get; init; }
        public bool StateChanged { get; init; }
        public string Message { get; init; } = string.Empty;

        /// <summary>
        /// Reference to the session state after applying the input line.
        /// </summary>
        public CommandState State { get; init; } = new CommandState();
    }

    /// <summary>
    /// Orchestrates input-line semantics (reserved commands + NL generate/adjust) over a mutable CommandState.
    /// Pre-TUI: designed for testable state transitions.
    /// </summary>
    public sealed class CommandSession
    {
        private readonly CommandGenerator _generator;
        private readonly CommandExecutor _executor;

        public CommandState State { get; }

        public CommandSession(CommandState? state = null)
        {
            _generator = new CommandGenerator();
            _executor = new CommandExecutor();
            State = state ?? new CommandState();
        }

        internal CommandSession(CommandGenerator generator, CommandExecutor executor, CommandState? state = null)
        {
            _generator = generator ?? throw new ArgumentNullException(nameof(generator));
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            State = state ?? new CommandState();
        }

        public async Task<CommandSessionResult> ApplyInputLine(string? line, CancellationToken ct = default)
        {
            string normalized = (line ?? string.Empty).Trim();

            if (normalized.Length == 0)
            {
                return new CommandSessionResult
                {
                    ShutdownRequested = false,
                    StateChanged = false,
                    Message = string.Empty,
                    State = State
                };
            }

            if (IsReserved(normalized, "exit"))
            {
                return new CommandSessionResult
                {
                    ShutdownRequested = true,
                    StateChanged = false,
                    Message = "Shutdown requested.",
                    State = State
                };
            }

            if (IsReserved(normalized, "help"))
            {
                return new CommandSessionResult
                {
                    ShutdownRequested = false,
                    StateChanged = false,
                    Message =
                        "Usage:\n" +
                        "  - Type a natural-language request to generate a command\n" +
                        "  - Type additional lines to adjust the current command\n" +
                        "Reserved commands:\n" +
                        "  run   Execute the current command\n" +
                        "  clear Clear stored command + output\n" +
                        "  exit  Quit\n" +
                        "  help  Show this help",
                    State = State
                };
            }

            if (IsReserved(normalized, "clear"))
            {
                bool hadAnything =
                    !string.IsNullOrWhiteSpace(State.CurrentCommand) ||
                    !string.IsNullOrWhiteSpace(State.BaseRequest) ||
                    (State.Adjustments.Count > 0) ||
                    !string.IsNullOrWhiteSpace(State.LastOutput) ||
                    !string.IsNullOrWhiteSpace(State.LastError);

                ClearState(State);

                return new CommandSessionResult
                {
                    ShutdownRequested = false,
                    StateChanged = hadAnything,
                    Message = "Cleared",
                    State = State
                };
            }

            if (IsReserved(normalized, "run"))
            {
                if (string.IsNullOrWhiteSpace(State.CurrentCommand))
                {
                    return new CommandSessionResult
                    {
                        ShutdownRequested = false,
                        StateChanged = false,
                        Message = "No command to run",
                        State = State
                    };
                }

                CommandExecutor.CommandExecutionResult result =
                    await _executor.ExecuteAsync(State.CurrentCommand, ct).ConfigureAwait(false);

                State.LastOutput = result.StdOut;
                State.LastError = result.StdErr;
                State.LastExitCode = result.ExitCode;

                string message = "Executed. Exit code: " + result.ExitCode + ".";

                if (!string.IsNullOrWhiteSpace(result.StdErr))
                    message += " (stderr captured)";

                return new CommandSessionResult
                {
                    ShutdownRequested = false,
                    StateChanged = true,
                    Message = message,
                    State = State
                };
            }

            // Natural-language line:
            // - If no current command: generate
            // - Else: treat as adjustment and update
            if (string.IsNullOrWhiteSpace(State.CurrentCommand))
            {
                (string baseRequest, string description) = ParsePotentialFfmpegPrefix(normalized);

                string args = await _generator.GenerateFromDescription(description, ct).ConfigureAwait(false);

                State.BaseRequest = baseRequest;
                State.Adjustments.Clear();
                State.CurrentCommand = EnsureFullCommand(args);
                State.LastOutput = null;
                State.LastError = null;
                State.LastExitCode = null;

                return new CommandSessionResult
                {
                    ShutdownRequested = false,
                    StateChanged = true,
                    Message = "Command stored",
                    State = State
                };
            }
            else
            {
                string instruction = normalized;

                // "ffmpeg-only" assumption:
                // - We store State.CurrentCommand as a full executable command line (leading "ffmpeg ").
                // - The generator operates on args only; it strips any accidental leading "ffmpeg" and returns args (no leading "ffmpeg").
                string updatedArgs =
                    await _generator.AdjustFromInstruction(State.CurrentCommand, instruction, ct).ConfigureAwait(false);

                State.Adjustments.Add(instruction);
                State.CurrentCommand = EnsureFullCommand(updatedArgs);
                State.LastOutput = null;
                State.LastError = null;
                State.LastExitCode = null;

                return new CommandSessionResult
                {
                    ShutdownRequested = false,
                    StateChanged = true,
                    Message = "Command updated",
                    State = State
                };
            }
        }

        private static bool IsReserved(string input, string reserved) =>
            input.Equals(reserved, StringComparison.OrdinalIgnoreCase);

        private static void ClearState(CommandState state)
        {
            state.CurrentCommand = null;
            state.BaseRequest = null;
            state.Adjustments.Clear();
            state.LastOutput = null;
            state.LastError = null;
            state.LastExitCode = null;
        }

        private static (string baseRequest, string description) ParsePotentialFfmpegPrefix(string normalizedLine)
        {
            // Keep the base request as a user-facing string.
            // If user typed "ffmpeg ...", store only the description portion as the request text.
            if (normalizedLine.StartsWith("ffmpeg ", StringComparison.OrdinalIgnoreCase))
            {
                string description = normalizedLine.Substring("ffmpeg ".Length).TrimStart();
                return (baseRequest: description, description: description);
            }

            return (baseRequest: normalizedLine, description: normalizedLine);
        }

        private static string EnsureFullCommand(string generatorResult)
        {
            string trimmed = (generatorResult ?? string.Empty).Trim();

            if (trimmed.StartsWith("ffmpeg ", StringComparison.OrdinalIgnoreCase))
                return trimmed;

            // Invariant ("ffmpeg-only" assumption): we store an executable full command line, always including leading "ffmpeg ".
            // (The generator returns args-only.)
            return "ffmpeg " + trimmed;
        }
    }
}