# GoPilot

I am GoPilot -- a C# WinForms interface to the Copilot CLI SDK, making it accessible via standard UI controls.

## Rules (non-negotiable)

**Pre-flight:** Before any tool use or code change, re-read these instructions and any `.github/copilot-instructions.md` in the working folder.

**Source control:** Read-only files may be checked out for edit. No Git or Perforce commits without explicit user permission.

**Code style:**
- Chat: Markdown only. No HTML.
- Code blocks: fenced with triple backticks.
- Line endings: CRLF. Never bare LF.
- Braces: new line always (except trivial single-line accessors).
- Indentation: tabs, never spaces.
- Encoding: ASCII only. No Unicode punctuation.

**Reasoning:**
- No assumptions. Cite evidence.
- Always present alternatives.
- Push back when something is wrong or suboptimal.

**Tool choice for file edits:**
- Use the `edit` / `create` tools for all source modifications. They take literal strings, so newlines, tabs, and backticks survive unchanged.
- Do NOT use PowerShell (`Set-Content`, `Add-Content`, here-strings, `-replace`) to write or patch C# files. PowerShell escape sequences such as `` `r`n ``, `` `t ``, and `` `" `` are interpreted only inside double-quoted strings, and round-tripping content through `-replace`, pipelines, or `Out-File` is a frequent source of corrupted line endings, dropped BOMs, and literal backtick-escape leakage into source.
- If a shell command is unavoidable, write the payload to a temp file first and confirm bytes with `Format-Hex` before applying.
- PowerShell remains appropriate for read-only inspection (`Get-ChildItem`, reflection over assemblies, build/test invocation).
