using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace GoPilot;

/// <summary>
/// Checks NuGet for newer versions of the GitHub.Copilot.SDK package
/// and npm for newer versions of the Copilot CLI binary.
/// </summary>
internal static class UpdateChecker
{
	private const string NuGetUrl =
		"https://api.nuget.org/v3-flatcontainer/github.copilot.sdk/index.json";

	// The GitHub Releases page is the source of truth for the Copilot CLI version.
	// The npm @github/copilot-* packages have been observed to publish ahead of the
	// official release (e.g. npm at 1.0.38 while Releases is still at v1.0.37), which
	// produced spurious "update available" notifications. /releases/latest excludes
	// drafts and pre-releases automatically, matching what users see on the website.
	private const string GitHubLatestReleaseUrl =
		"https://api.github.com/repos/github/copilot-cli/releases/latest";

	private static readonly HttpClient _http = new()
	{
		Timeout = TimeSpan.FromSeconds(15),
	};

	static UpdateChecker()
	{
		_http.DefaultRequestHeaders.UserAgent.ParseAdd("GoPilot/1.0");
	}

	/// <summary>Returns the informational version of the loaded GitHub.Copilot.SDK assembly.</summary>
	public static string GetCurrentSdkVersion()
	{
		var asm = typeof(GitHub.Copilot.SDK.CopilotClient).Assembly;
		var infoVer = asm
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
			?.InformationalVersion;

		if (string.IsNullOrEmpty(infoVer))
			return asm.GetName().Version?.ToString(3) ?? "unknown";

		// Strip build metadata hash appended by SourceLink (e.g. "+abc1234")
		var plus = infoVer.IndexOf('+');
		return plus >= 0 ? infoVer[..plus] : infoVer;
	}

	/// <summary>
	/// Fetches all published versions of GitHub.Copilot.SDK from NuGet and returns
	/// the latest one. Returns null if the check fails or the network is unavailable.
	/// </summary>
	public static async Task<string?> GetLatestSdkVersionAsync()
	{
		try
		{
			var index = await _http
				.GetFromJsonAsync<NuGetIndex>(NuGetUrl)
				.ConfigureAwait(false);

			return index?.Versions?.LastOrDefault();
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Returns true when <paramref name="candidate"/> is strictly newer than
	/// <paramref name="current"/> using a SemVer-aware comparison.
	/// </summary>
	public static bool IsNewer(string current, string candidate)
		=> Compare(candidate, current) > 0;

	// ── Helpers ──────────────────────────────────────────────────────────────

	/// <summary>SemVer-aware comparison. Returns negative / zero / positive.</summary>
	private static int Compare(string a, string b)
	{
		var (aMaj, aMin, aPatch, aPre) = Parse(a);
		var (bMaj, bMin, bPatch, bPre) = Parse(b);

		var cmp = aMaj.CompareTo(bMaj);
		if (cmp != 0) return cmp;

		cmp = aMin.CompareTo(bMin);
		if (cmp != 0) return cmp;

		cmp = aPatch.CompareTo(bPatch);
		if (cmp != 0) return cmp;

		// Stable release outranks any pre-release of the same numeric version.
		if (string.IsNullOrEmpty(aPre) && !string.IsNullOrEmpty(bPre)) return  1;
		if (!string.IsNullOrEmpty(aPre) && string.IsNullOrEmpty(bPre)) return -1;

		return string.Compare(aPre, bPre, StringComparison.OrdinalIgnoreCase);
	}

	private static (int major, int minor, int patch, string pre) Parse(string v)
	{
		var pre = "";
		var dash = v.IndexOf('-');
		if (dash >= 0)
		{
			pre = v[(dash + 1)..];
			v   = v[..dash];
		}

		var parts = v.Split('.');
		int.TryParse(parts.Length > 0 ? parts[0] : "0", out var major);
		int.TryParse(parts.Length > 1 ? parts[1] : "0", out var minor);
		int.TryParse(parts.Length > 2 ? parts[2] : "0", out var patch);
		return (major, minor, patch, pre);
	}

	private sealed class NuGetIndex
	{
		[JsonPropertyName("versions")]
		public List<string>? Versions { get; set; }
	}

	// ── CLI version check ─────────────────────────────────────────────────────

	/// <summary>
	/// Fetches the latest Copilot CLI release tag from the GitHub Releases API
	/// (<c>github/copilot-cli</c>). The leading <c>v</c> in the tag (e.g. <c>v1.0.37</c>)
	/// is stripped so the result is directly comparable to the running CLI's version
	/// string. Returns null if the check fails or the network is unavailable.
	/// </summary>
	public static async Task<string?> GetLatestCliVersionAsync()
	{
		try
		{
			var release = await _http
				.GetFromJsonAsync<GitHubRelease>(GitHubLatestReleaseUrl)
				.ConfigureAwait(false);

			var tag = release?.TagName;
			if (string.IsNullOrEmpty(tag))
				return null;

			return tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
		}
		catch
		{
			return null;
		}
	}

	private sealed class GitHubRelease
	{
		[JsonPropertyName("tag_name")]
		public string? TagName { get; set; }
	}
}
