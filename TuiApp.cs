using System;
using System.Text;
using System.Threading.Tasks;
using Terminal.Gui;

namespace em;

public static class TuiApp
{
    public static void Run()
    {
        Application.Init();

        try
        {
            var session = new CommandSession();

            var top = Application.Top;

            // Reserve the bottom line for the StatusBar.
            var win = new Window("em — interactive command builder")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(1)
            };

            // --- Color schemes (approximate expectations from todo.md) ---
            var schemeCommandAvailable = new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.BrightGreen, Color.Black),
                Focus = Application.Driver.MakeAttribute(Color.BrightGreen, Color.Black),
                HotNormal = Application.Driver.MakeAttribute(Color.BrightGreen, Color.Black),
                HotFocus = Application.Driver.MakeAttribute(Color.BrightGreen, Color.Black),
                Disabled = Application.Driver.MakeAttribute(Color.Gray, Color.Black)
            };

            var schemeCommandNone = new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.Gray, Color.Black),
                Focus = Application.Driver.MakeAttribute(Color.Gray, Color.Black),
                HotNormal = Application.Driver.MakeAttribute(Color.Gray, Color.Black),
                HotFocus = Application.Driver.MakeAttribute(Color.Gray, Color.Black),
                Disabled = Application.Driver.MakeAttribute(Color.Gray, Color.Black)
            };

            var schemeBaseRequest = new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black),
                Focus = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black),
                HotNormal = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black),
                HotFocus = Application.Driver.MakeAttribute(Color.BrightCyan, Color.Black),
                Disabled = Application.Driver.MakeAttribute(Color.Gray, Color.Black)
            };

            var schemeDim = new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.Gray, Color.Black),
                Focus = Application.Driver.MakeAttribute(Color.Gray, Color.Black),
                HotNormal = Application.Driver.MakeAttribute(Color.Gray, Color.Black),
                HotFocus = Application.Driver.MakeAttribute(Color.Gray, Color.Black),
                Disabled = Application.Driver.MakeAttribute(Color.Gray, Color.Black)
            };

            var schemeError = new ColorScheme
            {
                Normal = Application.Driver.MakeAttribute(Color.BrightRed, Color.Black),
                Focus = Application.Driver.MakeAttribute(Color.BrightRed, Color.Black),
                HotNormal = Application.Driver.MakeAttribute(Color.BrightRed, Color.Black),
                HotFocus = Application.Driver.MakeAttribute(Color.BrightRed, Color.Black),
                Disabled = Application.Driver.MakeAttribute(Color.Gray, Color.Black)
            };

            // --- Panes ---
            var currentCommandFrame = new FrameView("Current command")
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = 5
            };

            var currentCommandLabel = new Label(string.Empty)
            {
                X = 1,
                Y = 0,
                Width = Dim.Fill(1),
                Height = Dim.Fill(),
                AutoSize = false
            };
            currentCommandFrame.Add(currentCommandLabel);

            var promptContextFrame = new FrameView("Prompt context")
            {
                X = 0,
                Y = Pos.Bottom(currentCommandFrame),
                Width = Dim.Fill(),
                Height = 7
            };

            var baseRequestLabel = new Label(string.Empty)
            {
                X = 1,
                Y = 0,
                Width = Dim.Fill(1),
                Height = 2,
                AutoSize = false,
                ColorScheme = schemeBaseRequest
            };

            var adjustmentsLabel = new Label(string.Empty)
            {
                X = 1,
                Y = Pos.Bottom(baseRequestLabel),
                Width = Dim.Fill(1),
                Height = Dim.Fill(),
                AutoSize = false,
                ColorScheme = schemeDim
            };

            promptContextFrame.Add(baseRequestLabel, adjustmentsLabel);

            var outputFrame = new FrameView("Output")
            {
                X = 0,
                Y = Pos.Bottom(promptContextFrame),
                Width = Dim.Fill(),
                Height = Dim.Fill(1) // leave last row for input
            };

            var outputTextView = new TextView
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                ReadOnly = true
            };
            outputFrame.Add(outputTextView);

            var inputField = new TextField(string.Empty)
            {
                X = 0,
                Y = Pos.AnchorEnd(0),
                Width = Dim.Fill(),
                Height = 1
            };

            win.Add(currentCommandFrame, promptContextFrame, outputFrame, inputField);

            // --- Footer / status ---
            bool isBusy = false;
            string busyText = string.Empty;

            // UI-local output log (session state remains source of truth for command/prompt/output capture).
            var outputLog = new StringBuilder();

            var busyItem = new StatusItem(Key.Null, "Idle", null);

            StatusBar? statusBar = new StatusBar(new[]
            {
                new StatusItem(Key.F5, "~F5~ run", () => _ = SubmitLineAsync("run")),
                new StatusItem(Key.F6, "~F6~ clear", () => _ = SubmitLineAsync("clear")),
                new StatusItem(Key.F1, "~F1~ help", () => _ = SubmitLineAsync("help")),
                new StatusItem(Key.F10, "~F10~ exit", () => _ = SubmitLineAsync("exit")),
                busyItem
            });

            // Instructions/hints should be visually "dim".
            statusBar!.ColorScheme = schemeDim;

            top.Add(win, statusBar!);

            void SetBusy(bool busy, string? message = null)
            {
                isBusy = busy;
                busyText = message ?? string.Empty;

                // Must update UI state on UI thread.
                Application.MainLoop.Invoke(() =>
                {
                    inputField.ReadOnly = isBusy;
                    busyItem.Title = isBusy
                        ? (string.IsNullOrWhiteSpace(busyText) ? "Busy…" : ("Busy: " + busyText))
                        : "Idle";

                    // Avoid referencing the StatusBar instance here to prevent definite-assignment issues
                    // (StatusBar is constructed with lambdas that call SubmitLineAsync -> SetBusy).
                    Application.Refresh();
                });
            }

            void AppendLog(string text, bool isError = false, bool isDim = false)
            {
                Application.MainLoop.Invoke(() =>
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        outputLog.AppendLine(text.TrimEnd());
                        outputLog.AppendLine();
                    }

                    // Best-effort color cue (TextView is single-scheme, so color the last write intent).
                    outputTextView.ColorScheme = isError ? schemeError : (isDim ? schemeDim : Colors.Base);

                    outputTextView.Text = outputLog.ToString();
                    outputTextView.MoveEnd();
                    outputTextView.SetNeedsDisplay();
                });
            }

            void RenderFromState(CommandState state)
            {
                Application.MainLoop.Invoke(() =>
                {
                    string cmd = string.IsNullOrWhiteSpace(state.CurrentCommand) ? "(none)" : state.CurrentCommand!;
                    currentCommandLabel.Text = cmd;

                    if (string.IsNullOrWhiteSpace(state.CurrentCommand))
                    {
                        currentCommandLabel.ColorScheme = schemeCommandNone;
                    }
                    else
                    {
                        currentCommandLabel.ColorScheme = schemeCommandAvailable;
                    }

                    bool hasBaseReq = !string.IsNullOrWhiteSpace(state.BaseRequest);
                    string baseReq = hasBaseReq ? state.BaseRequest! : "(none)";
                    baseRequestLabel.Text = baseReq;
                    baseRequestLabel.ColorScheme = hasBaseReq ? schemeBaseRequest : schemeDim;

                    if (state.Adjustments.Count == 0)
                    {
                        adjustmentsLabel.Text = "(no adjustments)";
                    }
                    else
                    {
                        var sb = new StringBuilder();
                        foreach (string a in state.Adjustments)
                        {
                            sb.Append("• ");
                            sb.AppendLine(a);
                        }

                        adjustmentsLabel.Text = sb.ToString().TrimEnd();
                    }

                    currentCommandFrame.SetNeedsDisplay();
                    promptContextFrame.SetNeedsDisplay();
                });
            }

            async Task SubmitLineAsync(string line)
            {
                // Called from UI thread, but do not block it.
                string normalized = (line ?? string.Empty).Trim();

                if (normalized.Length == 0)
                    return;

                // Minimal defined behavior: block quitting while busy (running/generating).
                // Applies to both typed `exit` and F10 status action.
                if (isBusy && normalized.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog("Busy: cannot exit while running. Please wait for completion.", isError: true);
                    return;
                }

                SetBusy(true, normalized.Equals("run", StringComparison.OrdinalIgnoreCase) ? "running" : "working");

                AppendLog($"> {normalized}");

                try
                {
                    CommandSessionResult result =
                        await session.ApplyInputLine(normalized).ConfigureAwait(false);

                    Application.MainLoop.Invoke(() =>
                    {
                        RenderFromState(result.State);

                        // Respect "clear stored command + output" semantics.
                        // NOTE: Clear the *UI* output view/log unconditionally on `clear`,
                        // even if the session state did not change (e.g., clearing after `help` output).
                        if (normalized.Equals("clear", StringComparison.OrdinalIgnoreCase))
                        {
                            outputLog.Clear();
                            outputTextView.Text = string.Empty;
                            outputTextView.SetNeedsDisplay();
                        }

                        if (!string.IsNullOrWhiteSpace(result.Message))
                        {
                            bool exitCodeNonZero =
                                normalized.Equals("run", StringComparison.OrdinalIgnoreCase) &&
                                result.State.LastExitCode.HasValue &&
                                result.State.LastExitCode.Value != 0;

                            bool isHelp =
                                normalized.Equals("help", StringComparison.OrdinalIgnoreCase) ||
                                result.Message.StartsWith("Usage:", StringComparison.OrdinalIgnoreCase);

                            // Treat messages as errors if stderr was present, message looks like an error, or exit code was non-zero.
                            bool isError =
                                exitCodeNonZero ||
                                (!string.IsNullOrWhiteSpace(result.State.LastError)) ||
                                result.Message.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
                                result.Message.StartsWith("No command to run", StringComparison.OrdinalIgnoreCase);

                            AppendLog(result.Message, isError: isError, isDim: isHelp && !isError);
                        }

                        if (normalized.Equals("run", StringComparison.OrdinalIgnoreCase) && result.State.LastExitCode.HasValue)
                        {
                            int code = result.State.LastExitCode.Value;
                            AppendLog("Exit code: " + code, isError: code != 0);
                        }

                        if (!string.IsNullOrWhiteSpace(result.State.LastOutput))
                        {
                            AppendLog("stdout:\n" + result.State.LastOutput);
                        }

                        if (!string.IsNullOrWhiteSpace(result.State.LastError))
                        {
                            AppendLog("stderr:\n" + result.State.LastError, isError: true);
                        }

                        if (result.ShutdownRequested)
                        {
                            Application.RequestStop();
                        }
                    });
                }
                catch (Exception ex)
                {
                    AppendLog("Error: " + ex.Message, isError: true);
                }
                finally
                {
                    SetBusy(false);
                }
            }

            // Enter-to-submit from the bottom input field.
            inputField.KeyPress += (args) =>
            {
                if (args.KeyEvent.Key != Key.Enter)
                    return;

                string line = inputField.Text?.ToString() ?? string.Empty;
                inputField.Text = string.Empty;

                args.Handled = true;

                // Terminal.Gui requires an event-handler signature here; keep it fire-and-forget.
                _ = SubmitLineAsync(line);
            };

            // Initial render.
            RenderFromState(session.State);

            Application.Run();
        }
        finally
        {
            Application.Shutdown();
        }
    }
}