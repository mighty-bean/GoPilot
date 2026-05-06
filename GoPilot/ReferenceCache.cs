using System.Collections.Generic;

namespace GoPilot;

/// <summary>
/// Lightweight summary of a custom agent definition surfaced to the UI for the
/// "List Agents" dialog. Built once from agent .md files when sessions/tier
/// folders change so the dialog can be opened without re-parsing.
/// </summary>
public sealed class AgentInfo
{
	public required string  Name        { get; init; }
	public          string? DisplayName { get; init; }
	public          string? Description { get; init; }
	public required string  FilePath    { get; init; }
	public required string  Tier        { get; init; }
}

/// <summary>
/// Lightweight summary of a skill (SKILL.md) surfaced to the UI for the
/// "List Skills" dialog. Triggers are extracted from the "## When to Use This Skill"
/// bullets when present.
/// </summary>
public sealed class SkillInfo
{
	public required string  Name        { get; init; }
	public          string? Description { get; init; }
	public required IReadOnlyList<string> Triggers { get; init; }
	public required string  FolderPath  { get; init; }
	public required string  Tier        { get; init; }
}

/// <summary>
/// Lightweight summary of a prompt template (a *.md file under a tier
/// folder's prompts/ subdirectory) surfaced to the UI for the "List Prompts"
/// dialog. Prompts are user-authored prompt bodies the model never sees a
/// special token for: when the user picks one from the dialog, GoPilot
/// attaches the underlying file to the next outgoing message exactly the
/// same way it would handle a manually picked file -- chip in the
/// attachments strip plus a relative-path @reference token at the caret.
/// </summary>
public sealed class PromptInfo
{
	public required string  Name        { get; init; }
	public          string? Description { get; init; }
	public required string  FilePath    { get; init; }
	public required string  Tier        { get; init; }
}
