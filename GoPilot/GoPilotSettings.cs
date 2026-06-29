using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GoPilot;

/// <summary>
/// Reads and writes gopilot.ini, stored adjacent to the GoPilot executable.
///
/// Supported format:
///   [SkillTree]
///   Folder=C:\path\to\folder1
///   Folder=C:\path\to\folder2
///
///   [SkillSources]
///   Url=https://github.com/owner/repo
///   Url=https://dev.azure.com/org/project/_git/repo
///
///   [Session]
///   Model=claude-opus-4.6
///   Mode=Standard
///   Effort=High
///   Fleet=false
///   AutoApprove=true
///
/// Legacy format (auto-migrated on first load, then rewritten as [SkillTree]):
///   [Org]
///   Folder=C:\path\to\org
/// </summary>
internal sealed class GoPilotSettings
{
	private static readonly string IniPath =
		Path.Combine(Application.StartupPath, "gopilot.ini");

	/// <summary>Filename of the curated default-source seed list shipped next to the
	/// executable. Read once on first run when [SkillSources] is missing from
	/// gopilot.ini, then never re-read after the first Save() rewrites the file.</summary>
	private const string DefaultSourcesFile = "gopilot-sources.default.txt";

	/// <summary>
	/// Ordered list of Skill Tree folders.  Each folder is searched for a <c>skills/</c>
	/// subdirectory and an <c>agents/</c> subdirectory when a Copilot session starts.
	/// Later entries override earlier entries for agent-name collisions.
	/// </summary>
	public List<string> SkillTreeFolders { get; set; } = new();

	/// <summary>
	/// Ordered list of remote Skill Sources URLs (GitHub or Azure DevOps) the
	/// Skill Catalog browser will scan for downloadable skills/agents/prompts/
	/// instructions. Persisted under the <c>[SkillSources]</c> section.
	/// Duplicates are dropped case-insensitively at load time.
	/// </summary>
	public List<string> SkillSources { get; set; } = new();

	/// <summary>
	/// When true, every user prompt is reduced by <see cref="CavemanTransformer"/>
	/// before being sent to the model. Persisted under the <c>[Caveman]</c>
	/// section as <c>Enabled=true|false</c>.
	/// </summary>
	public bool CavemanMode { get; set; }

	/// <summary>
	/// Controls whether Reasoning and Tool group sections in the Rendered tab
	/// stay expanded after they finish (true) or auto-collapse to a smart
	/// summary (false, default). Persisted under <c>[UI]</c> as
	/// <c>DetailsDefaultOpen=true|false</c>.  Mid-session changes take effect
	/// for newly emitted sections only.
	/// </summary>
	public bool DetailsDefaultOpen { get; set; }

	/// <summary>
	/// When true, every user prompt is first sent to a local Ollama model that
	/// either answers directly (when confident) or rewrites the prompt to the
	/// fewest tokens before forwarding to the cloud. Persisted under
	/// <c>[LocalFilter]</c>.
	/// </summary>
	public bool LocalFilterEnabled { get; set; }

	/// <summary>Ollama HTTP endpoint. Persisted as <c>Endpoint=</c>.</summary>
	public string LocalFilterEndpoint { get; set; } = "http://localhost:11434";

	/// <summary>Codellama model id; empty means auto-select from detected VRAM. Persisted as <c>Model=</c>.</summary>
	public string LocalFilterModel { get; set; } = string.Empty;

	/// <summary>Confidence required for the local model to answer directly (0..1). Persisted as <c>Threshold=</c>.</summary>
	public double LocalFilterThreshold { get; set; } = 0.85;

	/// <summary>
	/// Last selected model id (e.g. <c>claude-opus-4.6</c>). Empty when no
	/// preference has been recorded yet; in that case
	/// <see cref="MainForm.PopulateModelsAsync"/> falls back to its
	/// Opus-then-Sonnet heuristic. Persisted under <c>[Session]</c> as
	/// <c>Model=...</c>.
	/// </summary>
	public string LastModel { get; set; } = string.Empty;

