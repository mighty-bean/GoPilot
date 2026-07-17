using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoPilot;

/// <summary>
/// SDK-free data model for one user-configured MCP (Model Context Protocol)
/// server. GoPilot persists a list of these in gopilot.ini -- one base64-encoded
/// JSON token per <c>Server=</c> line under <c>[McpServers]</c> -- and converts
/// the enabled entries into the SDK's <c>McpStdioServerConfig</c> /
/// <c>McpHttpServerConfig</c> at session-creation time (see
/// <see cref="CopilotService"/>). Keeping this type free of any SDK reference
/// lets the settings layer and the editor dialog stay independent of the SDK.
/// </summary>
public sealed class McpServerEntry
{
	/// <summary>Transport identifier for a locally launched stdio process server.</summary>
	public const string TransportStdio = "stdio";

	/// <summary>Transport identifier for a streamable-HTTP server.</summary>
	public const string TransportHttp = "http";

	/// <summary>Unique server name; becomes the key the runtime shows for its tools.</summary>
	public string Name { get; set; } = "";

	/// <summary>When false the server is kept in the list but not sent to any session.</summary>
	public bool Enabled { get; set; } = true;

	/// <summary>Either <see cref="TransportStdio"/> or <see cref="TransportHttp"/>.</summary>
	public string Transport { get; set; } = TransportStdio;

	// -- stdio transport -------------------------------------------------------

	/// <summary>Executable to launch (stdio transport only).</summary>
	public string Command { get; set; } = "";

	/// <summary>Command-line arguments, one per element (stdio transport only).</summary>
	public List<string> Args { get; set; } = new();

	/// <summary>Working directory for the launched process (stdio transport only). Optional.</summary>
	public string WorkingDirectory { get; set; } = "";

	/// <summary>Extra environment variables for the process (stdio transport only).</summary>
	public Dictionary<string, string> Env { get; set; } = new();

	// -- http transport --------------------------------------------------------

	/// <summary>Endpoint URL (http transport only).</summary>
	public string Url { get; set; } = "";

	/// <summary>Extra request headers (http transport only).</summary>
	public Dictionary<string, string> Headers { get; set; } = new();

	/// <summary>
	/// For discovered entries, the full path of the <c>.mcp.json</c> file this
	/// server came from. Empty for manually configured entries. Never persisted
	/// (it is re-derived from disk on each scan).
	/// </summary>
	[JsonIgnore]
	public string SourcePath { get; set; } = "";

	/// <summary>True when this entry was discovered from a <c>.mcp.json</c> file.</summary>
	[JsonIgnore]
	public bool IsDiscovered => !string.IsNullOrEmpty(SourcePath);

	/// <summary>True when this entry uses the HTTP transport.</summary>
	[JsonIgnore]
	public bool IsHttp =>
		string.Equals(Transport, TransportHttp, StringComparison.OrdinalIgnoreCase);

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>
	/// Encodes this entry as a single storage token: base64 of its UTF-8 JSON.
	/// base64 keeps arbitrary values (paths, args, header values) safe inside the
	/// line-based gopilot.ini format without any delimiter escaping.
	/// </summary>
	public string Encode()
	{
		var json = JsonSerializer.Serialize(this, JsonOptions);
		return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
	}

