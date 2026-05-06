using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace GoPilot.SkillCatalog;

/// <summary>
/// Anonymous-only Azure DevOps catalog provider. Lists items via the
/// <c>_apis/git/repositories/.../items</c> endpoint with
/// <c>recursionLevel=Full</c> and downloads file content from the same
/// endpoint with <c>download=true&amp;$format=octetStream</c>. Detects
/// 401 / 203-with-login-redirect responses and surfaces them as
/// <see cref="CatalogProviderErrorKind.AuthRequired"/>.
/// </summary>
internal sealed class AzureDevOpsCatalogProvider : ICatalogProvider
{
	private const string ApiVersion = "7.1";
	private readonly HttpClient _http;

	public AzureDevOpsCatalogProvider(HttpClient http) => _http = http;

	public ProviderKind Kind => ProviderKind.AzureDevOps;

	/// <summary>
	/// Parsed components of an ADO source URL. <see cref="Subpath"/> is the
	/// <c>?path=</c> query value when present, otherwise empty.
	/// </summary>
	private sealed record AdoLocator(string ApiBase, string Org, string Project, string Repo, string? Branch, string Subpath);

	/// <summary>
	/// Recognised forms (case-insensitive on host):
	///   https://dev.azure.com/{org}/{project}/_git/{repo}
	///   https://dev.azure.com/{org}/{project}/_git/{repo}?path=/foo&amp;version=GBmain
	///   https://{org}.visualstudio.com/{project}/_git/{repo}
	/// </summary>
	private static AdoLocator ParseUrl(string url)
	{
		var uri = new Uri(url, UriKind.Absolute);
		var host = uri.Host.ToLowerInvariant();
		var segments = uri.AbsolutePath
			.Split('/', StringSplitOptions.RemoveEmptyEntries);

		string apiBase, org, project, repo;
		int gitIdx = Array.FindIndex(segments, s => s.Equals("_git", StringComparison.OrdinalIgnoreCase));
		if (gitIdx < 0 || gitIdx == segments.Length - 1)
		{
			throw new CatalogProviderException(CatalogProviderErrorKind.BadResponse,
				$"Azure DevOps URL must include /_git/<repo>: {url}");
		}

		repo = segments[gitIdx + 1];

		if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
		{
			// {org}.visualstudio.com/{project}/_git/{repo}
			org = host[..^".visualstudio.com".Length];
			project = gitIdx >= 1 ? segments[gitIdx - 1] : org;
			apiBase = $"https://{host}";
		}
		else if (host == "dev.azure.com")
		{
			// dev.azure.com/{org}/{project}/_git/{repo}
			if (gitIdx < 2)
				throw new CatalogProviderException(CatalogProviderErrorKind.BadResponse,
					$"dev.azure.com URL must include /<org>/<project>/_git/<repo>: {url}");
			org     = segments[0];
			project = segments[gitIdx - 1];
			apiBase = $"https://{host}/{Uri.EscapeDataString(org)}";
		}
		else
		{
			throw new CatalogProviderException(CatalogProviderErrorKind.BadResponse,
				$"Unrecognised Azure DevOps host: {url}");
		}

		// Optional query parameters: path (subpath anchor), version (branch).
		string? branch  = null;
		string  subpath = "";
		var query = HttpUtility.ParseQueryString(uri.Query);
		var pathParam = query["path"];
		if (!string.IsNullOrEmpty(pathParam))
		{
			subpath = pathParam.Trim('/');
		}
		var versionParam = query["version"];
		if (!string.IsNullOrEmpty(versionParam))
		{
			// ADO encodes version as GB<branch> (or GT<tag>, GC<commit>).
			if (versionParam.StartsWith("GB", StringComparison.Ordinal))
				branch = versionParam[2..];
			else
				branch = versionParam;
		}

		return new AdoLocator(apiBase, org, project, repo, branch, subpath);
	}

	public async Task<CatalogTree> ListAsync(CatalogSource source, string? cachedEtag, CancellationToken ct)
	{
		var loc = ParseUrl(source.Url);

		// items endpoint with full recursion under the (optional) scopePath.
		var qs = new List<string>
		{
			$"api-version={ApiVersion}",
			"recursionLevel=Full",
		};
		if (!string.IsNullOrEmpty(loc.Subpath))
			qs.Add($"scopePath=/{Uri.EscapeDataString(loc.Subpath).Replace("%2F", "/")}");
		if (!string.IsNullOrEmpty(loc.Branch))
			qs.Add($"versionDescriptor.version={Uri.EscapeDataString(loc.Branch)}&versionDescriptor.versionType=branch");

		var url = $"{loc.ApiBase}/{Uri.EscapeDataString(loc.Project)}/_apis/git/repositories/{Uri.EscapeDataString(loc.Repo)}/items?{string.Join('&', qs)}";

		using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);