	/// <summary>
	/// Last selected mode display name (<c>Standard</c>, <c>Plan</c>, or
	/// <c>Autopilot</c>). Empty when no preference has been recorded yet; in
	/// that case the first mode in the combo (Standard) is selected.
	/// Persisted under <c>[Session]</c> as <c>Mode=...</c>.
	/// </summary>
	public string LastMode { get; set; } = string.Empty;

	/// <summary>
	/// Last selected reasoning-effort level for the active model (e.g.
	/// <c>High</c>). Empty when no preference has been recorded yet or when
	/// the active model does not advertise reasoning-effort support; in that
	/// case the highest-available level is selected. Persisted under
	/// <c>[Session]</c> as <c>Effort=...</c>.
	/// </summary>
	public string LastEffort { get; set; } = string.Empty;

	/// <summary>
	/// Last value of the Fleet toggle from the Options dropdown. Defaults to
	/// false to match the designer default for first-run users. Persisted
	/// under <c>[Session]</c> as <c>Fleet=true|false</c>.
	/// </summary>
	public bool LastFleet { get; set; }

	/// <summary>
	/// Last value of the Auto-approve toggle from the Options dropdown.
	/// Defaults to false so first-run users explicitly opt in to having
	/// Copilot run tools without confirmation. Persisted under
	/// <c>[Session]</c> as <c>AutoApprove=true|false</c>.
	/// </summary>
	public bool LastAutoApprove { get; set; }

	/// <summary>Loads settings from gopilot.ini; returns defaults if the file does not exist.</summary>
	public static GoPilotSettings Load()
	{
		var settings = new GoPilotSettings();
		if (!File.Exists(IniPath))
		{
			SeedDefaultSourcesFromFile(settings);
			return settings;
		}

		string? section = null;
		string? legacyOrgFolder = null;
		bool sawSkillTreeSection = false;
		bool sawSkillSourcesSection = false;
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var sourcesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		try
		{
			foreach (var rawLine in File.ReadLines(IniPath))
			{
				var line = rawLine.Trim();
				if (string.IsNullOrEmpty(line) || line.StartsWith(';') || line.StartsWith('#'))
					continue;

				if (line.StartsWith('[') && line.EndsWith(']'))
				{
					section = line[1..^1].Trim().ToLowerInvariant();
					if (section == "skilltree")    sawSkillTreeSection    = true;
					if (section == "skillsources") sawSkillSourcesSection = true;
					continue;
				}

				var eq = line.IndexOf('=');
				if (eq < 0) continue;

				var key = line[..eq].Trim().ToLowerInvariant();
				var val = line[(eq + 1)..].Trim();
				if (string.IsNullOrEmpty(val)) continue;

				if (section == "skilltree" && key == "folder")
				{
					if (seen.Add(val))
						settings.SkillTreeFolders.Add(val);
				}
				else if (section == "skillsources" && key == "url")
				{
					if (sourcesSeen.Add(val))
						settings.SkillSources.Add(val);
				}
				else if (section == "org" && key == "folder")
				{
					legacyOrgFolder = val;
				}
				else if (section == "caveman" && key == "enabled")
				{
					settings.CavemanMode = ParseBool(val);
				}
				else if (section == "ui" && key == "detailsdefaultopen")
				{
					settings.DetailsDefaultOpen = ParseBool(val);
				}
				else if (section == "localfilter")
				{
					switch (key)
					{
						case "enabled":   settings.LocalFilterEnabled  = ParseBool(val); break;
						case "endpoint":  settings.LocalFilterEndpoint = val;            break;
						case "model":     settings.LocalFilterModel    = val;            break;
						case "threshold":
							if (double.TryParse(val, System.Globalization.NumberStyles.Any,
									System.Globalization.CultureInfo.InvariantCulture, out var th))
								settings.LocalFilterThreshold = th;
							break;
					}
				}
				else if (section == "session")
				{
					switch (key)
					{
						case "model":       settings.LastModel       = val;             break;
						case "mode":        settings.LastMode        = val;             break;
						case "effort":      settings.LastEffort      = val;             break;
						case "fleet":       settings.LastFleet       = ParseBool(val);  break;
						case "autoapprove": settings.LastAutoApprove = ParseBool(val);  break;
					}
				}
			}
		}
		catch { /* best-effort; return defaults on any read error */ }

		// Migration: if no [SkillTree] section was present but a legacy [Org] Folder
		// value exists, seed the new list with that single value.  The next Save()
		// will rewrite the file in the new format and drop the [Org] section.
		if (!sawSkillTreeSection && !string.IsNullOrEmpty(legacyOrgFolder))
			settings.SkillTreeFolders.Add(legacyOrgFolder);

		// First-run seed for the Skill Sources list. Only honoured when the
		// section is absent from gopilot.ini -- once present (even as an empty
		// list with header), the user is in control and we never re-seed.
		if (!sawSkillSourcesSection)
			SeedDefaultSourcesFromFile(settings);

		return settings;
	}

