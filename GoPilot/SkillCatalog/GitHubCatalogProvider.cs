using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GoPilot.SkillCatalog;

/// <summary>
/// Anonymous-only GitHub catalog provider. Lists items via the recursive
/// <c>git/trees</c> endpoint and downloads file content via
/// <c>raw.githubusercontent.com</c>. Surfaces rate-limit headers in
/// <see cref="CatalogTree.RateLimit"/> so the browser dialog can show them
/// in its status bar. 404 from the metadata endpoint is mapped to
/// <see cref="CatalogProviderErrorKind.PrivateOrNotFound"/>; 403 with
/// <c>X-RateLimit-Remaining: 0</c> is mapped to
/// <see cref="CatalogProviderErrorKind.RateLimited"/>.
/// </summary>
internal sealed class GitHubCatalogProvider : ICatalogProvider
{
	private readonly HttpClient _http;

	public GitHubCatalogProvider(HttpClient http) => _http = http;

	public ProviderKind Kind => ProviderKind.GitHub;

	/// <summary>
	/// Parsed components of a GitHub source URL. <see cref="Ref"/> is null
	/// when the URL didn't include a /tree/<branch> segment, in which case
	/// the provider resolves the repo's default branch.
	/// </summary>
	private sealed record GitHubLocator(string Owner, string Repo, string? Ref, string Subpath);

	private static GitHubLocator ParseUrl(string url)
	{
		var uri = new Uri(url, UriKind.Absolute);
		var segments = uri.AbsolutePath
			.Split('/', StringSplitOptions.RemoveEmptyEntries);

		if (segments.Length < 2)
			throw new CatalogProviderException(CatalogProviderErrorKind.BadResponse,
				$"GitHub URL must contain owner and repo: {url}");

		var owner = segments[0];
		var repo  = segments[1];
		if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
			repo = repo[..^4];

		string? gitRef  = null;
		string  subpath = "";

		// /tree/<ref>[/path...] and /blob/<ref>/path... -- the latter is
		// rare for a "source" URL but harmless to honour.
		if (segments.Length >= 4 && (segments[2] == "tree" || segments[2] == "blob"))
		{
			gitRef = segments[3];
			if (segments.Length > 4)
				subpath = string.Join('/', segments[4..]);
		}

		return new GitHubLocator(owner, repo, gitRef, subpath);
	}

	public async Task<CatalogTree> ListAsync(CatalogSource source, string? cachedEtag, CancellationToken ct)
	{
		var loc = ParseUrl(source.Url);
		var gitRef = loc.Ref ?? await ResolveDefaultBranchAsync(loc.Owner, loc.Repo, ct).ConfigureAwait(false);

		// Issue the recursive-tree call. ETag goes on this request; raw downloads
		// are unauthenticated and don't participate in the cache.
		var treeUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(loc.Owner)}/{Uri.EscapeDataString(loc.Repo)}/git/trees/{Uri.EscapeDataString(gitRef)}?recursive=1";

