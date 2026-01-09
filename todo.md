# Interactive command-builder TUI (Plan + TODO)

## Target behavior (acceptance criteria)
- Start with an empty input line (no `>` prompt); user types a natural-language request.
- App generates a command and **renders it above** the input line; input becomes empty again.
- Additional natural-language lines are treated as **adjustments** to the stored command; the **updated prompt context** and updated command are rendered above the input line.
- Entering `run` executes the currently stored command as if typed manually.
- Entering `clear` clears the stored command and any associated prompt context.
- Uses **either** Spectre.Console **or** Terminal.Gui for a slick interactive TUI with effective color usage.

## Notes from current code (baseline)
- Current behavior is a simple Console REPL in [`Program.cs`](Program.cs:1) with:
  - “generate -> accept/edit -> execute” keypress flow
  - stored state: `s_lastAcceptedCommand`
  - OpenAI integration via `CallOpenAIAPI()` and `CallOpenAIAPIAdjust()`
  - execution via `cmd.exe /C ...` in `ExecuteCommand()`
- Current dependencies are in [`em.csproj`](em.csproj:1) and do not include any TUI library.

---

## Implementation approach (high level)
- Replace the current REPL with a **stateful TUI loop**:
  - State: `CurrentCommand`, `BaseRequest`, `Adjustments[]`, `LastRunOutput`, `LastError`, `Mode` (idle / generating / running).
  - Input handler:
    - `run`, `clear`, `exit` (and optional `help`)
    - otherwise: if no current command -> generate; else -> adjust
  - Renderer:
    - “Current command” panel (colored)
    - “Prompt context” panel (base + adjustments)
    - “Output” panel (stdout/stderr, colored)
    - input line at bottom

- Library choice:
  - **Terminal.Gui**: best for multi-pane UI, persistent input box, scrollable output, async-friendly.
  - **Spectre.Console**: workable via `AnsiConsole.Live` + prompt loop, but multi-pane feels more manual.

---

## TODO (granular checklist)

### 0) Decisions / requirements clarification (minimal)
- [x] Confirm whether the TUI should replace REPL only, or also change the “args-as-one-shot” behavior in [`Program.cs`](Program.cs:15) — remove one-shot args mode; TUI always runs.
- [x] Confirm desired reserved commands besides `run` and `clear` (at minimum keep `exit`; decide on `help`) — reserved commands: `run`, `clear`, `exit`, `help`.
- [x] Decide on the primary TUI library:
  - [x] Option A: Terminal.Gui (recommended)
  - [ ] Option B: Spectre.Console

### 1) Refactor current code into testable components (no UI yet)
- [x] Create a new `CommandState` model to hold:
  - [x] `string? CurrentCommand`
  - [x] `string? BaseRequest`
  - [x] `List<string> Adjustments`
  - [x] `string? LastOutput`
  - [x] `string? LastError`
- [x] Extract OpenAI logic from [`Program.cs`](Program.cs:133) into a dedicated service (e.g. `CommandGenerator`):
  - [x] `GenerateFromDescription(description) -> full command or args`
  - [x] `AdjustFromInstruction(previousFullCommand, instruction) -> updated args`
  - [x] Keep existing prompt/system messages but relocate them.
- [ ] Extract process execution from [`Program.cs`](Program.cs:315) into `CommandExecutor`:
  - [ ] Support capturing stdout + stderr
  - [ ] Return exit code and combined output model
  - [ ] Ensure it runs without blocking the UI thread (async wrapper)
- [ ] Introduce a single orchestration method (e.g. `CommandSession.ApplyInputLine(line)`):
  - [ ] If `line == run`: validate `CurrentCommand` exists; execute; store output
  - [ ] If `line == clear`: clear state; clear output
  - [ ] If `line == exit`: request app shutdown
  - [ ] Else: generate/adjust and update state accordingly
- [ ] Preserve current “ffmpeg-only” assumption explicitly:
  - [ ] Decide whether to store “full command” always including `ffmpeg`
  - [ ] Ensure adjustment path continues to behave like existing logic in [`Program.cs`](Program.cs:68).

### 2) Add TUI dependency + bootstrap
- [ ] Add NuGet package reference:
  - [ ] Terminal.Gui: add package to [`em.csproj`](em.csproj:1)
  - [ ] OR Spectre.Console: add package to [`em.csproj`](em.csproj:1)
- [ ] Update [`Program.cs`](Program.cs:15) entrypoint to route to:
  - [ ] One-shot mode when args are present (if retained)
  - [ ] TUI mode when no args are present

### 3) Build the TUI layout (rendering)
#### If using Terminal.Gui
- [ ] Create a TUI composition root (e.g. `TuiApp.Run()`):
  - [ ] Initialize `Application.Init()`
  - [ ] Create main `Window`
