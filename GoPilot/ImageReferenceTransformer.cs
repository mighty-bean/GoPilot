using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace GoPilot;

/// <summary>
/// Detects bare image-file references in Copilot's assistant markdown
/// (e.g. <c>screenshot.png</c>, <c>assets/diagram.svg</c>,
/// <c>@docs/img/foo.jpg</c>, or <c>C:\path\to\bar.png</c>) and appends
/// an inline thumbnail beneath each unique reference by injecting a
/// standard <c>![alt](data:...)</c> markdown line. The original path
/// text is preserved verbatim.
///
/// References inside <em>inline</em> backtick spans are still detected
/// (Copilot routinely formats file paths as <c>`path`</c>); only
/// fenced code blocks and existing markdown image syntax are skipped.
///
/// Files are loaded from disk and inlined as base64 data URIs. This
/// avoids all WebView2 virtual-host setup, multi-instance collisions,
/// and workspace-switch lifecycle hazards. Resolution rules:
/// <list type="bullet">
///   <item>Relative paths are resolved against the open workspace.</item>
///   <item>Absolute local paths are used as-is.</item>
///   <item>Remote URLs (<c>http://</c>, <c>https://</c>, <c>file://</c>)
///         are intentionally ignored — emitting them as &lt;img&gt;
///         tags would let the model fetch arbitrary external content
///         or expose unrelated files.</item>
///   <item>Files larger than <see cref="MaxFileBytes"/> or that fail
///         to read are silently skipped; nothing is injected.</item>
/// </list>
/// </summary>
internal static class ImageReferenceTransformer
{
	/// <summary>
	/// Maximum on-disk size of an image that will be inlined as a
	/// data URI. Larger files are skipped to keep the rendered DOM
	/// from ballooning. Big screenshots (~MB) fit comfortably; raw
	/// camera output usually does not.
	/// </summary>
	public const long MaxFileBytes = 4L * 1024 * 1024;

	private const string ImageExtensions = @"png|jpe?g|gif|webp|svg|bmp|ico|tiff?|avif";

	private static readonly Regex CodeFenceRegex = new(
		@"```[\s\S]*?```",
		RegexOptions.Compiled);

	private static readonly Regex MarkdownImageRegex = new(
		@"!\[[^\]]*\]\([^)]+\)",
		RegexOptions.Compiled);

	// Boundary-anchored: the reference must start at line-begin or after a
	// whitespace / quote / paren / bracket character, and end before
	// whitespace, sentence punctuation, quote, paren, or bracket. The
	// reference itself is an optional `@` or scheme/drive prefix followed
	// by a path of word/dot/dash segments separated by / or \, ending
	// with a recognised image extension.
	//
	// Group 3 optionally consumes a trailing backtick so that paths
	// written as `inline code` have the thumbnail injection land
	// AFTER the closing backtick (otherwise the injected paragraph
	// would be swallowed by the inline-code span).
	private static readonly Regex ImageReferenceRegex = new(
		@"(^|[\s(\[""'`])" +
		@"(@?(?:https?://|file:///?|[A-Za-z]:[\\/])?" +
		@"[\w.\-]+(?:[\\/][\w.\-]+)*\.(?:" + ImageExtensions + @"))" +
		@"(`?)" +
		@"(?=$|[\s)\]""'`,;:!?]|\.(?:\s|$))",
		RegexOptions.Compiled | RegexOptions.IgnoreCase);