		using var req = new HttpRequestMessage(HttpMethod.Get, treeUrl);
		if (!string.IsNullOrEmpty(cachedEtag))
			req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cachedEtag));

		using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
		var rate = ReadRateLimit(resp);

		if (resp.StatusCode == HttpStatusCode.NotModified)
			throw new CatalogTreeNotModifiedException();

		if (resp.StatusCode == HttpStatusCode.Forbidden && rate is { Remaining: 0 })
		{
			var resetText = rate.ResetUtc?.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture) ?? "later";
			throw new CatalogProviderException(CatalogProviderErrorKind.RateLimited,
				$"GitHub API rate limit exhausted. Resets at {resetText} local time. Cached results still available.");
		}

		if (resp.StatusCode == HttpStatusCode.NotFound)
			throw new CatalogProviderException(CatalogProviderErrorKind.PrivateOrNotFound,
				"Private or not found. GoPilot does not collect credentials; clone the repo manually if you need its contents.");

		if (!resp.IsSuccessStatusCode)
			throw new CatalogProviderException(CatalogProviderErrorKind.BadResponse,
				$"GitHub returned {(int)resp.StatusCode} {resp.ReasonPhrase} for {treeUrl}");

		var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		var entries = ParseTree(json, out var truncated, out var resolvedSha);

		// The truncated flag would mean we've hit the 100k entry / 7MB tree
		// response cap; for reasonable skill repos this should never happen,
		// but surfacing it as a soft warning beats silent under-listing.
		var etag = resp.Headers.ETag?.Tag;
		var tree = BuildCatalogTree(source, loc, gitRef, resolvedSha ?? gitRef, entries, truncated, rate, etag);
		return tree;
	}

	private async Task<string> ResolveDefaultBranchAsync(string owner, string repo, CancellationToken ct)
	{
		var url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}";
		using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
		var rate = ReadRateLimit(resp);

		if (resp.StatusCode == HttpStatusCode.NotFound)
			throw new CatalogProviderException(CatalogProviderErrorKind.PrivateOrNotFound,
				"Private or not found. GoPilot does not collect credentials; clone the repo manually if you need its contents.");

		if (resp.StatusCode == HttpStatusCode.Forbidden && rate is { Remaining: 0 })
		{
			var resetText = rate.ResetUtc?.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture) ?? "later";
			throw new CatalogProviderException(CatalogProviderErrorKind.RateLimited,
				$"GitHub API rate limit exhausted. Resets at {resetText} local time.");
		}

		if (!resp.IsSuccessStatusCode)
			throw new CatalogProviderException(CatalogProviderErrorKind.BadResponse,
				$"GitHub returned {(int)resp.StatusCode} {resp.ReasonPhrase} for {url}");

		var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		using var doc = JsonDocument.Parse(json);
		if (doc.RootElement.TryGetProperty("default_branch", out var br) && br.ValueKind == JsonValueKind.String)
			return br.GetString()!;

		throw new CatalogProviderException(CatalogProviderErrorKind.BadResponse,
			$"GitHub repo metadata did not include a default_branch: {url}");
	}

	public async Task<byte[]> FetchFileAsync(CatalogSource source, string resolvedRef, string repoRelPath, CancellationToken ct)
	{
		var loc = ParseUrl(source.Url);
		// Use raw.githubusercontent.com -- doesn't count against API quota.
		var url = $"https://raw.githubusercontent.com/{Uri.EscapeDataString(loc.Owner)}/{Uri.EscapeDataString(loc.Repo)}/{Uri.EscapeDataString(resolvedRef)}/{EscapePath(repoRelPath)}";
		using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);

		if (resp.StatusCode == HttpStatusCode.NotFound)
			throw new CatalogProviderException(CatalogProviderErrorKind.PrivateOrNotFound,
				$"File not found at {url}");

		if (!resp.IsSuccessStatusCode)
			throw new CatalogProviderException(CatalogProviderErrorKind.BadResponse,
				$"GitHub returned {(int)resp.StatusCode} {resp.ReasonPhrase} for {url}");

		return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
	}

	/// <summary>Forward-slash-preserving URL escape for repo-relative paths.</summary>
	private static string EscapePath(string repoRelPath) =>
		string.Join('/', repoRelPath.Split('/').Select(Uri.EscapeDataString));

	private sealed record TreeEntry(string Path, string Type, long Size, string? Sha);

	private static List<TreeEntry> ParseTree(string json, out bool truncated, out string? sha)
	{
		var entries = new List<TreeEntry>();
		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		sha = root.TryGetProperty("sha", out var shaProp) && shaProp.ValueKind == JsonValueKind.String
			? shaProp.GetString()
			: null;

		truncated = root.TryGetProperty("truncated", out var trunc)
			&& trunc.ValueKind == JsonValueKind.True;

		if (!root.TryGetProperty("tree", out var treeArr) || treeArr.ValueKind != JsonValueKind.Array)
			return entries;

		foreach (var e in treeArr.EnumerateArray())
		{
			var path = e.GetProperty("path").GetString() ?? "";
			var type = e.GetProperty("type").GetString() ?? "";
			long size = e.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
				? s.GetInt64()
				: 0;
			var entrySha = e.TryGetProperty("sha", out var sh) && sh.ValueKind == JsonValueKind.String
				? sh.GetString()
				: null;
			entries.Add(new TreeEntry(path, type, size, entrySha));
		}

		return entries;
	}

	/// <summary>
	/// Applies the strict discovery rules to the recursive tree and groups the
	/// results into a <see cref="CatalogTree"/>. All paths in <paramref name="entries"/>
	/// are repo-relative and use forward slashes (GitHub guarantees this).
	/// </summary>
	private static CatalogTree BuildCatalogTree(
		CatalogSource source, GitHubLocator loc, string gitRef, string resolvedSha,
		List<TreeEntry> entries, bool _truncated, RateLimitInfo? rate, string? etag)
	{
		var subpath = loc.Subpath.TrimEnd('/');
		var anchor  = subpath.Length == 0 ? "" : subpath + "/";

		// Filter to entries inside the anchor (or all entries when no anchor).
		bool InAnchor(TreeEntry e) =>
			anchor.Length == 0
			|| e.Path.StartsWith(anchor, StringComparison.Ordinal);

		string Strip(string p) =>
			anchor.Length == 0 ? p : p[anchor.Length..];

		var blobs = entries.Where(e => e.Type == "blob" && InAnchor(e)).ToList();
		var trees = entries.Where(e => e.Type == "tree" && InAnchor(e)).ToList();

		var tree = new CatalogTree
		{
			Source       = source,
			ResolvedRef  = resolvedSha,
			FetchedUtc   = DateTime.UtcNow,
			RateLimit    = rate,
			ETag         = etag,
		};

		// gopilot-instructions.md at the anchor root.
		var goInstr = blobs.FirstOrDefault(b => Strip(b.Path).Equals("gopilot-instructions.md", StringComparison.OrdinalIgnoreCase));
		if (goInstr != null)
		{
			tree.Instructions.Add(new CatalogItem
			{
				Name        = "gopilot-instructions.md",
				Description = "Tier-level GoPilot instructions (installs to <tier>/imported/).",
				Kind        = CatalogItemKind.GoPilotInstructions,
				PrimaryRepoPath = goInstr.Path,
				Files       = new() { new CatalogFile(goInstr.Path, goInstr.Size, goInstr.Sha) },
				TotalBytes  = goInstr.Size,
			});
		}

		// instructions/*.instructions.md (top level only).
		foreach (var b in blobs)
		{
			var rel = Strip(b.Path);
			if (rel.StartsWith("instructions/", StringComparison.OrdinalIgnoreCase)
				&& rel.IndexOf('/', "instructions/".Length) < 0
				&& rel.EndsWith(".instructions.md", StringComparison.OrdinalIgnoreCase))
			{
				var stem = Path.GetFileName(rel);
				tree.Instructions.Add(new CatalogItem
				{
					Name        = stem,
					Description = "Copilot CLI custom-instructions file (installs to <tier>/instructions/).",
					Kind        = CatalogItemKind.CopilotInstructions,
					PrimaryRepoPath = b.Path,
					Files       = new() { new CatalogFile(b.Path, b.Size, b.Sha) },
					TotalBytes  = b.Size,
				});
			}
		}

		// agents/*.md (top level only).
		foreach (var b in blobs)
		{
			var rel = Strip(b.Path);
			if (rel.StartsWith("agents/", StringComparison.OrdinalIgnoreCase)
				&& rel.IndexOf('/', "agents/".Length) < 0
				&& rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
			{
				var stem = Path.GetFileNameWithoutExtension(rel);
				tree.Agents.Add(new CatalogItem
				{
					Name        = stem,
					Kind        = CatalogItemKind.Agent,
					PrimaryRepoPath = b.Path,
					Files       = new() { new CatalogFile(b.Path, b.Size, b.Sha) },
					TotalBytes  = b.Size,
				});
			}
		}

		// prompts/*.md (top level only).
		foreach (var b in blobs)
		{
			var rel = Strip(b.Path);
			if (rel.StartsWith("prompts/", StringComparison.OrdinalIgnoreCase)
				&& rel.IndexOf('/', "prompts/".Length) < 0
				&& rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
			{
				var stem = Path.GetFileNameWithoutExtension(rel);
				tree.Prompts.Add(new CatalogItem
				{
					Name        = stem,
					Kind        = CatalogItemKind.Prompt,
					PrimaryRepoPath = b.Path,
					Files       = new() { new CatalogFile(b.Path, b.Size, b.Sha) },
					TotalBytes  = b.Size,
				});
			}
		}

		// skills/<name>/SKILL.md -- bundle = whole <name>/ folder, recursive.
		foreach (var b in blobs)
		{
			var rel = Strip(b.Path);
			if (!rel.StartsWith("skills/", StringComparison.OrdinalIgnoreCase)) continue;
			var afterSkills = rel["skills/".Length..];
			var slash = afterSkills.IndexOf('/');
			if (slash <= 0) continue;
			var skillName = afterSkills[..slash];
			var tail = afterSkills[(slash + 1)..];
			if (!tail.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) continue;

			// Build the bundle: every blob whose stripped path starts with skills/<skillName>/
			var bundlePrefix = $"skills/{skillName}/";
			var bundleFiles = blobs
				.Select(x => (Entry: x, Stripped: Strip(x.Path)))
				.Where(x => x.Stripped.StartsWith(bundlePrefix, StringComparison.OrdinalIgnoreCase))
				.Select(x => new CatalogFile(x.Entry.Path, x.Entry.Size, x.Entry.Sha))
				.ToList();

			tree.Skills.Add(new CatalogItem
			{
				Name        = skillName,
				Kind        = CatalogItemKind.Skill,
				PrimaryRepoPath = b.Path,
				Files       = bundleFiles,
				TotalBytes  = bundleFiles.Sum(f => f.Bytes),
			});
		}

		// Sort each list alphabetically for stable display.
		tree.Skills.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
		tree.Agents.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
		tree.Prompts.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
		tree.Instructions.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

		_ = trees; // tree-type entries are not needed -- blob filter is sufficient
		return tree;
	}

	private static RateLimitInfo? ReadRateLimit(HttpResponseMessage resp)
	{
		if (!TryGetIntHeader(resp, "X-RateLimit-Limit", out var limit)
			|| !TryGetIntHeader(resp, "X-RateLimit-Remaining", out var remaining))
		{
			return null;
		}

		DateTime? reset = null;
		if (TryGetIntHeader(resp, "X-RateLimit-Reset", out var resetEpoch))
			reset = DateTimeOffset.FromUnixTimeSeconds(resetEpoch).UtcDateTime;

		return new RateLimitInfo(limit, remaining, reset);
	}

	private static bool TryGetIntHeader(HttpResponseMessage resp, string name, out int value)
	{
		value = 0;
		if (!resp.Headers.TryGetValues(name, out var values)) return false;
		var s = values.FirstOrDefault();
		if (s == null) return false;
		return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
	}
}

