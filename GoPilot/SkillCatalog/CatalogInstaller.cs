using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace GoPilot.SkillCatalog;

/// <summary>
/// Writes catalog items to disk under a chosen tier folder, applies the
/// safety caps documented in the plan (per-file 1 MiB, per-bundle 10 MiB,
/// extension whitelist, no path traversal), and emits a sidecar manifest
/// (<c>.gopilot-source.json</c>) per item so a future "Update" feature can
/// re-pull from the same source.
/// </summary>
internal sealed class CatalogInstaller
{
	public const long PerFileCapBytes   = 1L * 1024 * 1024;
	public const long PerBundleCapBytes = 10L * 1024 * 1024;

	private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase)
	{
		".md", ".txt", ".json", ".yaml", ".yml", ".toml", ".ini", ".csv",
	};

	private static readonly HashSet<string> ScriptExts = new(StringComparer.OrdinalIgnoreCase)
	{
		".ps1", ".sh", ".bat", ".cmd", ".py", ".js", ".ts",
		".exe", ".dll", ".so", ".dylib",
	};

	private readonly ICatalogProvider _provider;

	public CatalogInstaller(ICatalogProvider provider) => _provider = provider;

	/// <summary>
	/// Installs <paramref name="item"/> under <paramref name="tierRoot"/>.
	/// <paramref name="allowScripts"/> must be true to write any file with a
	/// script/binary extension; otherwise such files cause the install to be
	/// rejected up-front.
	/// </summary>
	public async Task<InstallResult> InstallAsync(
		CatalogTree tree,
		CatalogItem item,
		string tierRoot,
		bool allowScripts,
		CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(tierRoot)) throw new ArgumentException("tierRoot");
		Directory.CreateDirectory(tierRoot);

		ValidateBundle(item, allowScripts);

		var slug = tree.Source.Slug;
		var (destFolder, primaryDest, mode) = ResolveDestination(item, tierRoot, slug);

		Directory.CreateDirectory(destFolder);

		var written = new List<ManifestFile>();
		long totalBytes = 0;

		switch (mode)
		{
			case InstallMode.SingleFile:
			{
				var bytes = await _provider.FetchFileAsync(tree.Source, tree.ResolvedRef, item.PrimaryRepoPath, ct).ConfigureAwait(false);
				if (bytes.LongLength > PerFileCapBytes)
					throw new InvalidOperationException($"File exceeds {PerFileCapBytes:N0}-byte cap: {item.PrimaryRepoPath}");
				totalBytes += bytes.LongLength;
				if (totalBytes > PerBundleCapBytes)
					throw new InvalidOperationException($"Bundle exceeds {PerBundleCapBytes:N0}-byte cap.");
				File.WriteAllBytes(primaryDest, bytes);
				written.Add(new ManifestFile(item.PrimaryRepoPath, item.Files.FirstOrDefault()?.Sha, bytes.LongLength));
				break;
			}

			case InstallMode.Bundle:
			{
				// destFolder = the skill-bundle folder. Files are written
				// preserving their layout *under* the original skills/<name>/
				// prefix, so SKILL.md ends up at destFolder/SKILL.md.
				var bundlePrefix = ExtractBundlePrefix(item.PrimaryRepoPath);
				foreach (var file in item.Files)
				{
					ct.ThrowIfCancellationRequested();
					var rel = StripPrefix(file.RepoPath, bundlePrefix);
					if (rel == null) continue; // shouldn't happen

					RejectUnsafeRelPath(rel);
					var ext = Path.GetExtension(rel);
					if (!TextExts.Contains(ext) && !(allowScripts && ScriptExts.Contains(ext)))
					{
						// Silently skip unrecognised extensions inside a
						// bundle rather than aborting -- README, LICENSE,
						// and similar plain-text files often have no extension.
						if (!string.IsNullOrEmpty(ext)) continue;
					}

					var bytes = await _provider.FetchFileAsync(tree.Source, tree.ResolvedRef, file.RepoPath, ct).ConfigureAwait(false);
					if (bytes.LongLength > PerFileCapBytes)
						throw new InvalidOperationException($"File exceeds {PerFileCapBytes:N0}-byte cap: {file.RepoPath}");
					totalBytes += bytes.LongLength;
					if (totalBytes > PerBundleCapBytes)
						throw new InvalidOperationException($"Bundle exceeds {PerBundleCapBytes:N0}-byte cap.");

					var dest = Path.Combine(destFolder, rel.Replace('/', Path.DirectorySeparatorChar));
					var destDir = Path.GetDirectoryName(dest);
					if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
					File.WriteAllBytes(dest, bytes);
					written.Add(new ManifestFile(file.RepoPath, file.Sha, bytes.LongLength));
				}
				break;
			}
		}

		var manifest = new SourceManifest
		{
			SourceUrl   = tree.Source.Url,
			Provider    = tree.Source.Kind.ToString(),
			Ref         = tree.ResolvedRef,
			FetchedUtc  = DateTime.UtcNow,
			Kind        = item.Kind.ToString(),
			Files       = written,
		};
		var manifestPath = mode == InstallMode.Bundle
			? Path.Combine(destFolder, ".gopilot-source.json")
			: primaryDest + ".gopilot-source.json";
		File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, ManifestJsonOpts));

		return new InstallResult(item, primaryDest, totalBytes, written.Count);
	}

	private static readonly JsonSerializerOptions ManifestJsonOpts = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private static void ValidateBundle(CatalogItem item, bool allowScripts)
	{
		if (item.TotalBytes > PerBundleCapBytes)
			throw new InvalidOperationException($"Bundle exceeds {PerBundleCapBytes:N0}-byte cap.");

		foreach (var f in item.Files)
		{
			RejectUnsafeRelPath(f.RepoPath);
			if (f.Bytes > PerFileCapBytes)
				throw new InvalidOperationException($"File exceeds {PerFileCapBytes:N0}-byte cap: {f.RepoPath}");
			var ext = Path.GetExtension(f.RepoPath);
			if (ScriptExts.Contains(ext) && !allowScripts)
				throw new InvalidOperationException($"Bundle contains script/binary file '{f.RepoPath}'. Re-run with 'Allow scripts' enabled to install.");
		}
	}

	private static void RejectUnsafeRelPath(string p)
	{
		if (string.IsNullOrEmpty(p))
			throw new InvalidOperationException("Empty repo path.");
		if (p.StartsWith("/") || p.StartsWith("\\"))
			throw new InvalidOperationException($"Absolute repo path rejected: {p}");
		if (p.Length >= 2 && p[1] == ':')
			throw new InvalidOperationException($"Drive-letter path rejected: {p}");
		foreach (var seg in p.Split('/', '\\'))
		{
			if (seg == "..")
				throw new InvalidOperationException($"Path traversal rejected: {p}");
		}
	}

	private enum InstallMode { SingleFile, Bundle }

	private static (string destFolder, string primaryDest, InstallMode mode) ResolveDestination(
		CatalogItem item, string tierRoot, string slug)
	{
		switch (item.Kind)
		{
			case CatalogItemKind.Skill:
			{
				var bundleName = $"{slug}__{SafeName(item.Name)}";
				var folder = Path.Combine(tierRoot, "skills", bundleName);
				var primary = Path.Combine(folder, "SKILL.md");
				return (folder, primary, InstallMode.Bundle);
			}
			case CatalogItemKind.Agent:
			{
				var folder = Path.Combine(tierRoot, "agents");
				var file = Path.Combine(folder, $"{slug}__{SafeName(item.Name)}.md");
				return (folder, file, InstallMode.SingleFile);
			}
			case CatalogItemKind.Prompt:
			{
				var folder = Path.Combine(tierRoot, "prompts");
				var file = Path.Combine(folder, $"{slug}__{SafeName(item.Name)}.md");
				return (folder, file, InstallMode.SingleFile);
			}
			case CatalogItemKind.GoPilotInstructions:
			{
				// Never overwrite tier-root gopilot-instructions.md.
				var folder = Path.Combine(tierRoot, "imported");
				var file = Path.Combine(folder, $"{slug}-instructions.md");
				return (folder, file, InstallMode.SingleFile);
			}
			case CatalogItemKind.CopilotInstructions:
			{
				var folder = Path.Combine(tierRoot, "instructions");
				var name = SafeName(Path.GetFileNameWithoutExtension(item.Name)
					.Replace(".instructions", "", StringComparison.OrdinalIgnoreCase));
				var file = Path.Combine(folder, $"{slug}__{name}.instructions.md");
				return (folder, file, InstallMode.SingleFile);
			}
			default:
				throw new InvalidOperationException($"Unknown item kind: {item.Kind}");
		}
	}

	private static string SafeName(string raw)
	{
		var sb = new System.Text.StringBuilder(raw.Length);
		foreach (var c in raw)
		{
			if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
				sb.Append(c);
			else
				sb.Append('_');
		}
		var s = sb.ToString().Trim('.', '_');
		return s.Length == 0 ? "item" : s;
	}

	private static string ExtractBundlePrefix(string skillMdPath)
	{
		// e.g. "skills/my-skill/SKILL.md" -> "skills/my-skill/"
		var lastSlash = skillMdPath.LastIndexOf('/');
		return lastSlash <= 0 ? "" : skillMdPath[..(lastSlash + 1)];
	}

	private static string? StripPrefix(string path, string prefix)
	{
		if (prefix.Length == 0) return path;
		return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
			? path[prefix.Length..]
			: null;
	}
}

internal sealed record InstallResult(CatalogItem Item, string PrimaryDestination, long TotalBytes, int FilesWritten);

internal sealed class SourceManifest
{
	public string SourceUrl  { get; init; } = "";
	public string Provider   { get; init; } = "";
	public string Ref        { get; init; } = "";
	public DateTime FetchedUtc { get; init; }
	public string Kind       { get; init; } = "";
	public List<ManifestFile> Files { get; init; } = new();
}

internal sealed record ManifestFile(string Path, string? Sha, long Bytes);
