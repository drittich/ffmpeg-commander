# FFmpeg Commander (`em`)

> Describe what you want in plain English, and let AI write the FFmpeg command for you.

FFmpeg is incredibly powerful, but its command-line syntax is famously hard to
remember. **FFmpeg Commander** is a friendly terminal app that bridges that gap:
you type something like *"convert input.mov to a 720p mp4 and strip the audio"*,
and it generates a ready-to-run `ffmpeg` command. You can then refine it in plain
English, tweak the arguments by hand, and run it — all without leaving the terminal.

```
┌ Current command  (Tab to edit · Enter to commit) ───────────────────────────┐
│ ffmpeg -i input.mov -vf scale=-2:720 -an output.mp4                          │
└──────────────────────────────────────────────────────────────────────────────┘
┌ Prompt context ──────────────────────────────────────────────────────────────┐
│ convert input.mov to 720p mp4 and remove the audio                            │
│ • make it 720p                                                                │
│ • remove the audio                                                            │
└──────────────────────────────────────────────────────────────────────────────┘
┌ Output ────────────────────────────────────────────────────────────────────┐
│ > run                                                                         │
│ frame=  812 fps=240 q=28.0 size=    2304kB time=00:00:27.10 ...               │
│ Exit code: 0                                                                  │
└──────────────────────────────────────────────────────────────────────────────┘
┌ Request  (type plain English · Enter to send) ───────────────────────────────┐
│ make it 720p█                                                                 │
└──────────────────────────────────────────────────────────────────────────────┘
 F1 help  F2 edit  F5 run  F6 clear  F7 undo  F10 exit                    Idle
```

## What it does

- **Generate** — Type a natural-language request and it produces a complete
  `ffmpeg` command.
- **Refine** — Keep typing follow-up instructions (*"also make it 30fps"*,
  *"use a higher quality preset"*) and the command is adjusted in place. Each
  refinement is remembered in the **Prompt context** pane.
- **Edit by hand** — Press **F2** to jump into the command field and tweak the
  arguments directly when you'd rather not ask the AI.
- **Run it** — Press **F5** to execute the current command. Output streams live
  into the **Output** pane, and you can cancel a long-running job with **Esc**.
- **Undo** — Press **F7** to step back through any change (generate, refine,
  edit, or clear).

## Requirements

- **[.NET 10 SDK](https://dotnet.microsoft.com/download)** or later.
- **[FFmpeg](https://ffmpeg.org/download.html)** installed and available on your
  `PATH` (so that typing `ffmpeg` in a terminal works). Commander runs the
  generated command for real — it doesn't bundle FFmpeg.
- An **Azure OpenAI** resource with a deployed chat model (used to generate the
  commands). Without it, the app still opens and you can edit/run commands
  manually — you just won't get AI generation.
- **Windows** — commands are executed through `cmd.exe`, so this is currently a
  Windows-focused tool.

## Configuration

AI generation is powered by Azure OpenAI. Create an `appsettings.json` in the
project root (it's git-ignored, so it won't be committed) with the following:

```json
{
    "AzureOpenAI": {
        "Endpoint": "https://your-resource.openai.azure.com/",
        "ApiKey": "your-api-key",
        "Deployment": "your-chat-model-deployment-name"
    }
}
```

| Setting      | Description                                                                 |
| ------------ | --------------------------------------------------------------------------- |
| `Endpoint`   | The base URL of your Azure OpenAI resource.                                 |
| `ApiKey`     | A key for that resource.                                                     |
| `Deployment` | The name of your deployed chat model (e.g. a GPT deployment), **not** the model name. |

All three values must be present for generation to be enabled. If any are
missing, Commander shows a clear "not configured" notice on startup and disables
generation — manual editing and running still work.

> 🔒 `appsettings.json` is git-ignored, so your key stays on your machine and out
> of source control. Keep it that way — don't force-add it or paste real keys
> anywhere that gets committed.

## Getting started

From the project directory:

```sh
# Restore dependencies and run
dotnet run
```

Or build a release binary:

```sh
dotnet build -c Release
```

The app launches straight into the terminal UI (there is no command-line / batch
mode — it's always interactive).

## How to use it

1. **Start typing** in the **Request** field at the bottom and press **Enter**.
   With no command yet, your text is treated as the request to generate from.
2. **Refine** by typing more instructions and pressing **Enter** — each one
   adjusts the current command and is added to the **Prompt context** history.
3. **Edit directly** with **F2**: focus moves to the command field, where you can
   change the arguments. Press **Enter** to commit your edit, or **Tab** to move
   between the request field and the command field.
4. **Run** with **F5** and watch the live output. Press **Esc** to cancel a run
   in progress.
5. **Undo** any change with **F7**, **clear** everything with **F6**, and **quit**
   with **F10**.

> Tip: You can prefix your request with `ffmpeg` (e.g. *"ffmpeg make a gif from
> the first 5 seconds"*) — the leading word is stripped automatically so the
> stored command never doubles up on `ffmpeg`.

### Keyboard reference

| Key       | Action                                                       |
| --------- | ------------------------------------------------------------ |
| **Enter** | Send the request / commit a command edit                     |
| **Tab**   | Move between the request field and the command field         |
| **F1**    | Show help                                                    |
| **F2**    | Edit the current command (focus the args field)              |
| **F5**    | Run the current command                                      |
| **F6**    | Clear the stored command, history, and output                |
| **F7**    | Undo the last change                                         |
| **F10**   | Exit                                                         |
| **Esc**   | Cancel a command that is currently running                   |

## How it works

FFmpeg Commander is a .NET console app built on a few focused pieces:

| File                                         | Responsibility                                                                          |
| -------------------------------------------- | --------------------------------------------------------------------------------------- |
| [Program.cs](Program.cs)                     | Entry point — launches the TUI.                                                         |
| [TuiApp.cs](TuiApp.cs)                        | The terminal user interface, built with [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) v2. |
| [CommandGenerator.cs](CommandGenerator.cs)    | Talks to Azure OpenAI to generate and adjust FFmpeg argument strings.                  |
| [CommandSession.cs](CommandSession.cs)        | Orchestrates the state machine (generate → adjust → edit → run) and the undo history.  |
| [CommandExecutor.cs](CommandExecutor.cs)      | Runs the command via `cmd.exe` and streams stdout/stderr back line by line.            |
| [CommandState.cs](CommandState.cs)            | The session's current command, request, refinement history, and last run result.       |

The AI is instructed to return **only** a single line of FFmpeg arguments (no
prose, no markdown), and Commander always stores a complete, executable command
beginning with `ffmpeg`.

## Tests

Unit tests live in the [em.Tests](em.Tests/) project:

```sh
dotnet test
```

## A note on safety

Commander executes the generated command on your machine. AI-generated commands
are usually fine, but always glance at the **Current command** pane before
pressing **F5** — especially for anything that overwrites or deletes files.
