using System;
using System.Text.RegularExpressions;

namespace GoPilot.SkillCatalog;

/// <summary>
/// Identifies the kind of remote that a <see cref="CatalogSource"/> URL points
/// at. The host name is the discriminator (github.com -> GitHub,
/// dev.azure.com / *.visualstudio.com -> AzureDevOps). Unknown hosts use
/// <see cref="Unknown"/> and are skipped by the catalog browser with a clear
/// "unsupported source" message.
/// </summary>
internal enum ProviderKind
{
	Unknown      = 0,
	GitHub       = 1,
	AzureDevOps  = 2,
}

/// <summary>
/// A single user-curated entry in the [SkillSources] list. <see cref="Url"/>
/// is the verbatim string the user typed; <see cref="Kind"/> is inferred from
/// the host. Anchoring metadata (owner, repo, ref, subpath for GitHub;
/// org/project/repo/branch/subpath for ADO) is parsed lazily by the
/// provider implementations -- this record is intentionally a thin value
/// object so it round-trips through cache JSON without surprises.
/// </summary>
internal sealed record CatalogSource(string Url, string Label, ProviderKind Kind)
{
	/// <summary>
	/// Parses a verbatim URL into a <see cref="CatalogSource"/>. The provider
	/// is inferred from the host. Returns a record with <c>Kind = Unknown</c>
	/// when the URL is well-formed but the host is not one we support.
	/// Throws <see cref="UriFormatException"/> on malformed URLs so callers can
	/// surface a clear validation error in the Skill Sources dialog.
	/// </summary>
	public static CatalogSource Parse(string url)
	{
		if (string.IsNullOrWhiteSpace(url))
			throw new UriFormatException("URL is empty.");

		var trimmed = url.Trim();
		var uri = new Uri(trimmed, UriKind.Absolute);
		if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
			throw new UriFormatException("URL must use http or https.");

		var kind = ProviderKind.Unknown;
		var host = uri.Host.ToLowerInvariant();

		if (host == "github.com" || host == "www.github.com")
			kind = ProviderKind.GitHub;
		else if (host == "dev.azure.com" || host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
			kind = ProviderKind.AzureDevOps;

		return new CatalogSource(trimmed, MakeLabelFor(uri), kind);
	}

	/// <summary>
	/// Non-throwing variant of <see cref="Parse"/>. Returns false and sets
	/// <paramref name="error"/> when the URL is not parseable. A successfully
	/// parsed URL with an unsupported host returns true with
	/// <c>source.Kind == ProviderKind.Unknown</c> -- callers should still
	/// reject it where it can't be acted on.
	/// </summary>
	public static bool TryParse(string url, out CatalogSource? source, out string? error)
	{
		try
		{
			source = Parse(url);
			error  = null;
			return true;
		}
		catch (UriFormatException ex)
		{
			source = null;
			error  = ex.Message;
			return false;
		}
		catch (Exception ex)
		{
			source = null;
			error  = ex.Message;
			return false;
		}
	}

	/// <summary>
	/// Returns the short label computed at parse time. Convenience accessor
	/// for callers that didn't keep the original Uri around.
	/// </summary>
	public string MakeLabel() => Label;

	/// <summary>
	/// Builds a short human-readable label for a source URL: "<host>/<first
	/// two path segments>" when present, or just the host. Used in the source
	/// dropdown and the file-system slug for the cache key.
	/// </summary>
	private static string MakeLabelFor(Uri uri)
	{
		var segments = uri.AbsolutePath
			.Split('/', StringSplitOptions.RemoveEmptyEntries);

		if (segments.Length == 0)
			return uri.Host;

		var take = Math.Min(2, segments.Length);
		return $"{uri.Host}/{string.Join('/', segments[..take])}";
	}

	/// <summary>
	/// Slug suitable for a per-source cache filename. Lowercases the URL,
	/// then replaces every character outside <c>[a-z0-9]</c> with a single
	/// underscore. The cache file additionally includes a SHA-1 of the URL
	/// (computed by <see cref="CatalogCache"/>) so two URLs that slugify to
	/// the same string still get distinct files.
	/// </summary>
	public string Slug
	{
		get
		{
			var lowered = Url.ToLowerInvariant();
			return Regex.Replace(lowered, "[^a-z0-9]+", "_").Trim('_');
		}
	}
}
