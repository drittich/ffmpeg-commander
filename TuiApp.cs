using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
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

            // C4: detect the unconfigured state up front so we can warn the user before they
            // try (and fail) to generate. CommandGenerator reads appsettings.json itself.
            bool aiConfigured = new CommandGenerator().IsConfigured;

            // H5: name the tool and make its ffmpeg-only scope obvious in the title.
            var win = new Window
            {
                Title = "em — describe an ffmpeg command in plain English",
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill()
            };

            // --- Color palette (L1) ---------------------------------------------------------
            // Anti-AI-slop scheme. Goals: no pure-black backgrounds, no cyan/neon-green cliché.
            // Dominant neutral = a dark slate tinted slightly toward blue (#1c2127 / #232a33).
            // Single restrained accent = warm amber (#e8b765) — used for the live, runnable command.
            // Secondary tone = soft teal-blue (#7fb0c8) for the user's base request.
            // Muted dim = desaturated slate-grey (#8a93a0) for hints/adjustments/log chrome.
            // Error = a clear, slightly-muted brick red (#d96459) — readable, not neon.
            var bg = new Color(0x1c, 0x21, 0x27);        // primary tinted-neutral background (NOT black)
            var bgInset = new Color(0x23, 0x2a, 0x33);    // slightly lighter inset for editable/active fields
            var fgNeutral = new Color(0xcf, 0xd6, 0xdf);  // default text: soft off-white
            var fgAccent = new Color(0xe8, 0xb7, 0x65);   // amber accent: the available/runnable command
            var fgSecondary = new Color(0x7f, 0xb0, 0xc8);// teal-blue: the base request
            var fgDim = new Color(0x8a, 0x93, 0xa0);      // muted slate: hints, adjustments, chrome
            var fgError = new Color(0xd9, 0x64, 0x59);    // clear non-neon red: errors

            var schemeDefault = new Scheme
            {
                Normal = new Attribute(fgNeutral, bg),
                Focus = new Attribute(fgNeutral, bgInset),
                HotNormal = new Attribute(fgAccent, bg),
                HotFocus = new Attribute(fgAccent, bgInset),
                Disabled = new Attribute(fgDim, bg)
            };

            // The available command (accent). When there is no command we show the prefix dim instead.
            var schemeCommandAvailable = new Scheme
            {
                Normal = new Attribute(fgAccent, bg),
                Focus = new Attribute(fgAccent, bgInset),
                HotNormal = new Attribute(fgAccent, bg),
                HotFocus = new Attribute(fgAccent, bgInset),
                Disabled = new Attribute(fgDim, bg)
            };

            var schemeCommandNone = new Scheme
            {
                Normal = new Attribute(fgDim, bg),
                Focus = new Attribute(fgDim, bgInset),
                HotNormal = new Attribute(fgDim, bg),
                HotFocus = new Attribute(fgDim, bgInset),
                Disabled = new Attribute(fgDim, bg)
            };

            // Editable args field: neutral text on the lighter inset so it reads as an input.
            var schemeArgsField = new Scheme
            {
                Normal = new Attribute(fgNeutral, bgInset),
                Focus = new Attribute(fgAccent, bgInset),
                HotNormal = new Attribute(fgNeutral, bgInset),
                HotFocus = new Attribute(fgAccent, bgInset),
                Disabled = new Attribute(fgDim, bg)
            };

            var schemeBaseRequest = new Scheme
            {
                Normal = new Attribute(fgSecondary, bg),
                Focus = new Attribute(fgSecondary, bgInset),
                HotNormal = new Attribute(fgSecondary, bg),
                HotFocus = new Attribute(fgSecondary, bgInset),
                Disabled = new Attribute(fgDim, bg)
            };

            var schemeDim = new Scheme
            {
                Normal = new Attribute(fgDim, bg),
                Focus = new Attribute(fgDim, bgInset),
                HotNormal = new Attribute(fgDim, bg),
                HotFocus = new Attribute(fgDim, bgInset),
                Disabled = new Attribute(fgDim, bg)
            };

            var schemeError = new Scheme
            {
                Normal = new Attribute(fgError, bg),
                Focus = new Attribute(fgError, bgInset),
                HotNormal = new Attribute(fgError, bg),
                HotFocus = new Attribute(fgError, bgInset),
                Disabled = new Attribute(fgDim, bg)
            };

            win.SetScheme(schemeDefault);

            // --- Panes ----------------------------------------------------------------------
            // M2: use Dim-based heights rather than magic constants. The command pane and the
            // prompt-context pane are auto-sized to their content; the input frame is pinned at
            // the bottom; the output pane fills everything that remains.

            // Command pane: a static, non-editable "ffmpeg" affix (H5) followed by an editable
            // args TextField (C2). The args field holds CurrentCommand with the leading
            // "ffmpeg " stripped; committing an edit calls session.SetCommand(...).
            var currentCommandFrame = new FrameView
            {
                Title = "Current command  (Tab to edit · Enter to commit)",
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = 3 // border (top+bottom) + 1 content row
            };

            var ffmpegPrefixLabel = new Label
            {
                Text = "ffmpeg ",
                X = 0,
                Y = 0,
                Width = Dim.Auto(),
                Height = 1
            };

            var commandArgsField = new VisibleCursorTextField
            {
                Text = string.Empty,
                X = Pos.Right(ffmpegPrefixLabel),
                Y = 0,
                Width = Dim.Fill(),
                Height = 1
            };
            commandArgsField.SetScheme(schemeArgsField);

            currentCommandFrame.Add(ffmpegPrefixLabel, commandArgsField);

            // Prompt-context pane: base request (secondary tone) + a SCROLLABLE adjustments view (M2).
            var promptContextFrame = new FrameView
            {
                Title = "Prompt context",
                X = 0,
                Y = Pos.Bottom(currentCommandFrame),
                Width = Dim.Fill(),
                Height = Dim.Percent(28)
            };

            var baseRequestLabel = new Label
            {
                Text = string.Empty,
                X = 1,
                Y = 0,
                Width = Dim.Fill(1),
                Height = 1
            };
            baseRequestLabel.SetScheme(schemeBaseRequest);

            // M2: read-only, scrollable TextView so a long refinement history isn't clipped.
            var adjustmentsView = new TextView
            {
                X = 1,
                Y = Pos.Bottom(baseRequestLabel),
                Width = Dim.Fill(1),
                Height = Dim.Fill(),
                ReadOnly = true
            };
            adjustmentsView.SetScheme(schemeDim);

            promptContextFrame.Add(baseRequestLabel, adjustmentsView);

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
                Title = "Request  (type plain English · Enter to send)",
                X = 0,
                Y = Pos.AnchorEnd(inputFrameHeight + 1),
                Width = Dim.Fill(),
                Height = inputFrameHeight
            };

            var inputField = new VisibleCursorTextField
            {
                Text = string.Empty,
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = 1
            };
            inputFrame.Add(inputField);

            win.Add(currentCommandFrame, promptContextFrame, outputFrame, inputFrame);

            // --- Footer / status ------------------------------------------------------------
            bool isBusy = false;
            string busyText = string.Empty;

            // H1: holds the token source for the active run so Esc can cancel it. Non-null only while running.
            CancellationTokenSource? runCts = null;

            // UI-local output log (session state remains source of truth for command/prompt/output capture).
            var outputLog = new StringBuilder();

            var busyItem = new Shortcut { Title = "Idle", Key = Key.Empty };

            // Key/shortcut map:
            //   F1 help · F2 edit (focus args) · F5 run · F6 clear · F7 undo · F10 exit · Esc cancel (while running)
            var statusBar = new StatusBar(new[]
            {
                new Shortcut(Key.F1, "help", () => _ = SubmitLineAsync("help", isCommand: true), null),
                new Shortcut(Key.F2, "edit", () => commandArgsField.SetFocus(), null),
                new Shortcut(Key.F5, "run", () => StartRun(), null),
                new Shortcut(Key.F6, "clear", () => _ = SubmitLineAsync("clear", isCommand: true), null),
                new Shortcut(Key.F7, "undo", () => DoUndo(), null),
                new Shortcut(Key.F10, "exit", () => _ = SubmitLineAsync("exit", isCommand: true), null),
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

                    // H1: advertise Esc-to-cancel while a run is in progress.
                    bool running = isBusy && busyText.Equals("running", StringComparison.OrdinalIgnoreCase);
                    busyItem.Title = isBusy
                        ? (running
                            ? "Running… (Esc cancel)"
                            : (string.IsNullOrWhiteSpace(busyText) ? "Busy…" : ("Busy: " + busyText)))
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

            // Strip the leading "ffmpeg " so the editable field shows args only (the prefix is a static affix).
            static string StripFfmpegPrefix(string? fullCommand)
            {
                string trimmed = (fullCommand ?? string.Empty).TrimStart();
                return trimmed.StartsWith("ffmpeg ", StringComparison.OrdinalIgnoreCase)
                    ? trimmed.Substring("ffmpeg ".Length).TrimStart()
                    : trimmed;
            }

            void RenderFromState(CommandState state)
            {
                app.Invoke(() =>
                {
                    bool hasCommand = !string.IsNullOrWhiteSpace(state.CurrentCommand);

                    // C2/H5: the prefix is a static affix; the args go into the editable field.
                    // When there is no command, the field is empty and the prefix is shown dim.
                    commandArgsField.Text = hasCommand ? StripFfmpegPrefix(state.CurrentCommand) : string.Empty;
                    ffmpegPrefixLabel.SetScheme(hasCommand ? schemeCommandAvailable : schemeCommandNone);

                    bool hasBaseReq = !string.IsNullOrWhiteSpace(state.BaseRequest);
                    string baseReq = hasBaseReq ? state.BaseRequest! : "(no request yet — type one below)";
                    baseRequestLabel.Text = baseReq;
                    baseRequestLabel.SetScheme(hasBaseReq ? schemeBaseRequest : schemeDim);

                    if (state.Adjustments.Count == 0)
                    {
                        adjustmentsView.Text = "(no adjustments)";
                    }
                    else
                    {
                        var sb = new StringBuilder();
                        foreach (string a in state.Adjustments)
                        {
                            sb.Append("• ");
                            sb.AppendLine(a);
                        }

                        adjustmentsView.Text = sb.ToString().TrimEnd();
                    }

                    currentCommandFrame.SetNeedsDraw();
                    promptContextFrame.SetNeedsDraw();
                });
            }

            // C2: commit a direct command edit. Called when the user presses Enter in the args field.
            void CommitCommandEdit()
            {
                if (isBusy)
                    return;

                string args = commandArgsField.Text ?? string.Empty;

                // Don't let an empty field commit a bare "ffmpeg " command.
                if (string.IsNullOrWhiteSpace(args))
                {
                    app.Invoke(() => inputField.SetFocus());
                    return;
                }

                // session.SetCommand normalizes the leading "ffmpeg " itself.
                CommandSessionResult result = session.SetCommand(args);
                RenderFromState(result.State);

                if (!string.IsNullOrWhiteSpace(result.Message))
                    AppendLog(result.Message, isDim: true);

                // Return focus to the main request input after committing.
                app.Invoke(() => inputField.SetFocus());
            }

            // H3: undo the last state change and re-render every dependent pane.
            void DoUndo()
            {
                if (isBusy)
                    return;

                CommandSessionResult result = session.Undo();
                RenderFromState(result.State);
                AppendLog(result.Message, isDim: true);
            }

            // C4: surface "AI is not configured" prominently rather than only as a dim log line.
            void ShowAiNotConfiguredAlert()
            {
                app.Invoke(() =>
                {
                    MessageBox.ErrorQuery(
                        app,
                        "Azure OpenAI not configured",
                        "Generation is unavailable until Azure OpenAI is configured.\n\n" +
                        "Set these in appsettings.json under \"AzureOpenAI\":\n" +
                        "  • Endpoint\n  • ApiKey\n  • Deployment\n\n" +
                        "You can still edit and run commands manually.",
                        "OK");
                });
            }

            // H1/H2: dedicated run handler. Streams stdout/stderr LIVE via session.RunAsync callbacks
            // (marshalled to the UI thread) and is cancelable via Esc. Replaces the old "run" path that
            // went through ApplyInputLine("run").
            void StartRun()
            {
                if (isBusy)
                    return;

                if (string.IsNullOrWhiteSpace(session.State.CurrentCommand))
                {
                    AppendLog("No command to run", isError: true);
                    return;
                }

                runCts = new CancellationTokenSource();
                CancellationToken ct = runCts.Token;

                SetBusy(true, "running");
                AppendLog("> run");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        CommandSessionResult result = await session.RunAsync(
                            onStdoutLine: line => AppendLog(line),
                            onStderrLine: line => AppendLog(line, isError: true),
                            ct).ConfigureAwait(false);

                        app.Invoke(() =>
                        {
                            RenderFromState(result.State);

                            if (result.State.LastExitCode.HasValue)
                            {
                                int code = result.State.LastExitCode.Value;
                                AppendLog("Exit code: " + code, isError: code != 0);
                            }
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        // H1: executor killed the process tree on cancel; report and return to idle.
                        AppendLog("Canceled", isError: true);
                    }
                    catch (Exception ex)
                    {
                        AppendLog("Error: " + ex.Message, isError: true);
                    }
                    finally
                    {
                        runCts?.Dispose();
                        runCts = null;
                        SetBusy(false);
                    }
                });
            }

            // H1: cancel the in-progress run, if any. Returns true if a cancel was issued.
            bool CancelRun()
            {
                CancellationTokenSource? cts = runCts;
                if (cts != null)
                {
                    try
                    {
                        cts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Run already finished between the check and the cancel; nothing to do.
                    }

                    return true;
                }

                return false;
            }

            // isCommand: true for the status-bar function-key actions (clear/exit/help), which dispatch
            // reserved commands. Typed input is always free text (natural language) and never a command.
            // NOTE: "run" no longer flows through here — it has its own streaming/cancelable handler (StartRun).
            async Task SubmitLineAsync(string line, bool isCommand = false)
            {
                // Called from UI thread, but do not block it.
                string normalized = (line ?? string.Empty).Trim();

                if (normalized.Length == 0)
                    return;

                // Minimal defined behavior: block quitting while busy (running/generating).
                // Only the `exit` command action can quit; typed text is never a command.
                if (isCommand && isBusy && normalized.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    AppendLog("Busy: cannot exit while running. Please wait for completion.", isError: true);
                    return;
                }

                SetBusy(true, "working");

                AppendLog($"> {normalized}");

                try
                {
                    // Function-key/status-bar actions dispatch reserved commands; typed input is always free text.
                    CommandSessionResult result = isCommand
                        ? await session.ApplyInputLine(normalized).ConfigureAwait(false)
                        : await session.ApplyTextLine(normalized).ConfigureAwait(false);

                    // C4: if generation failed because AI isn't configured, surface it as an alert.
                    bool notConfigured =
                        !string.IsNullOrWhiteSpace(result.State.LastError) &&
                        result.State.LastError!.Contains("AI is not configured", StringComparison.OrdinalIgnoreCase);
                    if (!notConfigured && !string.IsNullOrWhiteSpace(result.Message))
                    {
                        notConfigured = result.Message.Contains("AI is not configured", StringComparison.OrdinalIgnoreCase);
                    }

                    if (notConfigured)
                        ShowAiNotConfiguredAlert();

                    app.Invoke(() =>
                    {
                        RenderFromState(result.State);

                        // Respect "clear stored command + output" semantics.
                        // NOTE: Clear the *UI* output view/log unconditionally on the `clear` action,
                        // even if the session state did not change (e.g., clearing after `help` output).
                        if (isCommand && normalized.Equals("clear", StringComparison.OrdinalIgnoreCase))
                        {
                            outputLog.Clear();
                            outputTextView.Text = string.Empty;
                            outputTextView.SetNeedsDraw();
                        }

                        if (!string.IsNullOrWhiteSpace(result.Message))
                        {
                            bool isHelp =
                                (isCommand && normalized.Equals("help", StringComparison.OrdinalIgnoreCase)) ||
                                result.Message.StartsWith("Usage:", StringComparison.OrdinalIgnoreCase);

                            // Treat messages as errors if stderr was present or the message looks like an error.
                            bool isError =
                                (!string.IsNullOrWhiteSpace(result.State.LastError)) ||
                                result.Message.StartsWith("Error", StringComparison.OrdinalIgnoreCase) ||
                                result.Message.StartsWith("No command to run", StringComparison.OrdinalIgnoreCase);

                            AppendLog(result.Message, isError: isError, isDim: isHelp && !isError);
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
                // Typed input is always free text — never a reserved command.
                _ = SubmitLineAsync(line, isCommand: false);
            };

            // C2: Enter in the args field commits a direct edit via session.SetCommand(...).
            commandArgsField.KeyDown += (sender, key) =>
            {
                if (key != Key.Enter)
                    return;

                key.Handled = true;
                CommitCommandEdit();
            };

            // H1: bind Esc (window-wide) to cancel a running command. Only meaningful while a run
            // is in progress; otherwise let the key fall through.
            win.KeyDown += (sender, key) =>
            {
                if (key != Key.Esc)
                    return;

                if (CancelRun())
                    key.Handled = true;
            };

            // Initial render.
            RenderFromState(session.State);

            // Ensure the input is usable immediately on startup. Tab moves to the editable args field.
            inputField.SetFocus();

            // C4: warn about the unconfigured state once the UI is up. The app still opens.
            if (!aiConfigured)
            {
                AppendLog(
                    "Azure OpenAI is not configured — generation is disabled until appsettings.json is set up. " +
                    "You can still edit and run commands manually.",
                    isDim: true);
                ShowAiNotConfiguredAlert();
            }

            app.Run(win, null);
            win.Dispose();
        }
        finally
        {
            app.Dispose();
        }
    }

    // A TextField that renders a more visible terminal caret (a blinking block) while focused,
    // instead of the driver default that can be hard to spot against the dark scheme. Terminal.Gui v2
    // assigns the Cursor (an immutable record carrying position + style) during the draw pass, so we
    // keep the position the base view computed and only override the style afterward.
    private sealed class VisibleCursorTextField : TextField
    {
        public CursorStyle FocusedCursorStyle { get; init; } = CursorStyle.BlinkingBlock;

        // OnDrawComplete runs after the base TextField has positioned its caret for this draw pass,
        // so the position is already correct here — we only override the style to make it stand out.
        protected override void OnDrawComplete(DrawContext? context)
        {
            base.OnDrawComplete(context);

            if (HasFocus && Cursor.Position is not null)
            {
                Cursor = Cursor with { Style = FocusedCursorStyle };
            }
        }
    }
}
