using System;
using System.Text;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Terminal.Gui v2 moved its types out of the root namespace and renamed several APIs:
// ColorScheme -> Scheme, view.ColorScheme = x -> view.SetScheme(x), Application.Refresh ->
// Application.LayoutAndDraw, StatusItem -> Shortcut, KeyPress -> KeyDown. Alias Attribute to
// disambiguate it from System.Attribute.
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace em;

public static class TuiApp
{
    public static void Run()
    {
        // v2 replaces the static Application object with a disposable IApplication instance.
        // Disposing it performs the shutdown that Application.Shutdown() did in v1.
        IApplication app = Application.Create();
        app.Init();

        try
        {
            var session = new CommandSession();

            // Reserve the bottom line for the StatusBar.
            var win = new Window
            {
                Title = "em — interactive command builder",
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };

            // --- Color schemes (approximate expectations from todo.md) ---
            var schemeCommandAvailable = new Scheme
            {
                Normal = new Attribute(Color.BrightGreen, Color.Black),
                Focus = new Attribute(Color.BrightGreen, Color.Black),
                HotNormal = new Attribute(Color.BrightGreen, Color.Black),
                HotFocus = new Attribute(Color.BrightGreen, Color.Black),
                Disabled = new Attribute(Color.Gray, Color.Black)
            };

            var schemeCommandNone = new Scheme
            {
                Normal = new Attribute(Color.Gray, Color.Black),
                Focus = new Attribute(Color.Gray, Color.Black),
                HotNormal = new Attribute(Color.Gray, Color.Black),
                HotFocus = new Attribute(Color.Gray, Color.Black),
                Disabled = new Attribute(Color.Gray, Color.Black)
            };

            var schemeBaseRequest = new Scheme
            {
                Normal = new Attribute(Color.BrightCyan, Color.Black),
                Focus = new Attribute(Color.BrightCyan, Color.Black),
                HotNormal = new Attribute(Color.BrightCyan, Color.Black),
                HotFocus = new Attribute(Color.BrightCyan, Color.Black),
                Disabled = new Attribute(Color.Gray, Color.Black)
            };

            var schemeDim = new Scheme
            {
                Normal = new Attribute(Color.Gray, Color.Black),
                Focus = new Attribute(Color.Gray, Color.Black),
                HotNormal = new Attribute(Color.Gray, Color.Black),
                HotFocus = new Attribute(Color.Gray, Color.Black),
                Disabled = new Attribute(Color.Gray, Color.Black)
            };

            var schemeError = new Scheme
            {
                Normal = new Attribute(Color.BrightRed, Color.Black),
                Focus = new Attribute(Color.BrightRed, Color.Black),
                HotNormal = new Attribute(Color.BrightRed, Color.Black),
                HotFocus = new Attribute(Color.BrightRed, Color.Black),
                Disabled = new Attribute(Color.Gray, Color.Black)
            };

            Scheme schemeDefault = SchemeManager.GetScheme("Base");

            // --- Panes ---
            var currentCommandFrame = new FrameView
            {
                Title = "Current command",
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = 5
            };

            var currentCommandLabel = new Label
            {
                Text = string.Empty,
                X = 1,
                Y = 0,
                Width = Dim.Fill(1),
                Height = Dim.Fill()
            };
            currentCommandFrame.Add(currentCommandLabel);

            var promptContextFrame = new FrameView
            {
                Title = "Prompt context",
                X = 0,
                Y = Pos.Bottom(currentCommandFrame),
                Width = Dim.Fill(),
                Height = 7
            };

            var baseRequestLabel = new Label
            {
                Text = string.Empty,
                X = 1,
                Y = 0,
                Width = Dim.Fill(1),
                Height = 2
            };
            baseRequestLabel.SetScheme(schemeBaseRequest);

            var adjustmentsLabel = new Label
            {
                Text = string.Empty,
                X = 1,
                Y = Pos.Bottom(baseRequestLabel),
                Width = Dim.Fill(1),
                Height = Dim.Fill()
            };
            adjustmentsLabel.SetScheme(schemeDim);

            promptContextFrame.Add(baseRequestLabel, adjustmentsLabel);

            // Bottom input lives in its own frame so it's always visible and not overlapped by the output view.
            // Height includes the frame border + one-line TextField. The extra row below is reserved for the
            // StatusBar, which now lives inside the top-level window (v2 removed the separate Toplevel host).
            const int inputFrameHeight = 3;

            var outputFrame = new FrameView
            {
                Title = "Output",
                X = 0,
                Y = Pos.Bottom(promptContextFrame),
                Width = Dim.Fill(),
                Height = Dim.Fill(inputFrameHeight + 1) // leave room for the input frame + status bar below
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

            var inputFrame = new FrameView
            {
                Title = "Input",
                X = 0,
                Y = Pos.AnchorEnd(inputFrameHeight + 1),
                Width = Dim.Fill(),
                Height = inputFrameHeight
            };

            var inputField = new TextField
            {
                Text = string.Empty,
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = 1
            };
            inputFrame.Add(inputField);

            win.Add(currentCommandFrame, promptContextFrame, outputFrame, inputFrame);

            // --- Footer / status ---
            bool isBusy = false;
            string busyText = string.Empty;

            // UI-local output log (session state remains source of truth for command/prompt/output capture).
            var outputLog = new StringBuilder();

            var busyItem = new Shortcut { Title = "Idle", Key = Key.Empty };

            var statusBar = new StatusBar(new[]
            {
                new Shortcut(Key.F5, "run", () => _ = SubmitLineAsync("run"), null),
                new Shortcut(Key.F6, "clear", () => _ = SubmitLineAsync("clear"), null),
                new Shortcut(Key.F1, "help", () => _ = SubmitLineAsync("help"), null),
                new Shortcut(Key.F10, "exit", () => _ = SubmitLineAsync("exit"), null),
                busyItem
            });

            // Instructions/hints should be visually "dim".
            statusBar.SetScheme(schemeDim);

            win.Add(statusBar);

            void SetBusy(bool busy, string? message = null)
            {
                isBusy = busy;
                busyText = message ?? string.Empty;

                // Must update UI state on UI thread.
                app.Invoke(() =>
                {
                    inputField.ReadOnly = isBusy;
                    busyItem.Title = isBusy
                        ? (string.IsNullOrWhiteSpace(busyText) ? "Busy…" : ("Busy: " + busyText))
                        : "Idle";

                    // When leaving busy state, restore focus so typing always goes into the input field.
                    if (!isBusy)
                    {
                        inputField.SetFocus();
                    }

                    app.LayoutAndDraw(false);
                });
            }

            void AppendLog(string text, bool isError = false, bool isDim = false)
            {
                app.Invoke(() =>
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        outputLog.AppendLine(text.TrimEnd());
                        outputLog.AppendLine();
                    }

                    // Best-effort color cue (TextView is single-scheme, so color the last write intent).
                    outputTextView.SetScheme(isError ? schemeError : (isDim ? schemeDim : schemeDefault));

                    outputTextView.Text = outputLog.ToString();
                    outputTextView.MoveEnd();
                    outputTextView.SetNeedsDraw();
                });
            }

            void RenderFromState(CommandState state)
            {
                app.Invoke(() =>
                {
                    string cmd = string.IsNullOrWhiteSpace(state.CurrentCommand) ? "(none)" : state.CurrentCommand!;
                    currentCommandLabel.Text = cmd;

                    if (string.IsNullOrWhiteSpace(state.CurrentCommand))
                    {
                        currentCommandLabel.SetScheme(schemeCommandNone);
                    }
                    else
                    {
                        currentCommandLabel.SetScheme(schemeCommandAvailable);
                    }

                    bool hasBaseReq = !string.IsNullOrWhiteSpace(state.BaseRequest);
                    string baseReq = hasBaseReq ? state.BaseRequest! : "(none)";
                    baseRequestLabel.Text = baseReq;
                    baseRequestLabel.SetScheme(hasBaseReq ? schemeBaseRequest : schemeDim);

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

                    currentCommandFrame.SetNeedsDraw();
                    promptContextFrame.SetNeedsDraw();
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

                    app.Invoke(() =>
                    {
                        RenderFromState(result.State);

                        // Respect "clear stored command + output" semantics.
                        // NOTE: Clear the *UI* output view/log unconditionally on `clear`,
                        // even if the session state did not change (e.g., clearing after `help` output).
                        if (normalized.Equals("clear", StringComparison.OrdinalIgnoreCase))
                        {
                            outputLog.Clear();
                            outputTextView.Text = string.Empty;
                            outputTextView.SetNeedsDraw();
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
                            app.RequestStop();
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
            inputField.KeyDown += (sender, key) =>
            {
                if (key != Key.Enter)
                    return;

                string line = inputField.Text ?? string.Empty;
                inputField.Text = string.Empty;

                key.Handled = true;

                // Keep focus in the input field (especially after Enter).
                inputField.SetFocus();

                // Terminal.Gui requires an event-handler signature here; keep it fire-and-forget.
                _ = SubmitLineAsync(line);
            };

            // Initial render.
            RenderFromState(session.State);

            // Ensure the input is usable immediately on startup.
            inputField.SetFocus();

            app.Run(win, null);
            win.Dispose();
        }
        finally
        {
            app.Dispose();
        }
    }
}