	/// <summary>Writes settings to gopilot.ini, overwriting any previous content.</summary>
	public void Save()
	{
		var sb = new StringBuilder();
		sb.Append("[SkillTree]\r\n");

		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var folder in SkillTreeFolders)
		{
			if (string.IsNullOrWhiteSpace(folder)) continue;
			var trimmed = folder.Trim();
			if (!seen.Add(trimmed)) continue;
			sb.Append($"Folder={trimmed}\r\n");
		}

		sb.Append("\r\n[SkillSources]\r\n");
		var sourcesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var url in SkillSources)
		{
			if (string.IsNullOrWhiteSpace(url)) continue;
			var trimmed = url.Trim();
			if (!sourcesSeen.Add(trimmed)) continue;
			sb.Append($"Url={trimmed}\r\n");
		}

		sb.Append("\r\n[Caveman]\r\n");
		sb.Append($"Enabled={(CavemanMode ? "true" : "false")}\r\n");

		sb.Append("\r\n[UI]\r\n");
		sb.Append($"DetailsDefaultOpen={(DetailsDefaultOpen ? "true" : "false")}\r\n");

		sb.Append("\r\n[LocalFilter]\r\n");
		sb.Append($"Enabled={(LocalFilterEnabled ? "true" : "false")}\r\n");
		sb.Append($"Endpoint={LocalFilterEndpoint}\r\n");
		sb.Append($"Model={LocalFilterModel}\r\n");
		sb.Append($"Threshold={LocalFilterThreshold.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}\r\n");

		sb.Append("\r\n[Session]\r\n");
		sb.Append($"Model={LastModel}\r\n");
		sb.Append($"Mode={LastMode}\r\n");
		sb.Append($"Effort={LastEffort}\r\n");
		sb.Append($"Fleet={(LastFleet ? "true" : "false")}\r\n");
		sb.Append($"AutoApprove={(LastAutoApprove ? "true" : "false")}\r\n");

		File.WriteAllText(IniPath, sb.ToString(), Encoding.ASCII);
	}

	/// <summary>
	/// Reads <see cref="DefaultSourcesFile"/> from the executable directory and
	/// appends each non-comment, non-blank URL to <paramref name="settings"/>.
	/// Lines starting with '#' or ';' are treated as comments. Errors are
	/// swallowed -- the seed file is best-effort and never blocks startup.
	/// </summary>
	private static void SeedDefaultSourcesFromFile(GoPilotSettings settings)
	{
		var path = Path.Combine(Application.StartupPath, DefaultSourcesFile);
		if (!File.Exists(path)) return;

		var seen = new HashSet<string>(settings.SkillSources, StringComparer.OrdinalIgnoreCase);
		try
		{
			foreach (var rawLine in File.ReadLines(path))
			{
				var line = rawLine.Trim();
				if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith(';'))
					continue;
				if (seen.Add(line))
					settings.SkillSources.Add(line);
			}
		}
		catch { /* best-effort */ }
	}

	private static bool ParseBool(string val) =>
		val.Equals("true", System.StringComparison.OrdinalIgnoreCase)
		|| val == "1"
		|| val.Equals("yes", System.StringComparison.OrdinalIgnoreCase)
		|| val.Equals("on",  System.StringComparison.OrdinalIgnoreCase);
}
