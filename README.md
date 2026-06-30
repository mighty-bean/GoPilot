# GoPilot

A Windows desktop GUI for the [GitHub Copilot SDK](libs/copilot-sdk). GoPilot wraps the Copilot CLI in a WinForms window so you can chat, approve tool operations, and manage sessions without working in a terminal.

![screenshot](screenshot.png)

## Features

- Multi-line prompt editor. Enter inserts a newline, Ctrl+Enter (or the Send button) submits.
- Drag and drop files or folders from File Explorer or the VS Solution Explorer onto the prompt.
- Paste clipboard images directly; sent as PNGs, previewed inline.
- Past Sessions dialog to browse, resume, and bulk-delete persisted sessions.
- Model, Mode, Effort, Fleet, and Auto-approve choices persist between launches.
- Rendered and Raw output tabs. Rendered handles Markdown, Mermaid, and syntax-highlighted code.
- Pan, zoom, and fullscreen for oversized diagrams, code blocks, and tables.
- Context meter with an offer to compact or restart at 85% usage.
- Manual session refresh: compact, restart with summary, or fresh start.
- Skill Tree: ordered folders that contribute `skills/`, `agents/`, `prompts/`, and `gopilot-instructions.md` to every session.
- Custom agents and skills auto-discovered and insertable as `@agent:name` / `@skill:name`.
- Plan, Autopilot, and Fleet modes alongside Standard chat.
- Tool permission dialog with Allow / Approve Similar / Deny, plus an Auto-approve toggle.
- Caveman Mode: optional client-side prompt compression to cut tokens.
- **NEW** Local LLM filter: optional codellama pre-pass (on this machine or any Ollama host on your network) that minimizes prompts and answers simple requests locally to save cloud tokens.
- All option toggles show as badges on the Options button in a fixed order; enabled options appear in full colour and disabled options are greyed out.
- Sending while a turn is in flight interrupts it instead of queueing.
- Dark theme throughout.
- Self-update check.

## Why this exists

I find it difficult to work in a terminal, so I built a Windows app that makes it easier to edit prompts, save and restore session state, and launch tools at the workspace root. Think of it as an alternate front-end for Copilot CLI aimed at people who prefer standard Windows forms and editors. New features added regularly as I think of them :)

## Requirements

- Windows 11 or later
- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- A GitHub account with Copilot access (authenticated via `gh` CLI or environment)
- Remember: You'll need to install the Copilot CLI and run it at least once to login.

## Getting Started

1. Build:
   ```
   dotnet build GoPilot.sln
   ```
2. Run:
   ```
   dotnet run --project GoPilot/GoPilot.csproj
   ```
   Or launch the built `GoPilot.exe`. Pass a folder as the first argument to open it at startup:
   ```
   GoPilot.exe C:\path\to\my\project
   ```
3. Pick a workspace with **Session ▸ 📂 New Session...**. The Send button stays disabled until a workspace is open.
4. Type a prompt and press **Send** (or Ctrl+Enter).

## User Interface

The window has a menu bar, a toolbar with the prompt box, an output panel, and a status bar.

### Session menu

| Item | Description |
|---|---|
| 📂 New Session... | Pick a workspace folder and start a fresh session. |
| 📋 Past Sessions... | Browse, resume, or delete persisted sessions. |
| 📝 Summarize | Ask Copilot for a summary of the session so far. |
| 🗑 Clear Output | Clear the panel. The session itself is not reset. |
| 💤 Refresh ▸ | Compact, restart with summary, or fresh start. |

### References menu

Attach files and folders, or insert `@agent:`, `@skill:`, or prompt-file references. Attached files appear as chips above the output and as `@relative/path` tokens at the prompt caret.

### Tools menu

PowerShell, File Explorer, and VS Code at the workspace root. Skill Tree, Skill Sources, and Browse Skill Catalog editors for managing reusable assets.

### Toolbar

| Control | Description |
|---|---|
| Model | Pick the AI model. |
| Mode | Standard, Plan, or Autopilot. |
| Effort | Reasoning effort for the selected model when supported. Defaults to the highest available. |
| ⚙ Options ▾ | Session toggles. Active toggles show as coloured badges on the button. |
| ⬛ Stop | Cancel the current response. No-op when nothing is in flight. |
| ▶ Send | Submit the prompt. Shortcut: Ctrl+Enter. |

Model, Mode, Effort, Fleet, and Auto-approve are persisted to `gopilot.ini` under `[Session]` and restored on the next launch.

### Options dropdown