	/// <summary>
	/// Returns <paramref name="content"/> with thumbnail-injection markdown
	/// appended after each unique image reference. References that don't
	/// resolve to a readable file under <see cref="MaxFileBytes"/> are
	/// left alone (no thumbnail is injected, but the original text is
	/// preserved).
	/// </summary>
	public static string Apply(string? content, string? workspaceRoot)
	{
		if (string.IsNullOrEmpty(content)) return content ?? "";

		// Step 1 — mask regions we must not rewrite (fenced code blocks +
		// existing markdown images). Inline `code` spans are intentionally
		// NOT masked: Copilot routinely formats file paths as `path`, and
		// we still want to detect those.
		var masks = new List<string>();
		string Mask(Match m)
		{
			var token = "\u0000KPMASK" + masks.Count + "\u0000";
			masks.Add(m.Value);
			return token;
		}

		var masked = CodeFenceRegex.Replace(content, Mask);
		masked = MarkdownImageRegex.Replace(masked, Mask);

		// Step 2 — detect image refs, dedupe per block (by resolved path),
		// and stash the encoded data URIs.
		var thumbs = new List<(string Reference, string DataUri)>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		masked = ImageReferenceRegex.Replace(masked, match =>
		{
			var reference = match.Groups[2].Value;
			var resolvedPath = ResolveDiskPath(reference, workspaceRoot);
			if (resolvedPath == null) return match.Value;
			if (!seen.Add(resolvedPath)) return match.Value;

			var dataUri = TryEncodeDataUri(resolvedPath);
			if (dataUri == null) return match.Value;

			thumbs.Add((reference, dataUri));
			return match.Value + "\u0000KPTHUMB" + (thumbs.Count - 1) + "\u0000";
		});

		// Step 3 — restore masked regions verbatim.
		masked = Regex.Replace(masked, @"\u0000KPMASK(\d+)\u0000",
			m => masks[int.Parse(m.Groups[1].Value)]);

		// Step 4 — materialize thumbnail markers as standalone markdown
		// image paragraphs so they render on their own line beneath the
		// original path text.
		masked = Regex.Replace(masked, @"\u0000KPTHUMB(\d+)\u0000", m =>
		{
			var t = thumbs[int.Parse(m.Groups[1].Value)];
			var alt = t.Reference.Replace("[", "").Replace("]", "");
			return "\n\n![" + alt + "](" + t.DataUri + ")\n\n";
		});

		return masked;
	}

	/// <summary>
	/// Maps a textual image reference to an absolute, existing path on
	/// disk. Returns null when the reference is a remote URL, a rooted
	/// path with no drive letter, a relative path with no workspace,
	/// or a path that simply doesn't exist.
	/// </summary>
	private static string? ResolveDiskPath(string reference, string? workspaceRoot)
	{
		var s = reference;
		if (s.Length > 0 && s[0] == '@') s = s.Substring(1);

		// Remote URLs and file:// URIs are intentionally not handled.
		if (Regex.IsMatch(s, @"^(https?|file)://", RegexOptions.IgnoreCase))
			return null;

		string candidate;
		var fwd = s.Replace('/', '\\');
		bool hasDrive = fwd.Length >= 2 && char.IsLetter(fwd[0]) && fwd[1] == ':';
		bool isAbsolute = hasDrive || (fwd.Length > 0 && fwd[0] == '\\');

		if (isAbsolute)
		{
			if (!hasDrive) return null;   // Bare \-rooted path is ambiguous on Windows.
			candidate = fwd;
		}
		else
		{
			if (string.IsNullOrEmpty(workspaceRoot)) return null;
			var rel = fwd.StartsWith(@".\") ? fwd.Substring(2) : fwd;
			candidate = Path.Combine(workspaceRoot, rel);
		}

		try
		{
			candidate = Path.GetFullPath(candidate);
			return File.Exists(candidate) ? candidate : null;
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// Reads <paramref name="path"/> from disk and returns a base64 data
	/// URI suitable for use as an &lt;img src&gt;. Returns null when the
	/// file is too large or cannot be read. Exposed as <c>internal</c>
	/// so other UI paths (e.g. the user-echo block for pasted images in
	/// <c>MainForm</c>) can reuse the same encoder without duplicating
	/// the size-cap and MIME logic.
	/// </summary>
	internal static string? TryEncodeDataUri(string path)
	{
		try
		{
			var info = new FileInfo(path);
			if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytes)
				return null;

			var mime = MimeFromExtension(info.Extension);
			var bytes = File.ReadAllBytes(path);
			return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
		}
		catch
		{
			return null;
		}
	}

	private static string MimeFromExtension(string extension) =>
		extension.ToLowerInvariant() switch
		{
			".png"            => "image/png",
			".jpg" or ".jpeg" => "image/jpeg",
			".gif"            => "image/gif",
			".webp"           => "image/webp",
			".svg"            => "image/svg+xml",
			".bmp"            => "image/bmp",
			".ico"            => "image/x-icon",
			".tif" or ".tiff" => "image/tiff",
			".avif"           => "image/avif",
			_                 => "application/octet-stream",
		};
}
