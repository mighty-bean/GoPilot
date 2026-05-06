using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoPilot;

/// <summary>
/// Describes a persisted Copilot session so it can be resumed with the
/// correct workspace, model, mode, and UI settings.
/// </summary>
internal sealed class SessionMetadataEntry
{
	[JsonPropertyName("sessionId")]
	public string SessionId { get; set; } = "";

	[JsonPropertyName("workspaceFolder")]
	public string WorkspaceFolder { get; set; } = "";

	[JsonPropertyName("model")]
	public string Model { get; set; } = "";

	[JsonPropertyName("mode")]
	public string Mode { get; set; } = "Standard";

	[JsonPropertyName("fleet")]
	public bool Fleet { get; set; }

	[JsonPropertyName("autoApprove")]
	public bool AutoApprove { get; set; }

	[JsonPropertyName("createdAt")]
	public DateTime CreatedAt { get; set; }

	/// <summary>
	/// Brief human-readable description of the session, typically the first
	/// user prompt truncated to a sentence or two. Shown in the Past Sessions
	/// list so the user can recognise sessions at a glance.
	/// </summary>
	[JsonPropertyName("description")]
	public string Description { get; set; } = "";
}

/// <summary>
/// Persists session metadata to <c>gopilot-sessions.json</c> beside the
/// executable.  Thread-safe: all mutating operations serialise through a lock.
/// </summary>
internal sealed class SessionMetadataStore
{
	private static readonly string StorePath =
		Path.Combine(Application.StartupPath, "gopilot-sessions.json");

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true,
	};

	private readonly object _lock = new();

	/// <summary>Loads all stored session metadata entries.</summary>
	public List<SessionMetadataEntry> Load()
	{
		lock (_lock)
		{
			if (!File.Exists(StorePath))
				return new List<SessionMetadataEntry>();

			try
			{
				var json = File.ReadAllText(StorePath);
				return JsonSerializer.Deserialize<List<SessionMetadataEntry>>(json, JsonOptions)
					?? new List<SessionMetadataEntry>();
			}
			catch
			{
				return new List<SessionMetadataEntry>();
			}
		}
	}

	/// <summary>Adds or updates an entry and persists to disk.</summary>
	public void Save(SessionMetadataEntry entry)
	{
		lock (_lock)
		{
			var entries = Load();
			var idx = entries.FindIndex(
				e => string.Equals(e.SessionId, entry.SessionId, StringComparison.Ordinal));
			if (idx >= 0)
				entries[idx] = entry;
			else
				entries.Add(entry);
			Persist(entries);
		}
	}

	/// <summary>Removes a session by ID and persists to disk.</summary>
	public void Remove(string sessionId)
	{
		lock (_lock)
		{
			var entries = Load();
			entries.RemoveAll(
				e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal));
			Persist(entries);
		}
	}

	/// <summary>
	/// Retains only entries whose session IDs exist in <paramref name="liveIds"/>.
	/// Call after <c>ListSessionsAsync</c> to prune stale metadata.
	/// </summary>
	public void Prune(IReadOnlySet<string> liveIds)
	{
		lock (_lock)
		{
			var entries = Load();
			var before = entries.Count;
			entries.RemoveAll(e => !liveIds.Contains(e.SessionId));
			if (entries.Count != before)
				Persist(entries);
		}
	}

	/// <summary>Finds a session by ID. Returns null if not found.</summary>
	public SessionMetadataEntry? Find(string sessionId)
	{
		return Load().Find(
			e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal));
	}

	private void Persist(List<SessionMetadataEntry> entries)
	{
		var json = JsonSerializer.Serialize(entries, JsonOptions);
		File.WriteAllText(StorePath, json);
	}
}
