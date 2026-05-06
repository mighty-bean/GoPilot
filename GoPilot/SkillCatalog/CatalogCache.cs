using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoPilot.SkillCatalog;

/// <summary>
/// Per-source on-disk cache for <see cref="CatalogTree"/> listings. Keyed by
/// SHA-1 of the source URL plus the source slug for human-readable filenames.
/// Stores the full tree alongside an ETag (GitHub) or commit SHA (ADO) so the
/// next list call can issue a conditional request.
/// </summary>
internal sealed class CatalogCache
{
	/// <summary>
	/// Cache root directory. Resolved once at construction so the catalog
	/// dialog can be opened before any session is created (in which case the
	/// service has no per-workspace cache path yet, and we fall back to a
	/// machine-global folder under %LOCALAPPDATA%).
	/// </summary>
	public string CacheDir { get; }

	private static readonly JsonSerializerOptions JsonOpts = new()
	{
		WriteIndented = false,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	public CatalogCache(string? workspaceDataPath)
	{
		var root = !string.IsNullOrEmpty(workspaceDataPath)
			? workspaceDataPath
			: Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"GoPilot",
				"workspaces",
				"_global");
		CacheDir = Path.Combine(root, "catalog-cache");
		Directory.CreateDirectory(CacheDir);
	}

	private string PathFor(CatalogSource source)
	{
		var bytes = Encoding.UTF8.GetBytes(source.Url.ToLowerInvariant());
		var hash  = SHA1.HashData(bytes);
		var hex   = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
		var slug  = source.Slug;
		if (slug.Length > 80) slug = slug[..80];
		return Path.Combine(CacheDir, $"{slug}-{hex}.json");
	}

	/// <summary>
	/// Returns the cached entry for <paramref name="source"/> if present,
	/// or null when no cache exists / the cache is corrupt. Errors are
	/// swallowed: the worst case is one extra REST call.
	/// </summary>
	public CacheEntry? TryLoad(CatalogSource source)
	{
		var path = PathFor(source);
		if (!File.Exists(path)) return null;
		try
		{
			var json = File.ReadAllText(path);
			return JsonSerializer.Deserialize<CacheEntry>(json, JsonOpts);
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Atomically writes <paramref name="entry"/> to the cache for the source
	/// it references. Best-effort -- failure to persist is silent (the next
	/// listing will simply be a full fetch).
	/// </summary>
	public void Save(CacheEntry entry)
	{
		var path = PathFor(entry.Tree.Source);
		try
		{
			var tmp = path + ".tmp";
			var json = JsonSerializer.Serialize(entry, JsonOpts);
			File.WriteAllText(tmp, json);
			if (File.Exists(path)) File.Delete(path);
			File.Move(tmp, path);
		}
		catch
		{
			// best-effort
		}
	}

	/// <summary>
	/// Drops any stored cache entry for the given source (e.g. when the user
	/// presses Refresh and we want a force-revalidate that ignores ETag).
	/// </summary>
	public void Drop(CatalogSource source)
	{
		var path = PathFor(source);
		try { if (File.Exists(path)) File.Delete(path); }
		catch { /* best-effort */ }
	}

	/// <summary>Removes every cached listing. Useful in tests / when the user
	/// asks to "Refresh all".</summary>
	public void Clear()
	{
		try
		{
			foreach (var f in Directory.GetFiles(CacheDir, "*.json"))
				File.Delete(f);
		}
		catch
		{
			// best-effort
		}
	}
}

/// <summary>
/// One cached source listing plus the validator (ETag for GitHub, latest
/// commit id for ADO) needed to send a conditional request next time.
/// </summary>
internal sealed class CacheEntry
{
	public CatalogTree Tree { get; init; } = null!;
	public string?     ETag { get; init; }
}
