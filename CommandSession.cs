using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FfmpegCommander
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

        // Immutable snapshot of the mutable parts of CommandState, used for Undo.
        private sealed record StateSnapshot(
            string? CurrentCommand,
            string? BaseRequest,
            IReadOnlyList<string> Adjustments,
            string? LastOutput,
            string? LastError,
            int? LastExitCode);

        private readonly Stack<StateSnapshot> _undo = new();

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
                        "Commands (press the function key or click it in the status bar):\n" +
                        "  F5  run    Execute the current command\n" +
                        "  F6  clear  Clear stored command + output\n" +
                        "  F1  help   Show this help\n" +
                        "  F10 exit   Quit",
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

                // Snapshot BEFORE clearing so the clear is undoable.
                PushUndoSnapshot();

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
                // Run is NOT state-mutating in the undoable sense (only updates Last* output fields),
                // so we do not snapshot here. Route through RunAsync with no streaming callbacks.
                return await RunAsync(onStdoutLine: null, onStderrLine: null, ct).ConfigureAwait(false);
            }

            // Not a reserved command — treat as free text (generate or adjust).
            return await ApplyTextLine(normalized, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Applies a free-text line: generates a new command when none is stored, otherwise treats the
        /// line as an adjustment to the current command. Reserved words are NOT interpreted here — typed
        /// input is always natural language. Command actions (run/clear/exit/help) are invoked separately
        /// (via <see cref="ApplyInputLine"/> from the status-bar function keys).
        /// </summary>
        public async Task<CommandSessionResult> ApplyTextLine(string? line, CancellationToken ct = default)
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

            // - If no current command: generate
            // - Else: treat as adjustment and update
            if (string.IsNullOrWhiteSpace(State.CurrentCommand))
            {
                (string baseRequest, string description) = ParsePotentialFfmpegPrefix(normalized);

                // Snapshot BEFORE the generate mutation so it can be undone.
                PushUndoSnapshot();

                CommandGenerator.GeneratorCallResult gen =
                    await _generator.GenerateFromDescription(description, ct).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(gen.Error))
                {
                    State.LastOutput = null;
                    State.LastError = gen.Error;
                    State.LastExitCode = null;

                    return new CommandSessionResult
                    {
                        ShutdownRequested = false,
                        StateChanged = true,
                        Message = "Error: " + gen.Error,
                        State = State
                    };
                }

                State.BaseRequest = baseRequest;
                State.Adjustments.Clear();
                State.CurrentCommand = EnsureFullCommand(gen.Value);
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

                // Snapshot BEFORE the adjust mutation so it can be undone.
                PushUndoSnapshot();

                // "ffmpeg-only" assumption:
                // - We store State.CurrentCommand as a full executable command line (leading "ffmpeg ").
                // - The generator operates on args only; it strips any accidental leading "ffmpeg" and returns args (no leading "ffmpeg").
                CommandGenerator.GeneratorCallResult adjust =
                    await _generator.AdjustFromInstruction(State.CurrentCommand, instruction, ct).ConfigureAwait(false);

                State.Adjustments.Add(instruction);
                State.CurrentCommand = EnsureFullCommand(adjust.Value);
                State.LastOutput = null;
                State.LastExitCode = null;

                // If adjust failed, keep previous command (AdjustFromInstruction already returns previous args on failure)
                // but surface the error so UI can show it.
                State.LastError = string.IsNullOrWhiteSpace(adjust.Error) ? null : adjust.Error;

                return new CommandSessionResult
                {
                    ShutdownRequested = false,
                    StateChanged = true,
                    Message = string.IsNullOrWhiteSpace(adjust.Error) ? "Command updated" : ("Error: " + adjust.Error),
                    State = State
                };
            }
        }

        /// <summary>
        /// Runs the current command, streaming stdout/stderr lines to the supplied callbacks as they arrive.
        /// Stores LastOutput/LastError/LastExitCode and returns the same "Executed. Exit code: N." message
        /// (with " (stderr captured)" appended when stderr is non-empty) as the reserved "run" path.
        /// The callbacks may be invoked on background threads (see <see cref="CommandExecutor.ExecuteStreamingAsync"/>).
        /// </summary>
        public async Task<CommandSessionResult> RunAsync(
            Action<string>? onStdoutLine,
            Action<string>? onStderrLine,
            CancellationToken ct = default)
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
                await _executor.ExecuteStreamingAsync(State.CurrentCommand, onStdoutLine, onStderrLine, ct).ConfigureAwait(false);

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

        /// <summary>
        /// Applies a user-edited command directly. Normalizes a leading "ffmpeg " via the same helper used
        /// for generator output, sets State.CurrentCommand, and clears the last run output. Undoable.
        /// </summary>
        public CommandSessionResult SetCommand(string fullCommandOrArgs)
        {
            // Snapshot BEFORE mutating so the edit can be undone.
            PushUndoSnapshot();

            State.CurrentCommand = EnsureFullCommand(fullCommandOrArgs ?? string.Empty);
            State.LastOutput = null;
            State.LastError = null;
            State.LastExitCode = null;

            return new CommandSessionResult
            {
                ShutdownRequested = false,
                StateChanged = true,
                Message = "Command edited",
                State = State
            };
        }

        /// <summary>True when there is at least one snapshot available to undo.</summary>
        public bool CanUndo => _undo.Count > 0;

        /// <summary>
        /// Restores the most recent snapshot into the existing State object (mutated in place so callers
        /// holding a reference to session.State observe the change).
        /// </summary>
        public CommandSessionResult Undo()
        {
            if (_undo.Count == 0)
            {
                return new CommandSessionResult
                {
                    ShutdownRequested = false,
                    StateChanged = false,
                    Message = "Nothing to undo",
                    State = State
                };
            }

            StateSnapshot snapshot = _undo.Pop();
            RestoreSnapshot(snapshot);

            return new CommandSessionResult
            {
                ShutdownRequested = false,
                StateChanged = true,
                Message = "Undid last change",
                State = State
            };
        }

        private void PushUndoSnapshot()
        {
            _undo.Push(new StateSnapshot(
                CurrentCommand: State.CurrentCommand,
                BaseRequest: State.BaseRequest,
                Adjustments: new List<string>(State.Adjustments),
                LastOutput: State.LastOutput,
                LastError: State.LastError,
                LastExitCode: State.LastExitCode));
        }

        private void RestoreSnapshot(StateSnapshot snapshot)
        {
            State.CurrentCommand = snapshot.CurrentCommand;
            State.BaseRequest = snapshot.BaseRequest;
            State.Adjustments.Clear();
            State.Adjustments.AddRange(snapshot.Adjustments);
            State.LastOutput = snapshot.LastOutput;
            State.LastError = snapshot.LastError;
            State.LastExitCode = snapshot.LastExitCode;
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