	/// <summary>
	/// Decodes a token produced by <see cref="Encode"/>. Returns null when the
	/// token is not valid base64/JSON or has no name, so a single corrupt line
	/// can never break settings loading.
	/// </summary>
	public static McpServerEntry? TryDecode(string token)
	{
		if (string.IsNullOrWhiteSpace(token)) return null;
		try
		{
			var json  = Encoding.UTF8.GetString(Convert.FromBase64String(token.Trim()));
			var entry = JsonSerializer.Deserialize<McpServerEntry>(json, JsonOptions);
			if (entry == null || string.IsNullOrWhiteSpace(entry.Name)) return null;

			entry.Args      ??= new();
			entry.Env       ??= new();
			entry.Headers   ??= new();
			entry.Name        = entry.Name.Trim();
			entry.Transport   = entry.IsHttp ? TransportHttp : TransportStdio;
			return entry;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>Returns a deep copy so dialog edits do not mutate the live list until OK.</summary>
	public McpServerEntry Clone() =>
		new McpServerEntry
		{
			Name             = Name,
			Enabled          = Enabled,
			Transport        = Transport,
			Command          = Command,
			Args             = new List<string>(Args),
			WorkingDirectory = WorkingDirectory,
			Env              = new Dictionary<string, string>(Env),
			Url              = Url,
			Headers          = new Dictionary<string, string>(Headers),
		};

	/// <summary>
	/// Parses a standard MCP client config document (the <c>.mcp.json</c> format
	/// written by tools such as Unreal Engine, Claude Code, Cursor, and VS Code)
	/// into a list of entries. Accepts either a top-level <c>mcpServers</c> object
	/// (Claude/Cursor/UE) or a <c>servers</c> object (VS Code). Unknown fields are
	/// ignored and a malformed document yields an empty list, so callers can feed
	/// it untrusted files safely.
	/// </summary>
	public static List<McpServerEntry> FromMcpJson(string json)
	{
		var result = new List<McpServerEntry>();
		if (string.IsNullOrWhiteSpace(json)) return result;

		JsonDocument doc;
		try { doc = JsonDocument.Parse(json); }
		catch { return result; }

		using (doc)
		{
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object) return result;

			if (!root.TryGetProperty("mcpServers", out var map) &&
				!root.TryGetProperty("servers", out map))
				return result;
			if (map.ValueKind != JsonValueKind.Object) return result;

			foreach (var prop in map.EnumerateObject())
			{
				var entry = ParseOne(prop.Name, prop.Value);
				if (entry != null) result.Add(entry);
			}
		}
		return result;
	}

	private static McpServerEntry? ParseOne(string name, JsonElement el)
	{
		if (string.IsNullOrWhiteSpace(name) || el.ValueKind != JsonValueKind.Object)
			return null;

		var type = el.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
			? t.GetString()
			: null;
		var hasUrl = el.TryGetProperty("url", out var urlEl)
			&& urlEl.ValueKind == JsonValueKind.String
			&& !string.IsNullOrWhiteSpace(urlEl.GetString());
		var isHttp = hasUrl
			|| string.Equals(type, "http", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(type, "sse", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(type, "streamable-http", StringComparison.OrdinalIgnoreCase);

		var entry = new McpServerEntry { Name = name.Trim(), Enabled = true };
		if (isHttp)
		{
			if (!hasUrl) return null;
			entry.Transport = TransportHttp;
			entry.Url       = urlEl.GetString()!.Trim();
			entry.Headers   = ReadStringMap(el, "headers");
			return entry;
		}

		// stdio: command may be a string, or an array whose first element is the
		// executable and whose remainder are leading arguments.
		if (!el.TryGetProperty("command", out var cmdEl)) return null;

		entry.Transport = TransportStdio;
		entry.Args      = new List<string>();
		if (cmdEl.ValueKind == JsonValueKind.String)
		{
			entry.Command = cmdEl.GetString()!.Trim();
		}
		else if (cmdEl.ValueKind == JsonValueKind.Array)
		{
			foreach (var item in cmdEl.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.String) continue;
				var s = item.GetString() ?? "";
				if (entry.Command.Length == 0) entry.Command = s.Trim();
				else entry.Args.Add(s);
			}
		}
		if (string.IsNullOrWhiteSpace(entry.Command)) return null;

		entry.Args.AddRange(ReadStringArray(el, "args"));
		entry.Env = ReadStringMap(el, "env");
		if (el.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String)
			entry.WorkingDirectory = cwdEl.GetString()!.Trim();
		else if (el.TryGetProperty("workingDirectory", out var wdEl) && wdEl.ValueKind == JsonValueKind.String)
			entry.WorkingDirectory = wdEl.GetString()!.Trim();

		return entry;
	}

	private static List<string> ReadStringArray(JsonElement parent, string name)
	{
		var list = new List<string>();
		if (parent.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
		{
			foreach (var item in arr.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.String)
					list.Add(item.GetString() ?? "");
			}
		}
		return list;
	}

	private static Dictionary<string, string> ReadStringMap(JsonElement parent, string name)
	{
		var dict = new Dictionary<string, string>(StringComparer.Ordinal);
		if (parent.TryGetProperty(name, out var obj) && obj.ValueKind == JsonValueKind.Object)
		{
			foreach (var p in obj.EnumerateObject())
			{
				dict[p.Name] = p.Value.ValueKind == JsonValueKind.String
					? (p.Value.GetString() ?? "")
					: p.Value.ToString();
			}
		}
		return dict;
	}
}
