using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GoPilot.SkillCatalog;

/// <summary>
/// Categorises a <see cref="CatalogItem"/> so the browser can show a separate
/// node group per kind and the installer can choose the correct destination
/// subfolder under the chosen tier.
/// </summary>
internal enum CatalogItemKind
{
	Skill                   = 1,
	Agent                   = 2,
	Prompt                  = 3,
	GoPilotInstructions     = 4,  // gopilot-instructions.md at source root
	CopilotInstructions     = 5,  // *.instructions.md inside instructions/
}

/// <summary>
/// A single downloadable entry surfaced by the catalog browser. <see
/// cref="Files"/> lists every repo-relative path (forward slashes, no leading
/// '/') that should be fetched when the user installs this item -- a single
/// entry for agents/prompts/instructions, the whole skill folder recursively
/// for skills.
/// </summary>
internal sealed class CatalogItem
{
	/// <summary>Human-readable name -- front-matter <c>name</c> when present, file
	/// stem otherwise (folder name for skills).</summary>
	public string Name { get; init; } = "";

	/// <summary>Optional short description from front-matter.</summary>
	public string? Description { get; init; }

	/// <summary>Categorisation -- drives the install destination and the tree
	/// node group.</summary>
	public CatalogItemKind Kind { get; init; }

	/// <summary>Repo-relative path (forward slashes) to the primary file:
	/// SKILL.md for skills, the *.md file itself for agents/prompts/instructions.
	/// Used as the preview source in the dialog.</summary>
	public string PrimaryRepoPath { get; init; } = "";

	/// <summary>Every file that needs to be downloaded for this item, with its
	/// repo-relative path and byte size. Used by the installer to enforce the
	/// per-bundle size cap and to drive the progress bar.</summary>
	public List<CatalogFile> Files { get; init; } = new();

	/// <summary>Sum of <see cref="CatalogFile.Bytes"/>. Pre-computed so the
	/// dialog doesn't have to walk <see cref="Files"/> repeatedly.</summary>
	public long TotalBytes { get; init; }
}

/// <summary>
/// One file entry inside a <see cref="CatalogItem.Files"/> list. <see cref="Sha"/>
/// is the provider's content identifier (GitHub blob SHA, ADO objectId) and
/// is recorded in the install-time sidecar manifest for future
/// "update available" comparison.
/// </summary>
internal sealed record CatalogFile(string RepoPath, long Bytes, string? Sha);

/// <summary>
/// Result of a single <see cref="ICatalogProvider.ListAsync"/> call.
/// </summary>
internal sealed class CatalogTree
{
	public CatalogSource Source { get; init; } = null!;

	/// <summary>The commit SHA / object id that was actually listed. Recorded
	/// in the cache and the install-time manifest so a subsequent "update"
	/// check can compare against the current upstream HEAD.</summary>
	public string ResolvedRef { get; init; } = "";

	/// <summary>UTC timestamp of the listing operation.</summary>
	public DateTime FetchedUtc { get; init; }

	public List<CatalogItem> Skills       { get; init; } = new();
	public List<CatalogItem> Agents       { get; init; } = new();
	public List<CatalogItem> Prompts      { get; init; } = new();
	public List<CatalogItem> Instructions { get; init; } = new();

	/// <summary>True when none of the four lists has any entries -- used by the
	/// browser to surface the "(no recognized items)" hint.</summary>
	public bool IsEmpty =>
		Skills.Count == 0 && Agents.Count == 0
		&& Prompts.Count == 0 && Instructions.Count == 0;

	/// <summary>Latest rate-limit / quota snapshot reported by the provider on
	/// the listing request, if any. <c>null</c> means the provider does not
	/// surface rate-limit information (e.g. ADO).</summary>
	public RateLimitInfo? RateLimit { get; init; }

	/// <summary>Cache validator returned by the provider on the listing
	/// request -- ETag for GitHub, null for ADO. Recorded in the cache so
	/// the next list call can issue a conditional request.</summary>
	public string? ETag { get; init; }
}

/// <summary>
/// Rate-limit snapshot from a provider response. <see cref="Remaining"/> is
/// the current remaining call quota; <see cref="ResetUtc"/> is when the
/// window rolls over. Only GitHub fills this in for v1.
/// </summary>
internal sealed record RateLimitInfo(int Limit, int Remaining, DateTime? ResetUtc);

/// <summary>
/// Distinguishes "this is an expected upstream condition the user can act
/// on" (rate-limited, private/sign-in-required, no recognized items) from
/// "something genuinely went wrong" (network failure, 5xx). The browser
/// dialog shows the message verbatim and chooses the icon based on the kind.
/// </summary>
internal sealed class CatalogProviderException : Exception
{
	public CatalogProviderErrorKind Kind { get; }

	public CatalogProviderException(CatalogProviderErrorKind kind, string message, Exception? inner = null)
		: base(message, inner)
	{
		Kind = kind;
	}
}

internal enum CatalogProviderErrorKind
{
	Network              = 1,
	RateLimited          = 2,
	PrivateOrNotFound    = 3,
	AuthRequired         = 4,
	UnsupportedProvider  = 5,
	BadResponse          = 6,
}

/// <summary>
/// Strategy interface implemented per-provider (GitHub, Azure DevOps).
/// All implementations are stateless beyond the shared <see cref="System.Net.Http.HttpClient"/>
/// they receive via DI; the per-source cache is layered on top and is not the
/// provider's concern.
/// </summary>
internal interface ICatalogProvider
{
	/// <summary>Set of provider kinds this implementation handles. Used by
	/// <see cref="CatalogProviderRegistry"/> to dispatch.</summary>
	ProviderKind Kind { get; }

	/// <summary>
	/// Lists every recognised catalog item under <paramref name="source"/>.
	/// Implementations are expected to issue the minimum number of REST calls
	/// (one for the recursive tree, ideally) and apply the strict discovery
	/// rules described in the plan. Throws <see cref="CatalogProviderException"/>
	/// on rate-limit, auth-required, or other expected error conditions.
	/// </summary>
	Task<CatalogTree> ListAsync(CatalogSource source, string? cachedEtag, CancellationToken ct);

	/// <summary>
	/// Downloads a single file's bytes from the resolved ref returned by the
	/// most-recent <see cref="ListAsync"/> for the same source. The caller
	/// supplies the repo-relative path verbatim from <see cref="CatalogFile.RepoPath"/>.
	/// Throws <see cref="CatalogProviderException"/> on transport failures.
	/// </summary>
	Task<byte[]> FetchFileAsync(CatalogSource source, string resolvedRef, string repoRelPath, CancellationToken ct);
}

/// <summary>
/// Lightweight, ETag-aware cache wrapper indication. Returned from a List
/// operation when the upstream returned 304 Not Modified, telling the caller
/// to keep using its cached <see cref="CatalogTree"/>.
/// </summary>
internal sealed class CatalogTreeNotModifiedException : Exception
{
	public CatalogTreeNotModifiedException() : base("Upstream returned 304 Not Modified.") { }
}