		if (resp.StatusCode == HttpStatusCode.Unauthorized)
			throw AuthRequiredException();

		// ADO often returns 203 Non-Authoritative with an HTML sign-in page
		// when anonymous access isn't permitted. Detect by content-type.
		if ((int)resp.StatusCode == 203
			|| (resp.StatusCode == HttpStatusCode.OK
				&& resp.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true))
		{
			throw AuthRequiredException();
		}

		if (resp.StatusCode == HttpStatusCode.NotFound)
			throw new CatalogProviderException(CatalogProviderErrorKind.PrivateOrNotFound,
				"Azure DevOps repository not found (or anonymous access disabled).");

		if (!resp.IsSuccessStatusCode)
			throw new CatalogProviderException(CatalogProviderErrorKind.BadResponse,
				$"Azure DevOps returned {(int)resp.StatusCode} {resp.ReasonPhrase} for {url}");

		var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
		var entries = ParseItems(json);

		// ADO doesn't return a single "tree SHA" -- use the latest commit-id from the listing
		// (every entry carries one) as the resolved ref. Fall back to branch name when missing.
		var resolvedRef = entries
			.Select(e => e.CommitId)
			.FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? loc.Branch ?? "";

		return BuildCatalogTree(source, loc, resolvedRef, entries);
	}

	public async Task<byte[]> FetchFileAsync(CatalogSource source, string resolvedRef, string repoRelPath, CancellationToken ct)
	{
		var loc = ParseUrl(source.Url);
		var qs = new List<string>
		{
			$"api-version={ApiVersion}",
			$"path=/{Uri.EscapeDataString(repoRelPath).Replace("%2F", "/")}",
			"download=true",
			"$format=octetStream",
		};
		if (!string.IsNullOrEmpty(resolvedRef))
		{
			// versionDescriptor.versionType=commit when we have a commit id
			// (the listing returns commitIds), otherwise treat as branch.
			var versionType = resolvedRef.Length == 40 ? "commit" : "branch";
			qs.Add($"versionDescriptor.version={Uri.EscapeDataString(resolvedRef)}");
			qs.Add($"versionDescriptor.versionType={versionType}");
		}

		var url = $"{loc.ApiBase}/{Uri.EscapeDataString(loc.Project)}/_apis/git/repositories/{Uri.EscapeDataString(loc.Repo)}/items?{string.Join('&', qs)}";

		using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);

		if (resp.StatusCode == HttpStatusCode.Unauthorized) throw AuthRequiredException();
		if (resp.StatusCode == HttpStatusCode.NotFound)
			throw new CatalogProviderException(CatalogProviderErrorKind.PrivateOrNotFound, $"File not found: {url}");
		if (!resp.IsSuccessStatusCode)
			throw new CatalogProviderException(CatalogProviderErrorKind.BadResponse,
				$"Azure DevOps returned {(int)resp.StatusCode} {resp.ReasonPhrase} for {url}");

		return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
	}

	private static CatalogProviderException AuthRequiredException() =>
		new(CatalogProviderErrorKind.AuthRequired,
			"This Azure DevOps source requires sign-in. GoPilot does not collect credentials; use a manual `git clone` for this source instead.");

	private sealed record AdoEntry(string Path, bool IsFolder, long Size, string? CommitId, string? ObjectId);

	private static List<AdoEntry> ParseItems(string json)
	{
		var list = new List<AdoEntry>();
		using var doc = JsonDocument.Parse(json);
		if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array)
			return list;

		foreach (var e in arr.EnumerateArray())
		{
			var path = e.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
				? p.GetString() ?? ""
				: "";
			var isFolder = e.TryGetProperty("isFolder", out var f)
				&& f.ValueKind == JsonValueKind.True;
			long size = e.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
				? s.GetInt64()
				: 0;
			var commitId = e.TryGetProperty("commitId", out var c) && c.ValueKind == JsonValueKind.String
				? c.GetString()
				: null;
			var objectId = e.TryGetProperty("objectId", out var o) && o.ValueKind == JsonValueKind.String
				? o.GetString()
				: null;
			list.Add(new AdoEntry(path.TrimStart('/'), isFolder, size, commitId, objectId));
		}

		return list;
	}

	private static CatalogTree BuildCatalogTree(CatalogSource source, AdoLocator loc, string resolvedRef, List<AdoEntry> entries)
	{
		var anchor = string.IsNullOrEmpty(loc.Subpath) ? "" : loc.Subpath.TrimEnd('/') + "/";

		bool InAnchor(AdoEntry e) =>
			anchor.Length == 0 || e.Path.StartsWith(anchor, StringComparison.OrdinalIgnoreCase);

		string Strip(string p) =>
			anchor.Length == 0 ? p : p[anchor.Length..];

		var blobs = entries.Where(e => !e.IsFolder && InAnchor(e)).ToList();

		var tree = new CatalogTree
		{
			Source      = source,
			ResolvedRef = resolvedRef,
			FetchedUtc  = DateTime.UtcNow,
			RateLimit   = null,
		};

		var goInstr = blobs.FirstOrDefault(b => Strip(b.Path).Equals("gopilot-instructions.md", StringComparison.OrdinalIgnoreCase));
		if (goInstr != null)
		{
			tree.Instructions.Add(new CatalogItem
			{
				Name        = "gopilot-instructions.md",
				Description = "Tier-level GoPilot instructions (installs to <tier>/imported/).",
				Kind        = CatalogItemKind.GoPilotInstructions,
				PrimaryRepoPath = goInstr.Path,
				Files       = new() { new CatalogFile(goInstr.Path, goInstr.Size, goInstr.ObjectId) },
				TotalBytes  = goInstr.Size,
			});
		}

		foreach (var b in blobs)
		{
			var rel = Strip(b.Path);
			if (rel.StartsWith("instructions/", StringComparison.OrdinalIgnoreCase)
				&& rel.IndexOf('/', "instructions/".Length) < 0
				&& rel.EndsWith(".instructions.md", StringComparison.OrdinalIgnoreCase))
			{
				tree.Instructions.Add(new CatalogItem
				{
					Name        = Path.GetFileName(rel),
					Description = "Copilot CLI custom-instructions file (installs to <tier>/instructions/).",
					Kind        = CatalogItemKind.CopilotInstructions,
					PrimaryRepoPath = b.Path,
					Files       = new() { new CatalogFile(b.Path, b.Size, b.ObjectId) },
					TotalBytes  = b.Size,
				});
			}
		}

		foreach (var b in blobs)
		{
			var rel = Strip(b.Path);
			if (rel.StartsWith("agents/", StringComparison.OrdinalIgnoreCase)
				&& rel.IndexOf('/', "agents/".Length) < 0
				&& rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
			{
				tree.Agents.Add(new CatalogItem
				{
					Name = Path.GetFileNameWithoutExtension(rel),
					Kind = CatalogItemKind.Agent,
					PrimaryRepoPath = b.Path,
					Files = new() { new CatalogFile(b.Path, b.Size, b.ObjectId) },
					TotalBytes = b.Size,
				});
			}
		}

		foreach (var b in blobs)
		{
			var rel = Strip(b.Path);
			if (rel.StartsWith("prompts/", StringComparison.OrdinalIgnoreCase)
				&& rel.IndexOf('/', "prompts/".Length) < 0
				&& rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
			{
				tree.Prompts.Add(new CatalogItem
				{
					Name = Path.GetFileNameWithoutExtension(rel),
					Kind = CatalogItemKind.Prompt,
					PrimaryRepoPath = b.Path,
					Files = new() { new CatalogFile(b.Path, b.Size, b.ObjectId) },
					TotalBytes = b.Size,
				});
			}
		}

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

			var bundlePrefix = $"skills/{skillName}/";
			var bundleFiles = blobs
				.Select(x => (Entry: x, Stripped: Strip(x.Path)))
				.Where(x => x.Stripped.StartsWith(bundlePrefix, StringComparison.OrdinalIgnoreCase))
				.Select(x => new CatalogFile(x.Entry.Path, x.Entry.Size, x.Entry.ObjectId))
				.ToList();

			tree.Skills.Add(new CatalogItem
			{
				Name = skillName,
				Kind = CatalogItemKind.Skill,
				PrimaryRepoPath = b.Path,
				Files = bundleFiles,
				TotalBytes = bundleFiles.Sum(f => f.Bytes),
			});
		}

		tree.Skills.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
		tree.Agents.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
		tree.Prompts.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
		tree.Instructions.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

		return tree;
	}
}

