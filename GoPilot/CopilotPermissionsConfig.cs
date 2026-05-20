using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GoPilot;

// ── Data model ────────────────────────────────────────────────────────────────

/// <summary>
/// In-memory representation of <c>~/.copilot/permissions-config.json</c>.
/// </summary>
internal sealed class PermissionsData
{
	/// <summary>
	/// All trusted workspace folders in the order they appear in the file.
	/// </summary>
	public List<LocationEntry> Locations { get; } = new();
}

/// <summary>
/// One entry under the <c>"locations"</c> map in permissions-config.json.
/// </summary>
internal sealed class LocationEntry
{
	/// <summary>The folder path exactly as stored as a JSON key.</summary>
	public string FolderPath { get; set; } = "";

	/// <summary>
	/// Permission kinds the user has pre-approved for this folder (from
	/// <see cref="CopilotPermissionsConfig.KnownKinds"/>).  Checked items
	/// skip the per-operation prompt inside the Copilot CLI.
	/// </summary>
	public HashSet<string> ApprovedKinds { get; } =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Raw <c>tool_approvals</c> array items preserved for round-trip safety.
	/// Includes known-kind items (which may carry extra fields) and
	/// unknown-kind items (always re-emitted unchanged on save).
	/// </summary>
	internal List<JsonNode> RawApprovals { get; } = new();
}

// ── Static helper ─────────────────────────────────────────────────────────────

/// <summary>
/// Reads and writes <c>~/.copilot/permissions-config.json</c>, the file the
/// Copilot CLI uses to record per-location tool approvals.  GoPilot checks
/// this file at connection time so both tools share the same notion of a
/// trusted workspace folder.
/// </summary>
internal static class CopilotPermissionsConfig
{
	internal static readonly string FilePath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
		".copilot",
		"permissions-config.json");

	private static readonly JsonSerializerOptions WriteOptions = new()
	{
		WriteIndented = true,
		// Keep non-ASCII path characters as-is rather than \uXXXX sequences.
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	/// <summary>
	/// All permission kind strings GoPilot presents in the UI, in display
	/// order.  The <c>Kind</c> strings match what the CLI writes into
	/// <c>tool_approvals[].kind</c>.
	/// </summary>
	public static readonly IReadOnlyList<(string Kind, string Label)> KnownKinds =
	[
		("write",        "Write / edit files"),
		("read",         "Read files"),
		("shell",        "Execute shell commands"),
		("mcp",          "Call MCP tools"),
		("mcp_sampling", "MCP sampling"),
		("memory",       "Access memory"),
		("custom_tool",  "Custom tools"),
		("url",          "Fetch URLs"),
		("hook",         "Invoke hooks"),
	];

	private static readonly HashSet<string> KnownKindSet =
		new(KnownKinds.Select(k => k.Kind), StringComparer.OrdinalIgnoreCase);

	// ── Load / Save ───────────────────────────────────────────────────────────

	/// <summary>
	/// Loads the full permissions config from disk.  Returns an empty
	/// <see cref="PermissionsData"/> when the file does not exist or cannot
	/// be read.
	/// </summary>
	public static PermissionsData LoadAll()
	{
		var data = new PermissionsData();
		try
		{
			if (!File.Exists(FilePath)) return data;
			var json = File.ReadAllText(FilePath);
			var root = JsonNode.Parse(json)?.AsObject();
			if (root == null) return data;
			if (root["locations"] is not JsonObject locations) return data;

			foreach (var prop in locations)
			{
				var entry = new LocationEntry { FolderPath = prop.Key };

				if (prop.Value?.AsObject()?["tool_approvals"] is JsonArray approvals)
				{
					foreach (var item in approvals)
					{
						if (item == null) continue;
						entry.RawApprovals.Add(item.DeepClone());
						var kind = item["kind"]?.GetValue<string>();
						if (!string.IsNullOrEmpty(kind) && KnownKindSet.Contains(kind))
							entry.ApprovedKinds.Add(kind);
					}
				}

				data.Locations.Add(entry);
			}
		}
		catch { /* return whatever was parsed */ }
		return data;
	}

	/// <summary>
	/// Writes <paramref name="data"/> back to disk.  For each location:
	/// <list type="bullet">
	/// <item>Checked known kinds: re-uses the original JSON node if one
	/// exists (preserving any extra fields), otherwise emits
	/// <c>{"kind":"..."}</c>.</item>
	/// <item>Unchecked known kinds: omitted.</item>
	/// <item>Unknown kinds present in the original file: always preserved
	/// unchanged so we never silently drop CLI-managed data.</item>
	/// </list>
	/// </summary>
	public static void SaveAll(PermissionsData data)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

			var locations = new JsonObject();
			foreach (var entry in data.Locations)
			{
				var approvals = new JsonArray();

				// Known checked kinds first (in KnownKinds display order).
				foreach (var (kind, _) in KnownKinds)
				{
					if (!entry.ApprovedKinds.Contains(kind)) continue;
					var existing = entry.RawApprovals.FirstOrDefault(n =>
						string.Equals(n["kind"]?.GetValue<string>(), kind,
							StringComparison.OrdinalIgnoreCase));
					approvals.Add(existing != null
						? existing.DeepClone()
						: new JsonObject { ["kind"] = kind });
				}

				// Unknown kinds: always re-emit unchanged.
				foreach (var raw in entry.RawApprovals)
				{
					var kind = raw["kind"]?.GetValue<string>();
					if (!string.IsNullOrEmpty(kind) && !KnownKindSet.Contains(kind))
						approvals.Add(raw.DeepClone());
				}

				locations[entry.FolderPath] = new JsonObject
				{
					["tool_approvals"] = approvals,
				};
			}

			var root = new JsonObject { ["locations"] = locations };
			File.WriteAllText(FilePath, root.ToJsonString(WriteOptions),
				System.Text.Encoding.UTF8);
		}
		catch { /* best-effort */ }
	}

	// ── Convenience helpers (used by the trust-check gate) ───────────────────

	/// <summary>
	/// Returns true if <paramref name="folder"/> already appears as a key in
	/// the CLI's permissions-config.json locations map.
	/// </summary>
	public static bool IsTrusted(string folder)
	{
		var key = Normalize(folder);
		return LoadAll().Locations.Any(e =>
			Normalize(e.FolderPath).Equals(key, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Adds <paramref name="folder"/> to permissions-config.json with an empty
	/// tool_approvals list, marking it as trusted.  No-op if already present.
	/// </summary>
	public static void Trust(string folder)
	{
		if (IsTrusted(folder)) return;
		var data = LoadAll();
		data.Locations.Add(new LocationEntry { FolderPath = folder });
		SaveAll(data);
	}

	// ── Shared utility ────────────────────────────────────────────────────────

	/// <summary>
	/// Canonical, trailing-separator-free absolute path for case-insensitive
	/// comparison on Windows.
	/// </summary>
	internal static string Normalize(string path) =>
		Path.GetFullPath(path)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
