using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GoPilot;

/// <summary>
/// Reads and writes <c>~/.copilot/permissions-config.json</c>, the file the
/// Copilot CLI uses to record per-location tool approvals.  GoPilot checks
/// this file at connection time so both tools share the same notion of a
/// trusted workspace folder.
/// </summary>
internal static class CopilotPermissionsConfig
{
	private static readonly string FilePath = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
		".copilot",
		"permissions-config.json");

	/// <summary>
	/// Returns true if <paramref name="folder"/> already appears as a key in
	/// the CLI's <c>permissions-config.json</c> locations map -- meaning the
	/// user has previously trusted or interacted with that path via either
	/// GoPilot or the Copilot CLI directly.
	/// </summary>
	public static bool IsTrusted(string folder)
	{
		var key = Normalize(folder);
		try
		{
			if (!File.Exists(FilePath)) return false;
			using var stream = File.OpenRead(FilePath);
			using var doc    = JsonDocument.Parse(stream);
			if (!doc.RootElement.TryGetProperty("locations", out var locations))
				return false;
			foreach (var prop in locations.EnumerateObject())
			{
				if (Normalize(prop.Name).Equals(key, StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// Adds <paramref name="folder"/> to <c>permissions-config.json</c> with an
	/// empty <c>tool_approvals</c> array, marking it as a trusted location.
	/// Creates the file and parent directory if they do not exist.  This
	/// mirrors what the Copilot CLI writes when a user first trusts a folder,
	/// so subsequent CLI sessions in the same folder will not re-prompt.
	/// Errors are swallowed -- failure here is non-fatal.
	/// </summary>
	public static void Trust(string folder)
	{
		var key = Normalize(folder);
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

			JsonObject root;
			if (File.Exists(FilePath))
			{
				var json = File.ReadAllText(FilePath);
				root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
			}
			else
			{
				root = new JsonObject();
			}

			if (root["locations"] is not JsonObject locations)
			{
				locations = new JsonObject();
				root["locations"] = locations;
			}

			// Skip if already present (case-insensitive on Windows).
			foreach (var existing in locations)
			{
				if (Normalize(existing.Key).Equals(key, StringComparison.OrdinalIgnoreCase))
					return;
			}

			locations[folder] = new JsonObject
			{
				["tool_approvals"] = new JsonArray(),
			};

			var options = new JsonSerializerOptions
			{
				WriteIndented = true,
				// Preserve non-ASCII characters in path strings as-is rather
				// than converting them to \uXXXX escape sequences.
				Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
			};
			File.WriteAllText(FilePath, root.ToJsonString(options), System.Text.Encoding.UTF8);
		}
		catch { /* best-effort */ }
	}

	/// <summary>
	/// Returns a canonical, trailing-separator-free absolute path suitable
	/// for case-insensitive comparison on Windows.
	/// </summary>
	private static string Normalize(string path) =>
		Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