| Item | Default | Description |
|---|---|---|
| ⚠️ Auto-approve tools | Off | Skip the permission dialog. |
| 👥 Fleet mode | Off | Spawn parallel sub-agents on large tasks. Toggling triggers a summary-and-restart handoff on next send. |
| 🦴 Caveman Mode | Off | Compress prompts client-side. See [Caveman Mode](#caveman-mode). |
| 🧠 Local LLM filter | Off | Route prompts through a local codellama model first. See [Local LLM Filter](#local-llm-filter). |
| 🧠 Local LLM settings... | - | Pick the Ollama server (localhost or a network machine by host/IP), model, and confidence threshold. See [Local LLM Filter](#local-llm-filter). |
| 💬 Show Working Steps | Off | Keep Reasoning and Tool sections expanded after they finish. |

### Prompt box

Drag files or folders to attach, paste images from the clipboard, navigate prompt history with the ▲ / ▼ buttons. Right-click for Cut/Copy/Paste plus shortcuts to the References menu.

Sending while Copilot is mid-response interrupts the current turn rather than queueing behind it. The Stop button only acts when something is in flight.

References use the `@` prefix:

| Reference | Inserted via | Meaning |
|---|---|---|
| `@relative/path/to/file.ext` | Add File... or drag-drop | Attach a file. |
| `@relative/path/to/folder` | Add Folder... or drag-drop | Attach a folder. |
| `@agent:name` | List Agents... | Reference a custom agent. |
| `@skill:name` | List Skills... | Reference a skill. |

### Output panel

| Tab | Description |
|---|---|
| Rendered | HTML view (WebView2). Markdown, Mermaid, and syntax-highlighted code. |
| Raw | Plain-text transcript. Useful for copying or as a fallback. |

Mermaid diagrams, wide code blocks, and large tables get a scrollable viewport. Hover for the toolbar (`+`, `-`, `Fit`, `Full`). Mouse wheel zooms, left-drag pans, double-click resets, Esc exits fullscreen.

Color coding (both tabs where applicable): blue for your prompt, green for Copilot's response, yellow for tool activity, red for errors.

### Status bar

Connection status, Copilot version, agent activity, context meter (green below 60%, amber 60-85%, red above), and session info.

## Refreshing the Session

| Option | What it does |
|---|---|
| ⚡ Compact (fast) | In-place compaction via `session.history.compact`. Session ID preserved. |
| 🔄 Restart with summary | Saves a one-page summary to `%LOCALAPPDATA%\GoPilot\workspaces\<key>\dreams\`, opens a fresh session, seeds it with the summary. |
| 🆕 Fresh start | Discards all context and opens a brand-new session in the same folder. |

At 85% context usage GoPilot offers to compact or restart automatically.

Changing Mode or toggling Fleet during an active session forces a new session (the system message is baked in at creation). Rather than discarding context, GoPilot defers the reset: on the next send it runs the same summary-and-restart flow, then forwards the prompt to the fresh session.

## Past Sessions

Every session GoPilot creates gets a stable ID of the form `{LeafFolder}-{MM-dd-yyyy-HHmmss}` (e.g. `GoPilot-04-24-2026-211734`). Metadata (workspace, model, mode, fleet, auto-approve, timestamp, first-prompt description) is recorded in `gopilot-sessions.json` next to the executable.

**Session ▸ 📋 Past Sessions...** opens a list of every persisted session with columns for Session ID, Workspace, Model, Mode, Created, and Description.

- **Resume** restores the recorded settings, opens the workspace, reconnects via `ResumeSessionAsync`, and replays the conversation into both output tabs.
- **Delete** removes the session from the SDK store, deletes its scratch folder under `~/.copilot/session-state/<id>/`, and drops the row from the local metadata file. Multi-select is supported. The `Delete` key is a shortcut.
- The current session shows as `(current)` and is protected from Resume and Delete.
- Stale local metadata is pruned automatically every time the dialog opens.

## Modes

| Mode | Behaviour |
|---|---|
| Standard | Direct conversational chat. |
| Plan | Plan a sequence of actions before executing them. |
| Autopilot | Fully autonomous execution with minimal prompting. |

## Fleet Mode

With Fleet on, Copilot can spawn parallel sub-agents on different parts of a task. The output panel shows each agent's progress; the session completes when the last sub-agent finishes. Best for large refactors and tasks that decompose cleanly.

## Skill Tree

**Tools ▸ 🌳 Skill Tree...** edits an ordered list of folders that contribute reusable assets to every session GoPilot starts. For each folder:

| Subfolder | Effect |
|---|---|
| `skills/` | Added to the session's `skillDirectories`. |
| `agents/` | Each `*.md` file is parsed into a custom agent. |
| `prompts/` | Each `*.md` file becomes a named prompt template surfaced through References ▸ List Prompts... |

A `gopilot-instructions.md` file at the root of a Skill Tree folder is concatenated into the system message. The list is persisted in `gopilot.ini` under `[SkillTree]`. Order matters: later entries override earlier ones on name collisions, and the project folder always wins overall.

Editing the Skill Tree triggers the same summary-and-restart handoff used by Mode and Fleet changes, because skills and agents are baked in at session creation. Prompts are picked up live since they are attached at send time.

## Caveman Mode

When on, GoPilot rewrites each outgoing prompt into a token-minimal form before it reaches the model, and asks the model to reply in the same style. The reduced prompt is what shows as your `👤 You:` echo; there is no hidden rewrite.

- Strips articles, prepositions, auxiliaries, filler words, pleasantries, hedges, polite-clause openers, leading subjects, `-ly` adverbs, and decorative punctuation.
- Preserves negations, fenced code blocks, inline backticks, `@references`, URLs, and any line whose first non-whitespace characters are `!!` (the marker is stripped).
- Number words 1-20, 30-90, and hundred/thousand/million/billion are converted to digits.

After each send a discreet meta line reports the before/after character count. Typical prose savings are 10-20%. Code-heavy prompts will see little or none.

Toggling Caveman mid-session sends a single `CAVEMAN MODE ON.` or `CAVEMAN MODE OFF.` side instruction so the model switches style on its next reply without a session restart. The setting is persisted in `gopilot.ini` under `[Caveman]`.

## Local LLM Filter

An optional pre-processor (powered by [OllamaSharp](https://github.com/awaescher/OllamaSharp)) that sits between you and the cloud model to cut token spend. The Ollama server can run on this machine *or* on another machine on your home network:

- **Minimize:** every prompt is rewritten to the fewest tokens that preserve intent (code, paths, and names kept verbatim) before being forwarded to the cloud, with a short directive telling the cloud model to answer concisely - response tokens are the costliest, so this is where most AIC is saved.
- **Answer locally:** when the local model is confident enough (default threshold 0.85) it answers directly and the cloud is skipped entirely - zero cloud tokens, zero AIC.

### Choosing the server

**Options > Local LLM settings...** opens a dialog where you set:

| Field | Meaning |
|---|---|
| Server host or IP | `localhost` for this machine, or the host name / IP of another machine on your network (e.g. `192.168.1.50`). A full `http://host:port` URL is also accepted. |
| Port | Ollama's port (default `11434`). |
| Model | A model installed on that server, or blank to auto-detect. |
| Answer-locally confidence | Threshold (0.00 - 1.00) the local model must clear to answer without the cloud. |

Changing the server or model re-detects against the new target immediately when the filter is on; the status line reports the chosen model and host, e.g. `Local filter: ready (codellama:13b-instruct @ 192.168.1.50)`.

When **Model** is blank, GoPilot auto-detects dedicated VRAM (via `nvidia-smi`, falling back to the registry) and picks a fitting model: `codellama:7b-instruct` (~8GB), `codellama:13b-instruct` (~16GB), or `codellama:34b-instruct` (24GB+). VRAM describes *this* machine, so when the server is remote GoPilot instead prefers a codellama model already installed on that host. The cloud always receives the original prompt if the filter is off, unavailable, or errors.

### Setup

The filter is optional. If you don't run Ollama, GoPilot ignores it and works normally - prompts go straight to the cloud, with one "unavailable" status line when toggled.

1. Install Ollama from [ollama.com](https://ollama.com) and let it run. By default it serves on `localhost:11434`; to reach it from other machines, start it with `OLLAMA_HOST=0.0.0.0:11434` and allow the port through that machine's firewall.
2. Pull a model on the server that will run it: `ollama pull codellama:13b-instruct` (or 7b/34b to match its VRAM).
3. If the server is on another machine, open **Options > Local LLM settings...** and enter its host/IP (and model).
4. Enable **Options > Local LLM filter**. When GoPilot finds the model it shows `Local filter: ready`; otherwise it bypasses and forwards your prompt unchanged.

Settings persist in `gopilot.ini` under `[LocalFilter]` (`Enabled`, `Endpoint`, `Model`, `Threshold`); leave `Model` blank for auto-selection.

## Tool Permission Dialog

When Copilot wants to run an operation and Auto-approve is off, a dialog appears:

| Button | Effect |
|---|---|
| ✓ Allow | Approve this one operation. |
| ✓ Approve Similar | Approve all future operations of the same type for the session. |
| ✗ Deny | Reject; Copilot adjusts. |

Operation types include shell commands, file I/O, MCP tool calls, URL fetches, memory access, and hook invocations.

## User Input Dialog

If Copilot asks a question mid-task, a dialog appears with the question and any predefined choices. Pick one or type a custom answer, then press Submit or Enter.

## Keyboard Shortcuts

| Shortcut | Action |
|---|---|
| Ctrl+Enter | Send prompt |
| Enter (in input dialog) | Submit answer |
| Delete (in Past Sessions) | Delete selected sessions |
| Esc | Close dialogs / exit fullscreen view |
| Alt+S / Alt+R / Alt+T / Alt+H | Open Session / References / Tools / Help menu |

Menu items with an underlined letter can be triggered with Alt+letter once their parent menu is open.

## Tips

- Open a folder and ask "What does this project do?" to get oriented.
- Use Plan mode for big tasks so you can review the plan before Copilot acts.
- Summarize before switching topics (Session ▸ 📝 Summarize).
- Watch the context meter. Amber at 60%, auto-refresh offer at 85%.
- Resume earlier work via Session ▸ 📋 Past Sessions...
- Switch to the Raw tab if a rendered block is not displaying as expected.
- For large Mermaid charts, hover and click Full or use the wheel to zoom.

---
If you enjoy this tool, please consider:

<a href="https://www.buymeacoffee.com/mighty_studios" target="_blank">
  <img src="https://cdn.buymeacoffee.com/buttons/default-yellow.png" alt="Buy Me A Coffee" height="41" width="174">
</a>

<small>(The joy I get from a free latte is incredible)</small> 