- [ ] Add “Current command” view:
  - [ ] A framed panel that renders `state.CurrentCommand` (or “(none)”)
  - [ ] Color: green for available command; gray when none
- [ ] Add “Prompt context” view:
  - [ ] Show base request + bullet list of adjustments
  - [ ] Color: base request in cyan; adjustments in dim/gray
- [ ] Add “Output” view:
  - [ ] Scrollable text view for stdout/stderr
  - [ ] Color: stdout normal; stderr red; exit code highlighted
- [ ] Add bottom input field:
  - [ ] Single-line `TextField`
  - [ ] Enter submits current text; clears field after submit
- [ ] Implement a status/footer bar:
  - [ ] Show hints: `run | clear | exit`
  - [ ] Show busy indicator when generating/running

#### If using Spectre.Console
- [ ] Create a Live layout with:
  - [ ] Header panels for command + prompt context
  - [ ] Output panel
  - [ ] Prompt loop at bottom using `AnsiConsole.Prompt(...)`
- [ ] Ensure the prompt returns to blank after each submission
- [ ] Ensure rerender occurs after generation/adjust/run/clear

### 4) Input semantics (exact behavior)
- [ ] Implement reserved commands:
  - [ ] `run`: execute stored command; if none, show a warning
  - [ ] `clear`: clear stored command and prompt context
  - [ ] `exit`: quit
  - [ ] Optional `help`: show quick usage panel
- [ ] Implement natural language handling:
  - [ ] If `CurrentCommand` is null: treat line as base request; call generate; store `BaseRequest`
  - [ ] Else: treat line as adjustment; append to `Adjustments`; call adjust; update `CurrentCommand`
- [ ] Decide how to display “updated form above the command line”:
  - [ ] Command panel updates immediately after generation/adjustment
  - [ ] Prompt context panel updates with base + adjustments

### 5) Execution behavior + output capture
- [ ] Keep Windows execution semantics (`cmd.exe /C ...`) consistent with [`ExecuteCommand()`](Program.cs:315).
- [ ] Capture:
  - [ ] stdout
  - [ ] stderr
  - [ ] exit code
- [ ] Display output in the TUI output panel without breaking layout.
- [ ] Ensure long-running commands don’t freeze UI:
  - [ ] Run execution on background task
  - [ ] Stream or append output (optional), or display after completion (minimum)

### 6) Color + UX polish
- [ ] Use distinct colors for:
  - [ ] Current command (green)
  - [ ] Prompt context (cyan/gray)
  - [ ] Instructions/hints (dim)
  - [ ] Errors (red)
- [ ] Add subtle separators and consistent spacing.
- [ ] Add small confirmations:
  - [ ] “Command stored” after generation/adjustment
  - [ ] “Cleared” after clear
  - [ ] “No command to run” warning when `run` with empty state

### 7) Validation, safety, and edge cases
- [ ] Prevent executing when `CurrentCommand` is empty/whitespace.
- [ ] Handle OpenAI failures:
  - [ ] Show error in output/status area
  - [ ] Keep previous command if adjust fails (consistent with current adjust fallback)
- [ ] Handle cancellation / quit while running (define behavior):
  - [ ] At minimum: block quit while running, or allow quit after completion
- [ ] Ensure `appsettings.json` behavior remains unchanged (copied to output by [`em.csproj`](em.csproj:16)).

### 8) Tests (optional but recommended)
- [ ] Add unit tests for `CommandSession.ApplyInputLine` state transitions:
  - [ ] First NL line stores base request + command
  - [ ] Second NL line appends adjustment + updates command
  - [ ] `clear` resets state
  - [ ] `run` with no command warns/does nothing
- [ ] Add unit tests for reserved command parsing (`run`, `clear`, `exit`).

### 9) Manual verification checklist (VS Code)
- [ ] Run app with no args and verify TUI starts and input is blank.
- [ ] Enter a request; verify command appears above input and input clears.
- [ ] Enter an adjustment; verify prompt context updates and command changes.
- [ ] Enter `run`; verify command executes and output appears.
- [ ] Enter `clear`; verify command + prompt context cleared.
- [ ] Enter `exit`; verify clean shutdown.

---

## Proposed structure (planned files)
- [ ] Keep entrypoint in [`Program.cs`](Program.cs:1) minimal.
- [ ] Add new files (names flexible):
  - [ ] `CommandState.cs`
  - [ ] `CommandGenerator.cs`
  - [ ] `CommandExecutor.cs`
  - [ ] `CommandSession.cs`
  - [ ] `TuiApp.cs` (Terminal.Gui or Spectre.Console implementation)
