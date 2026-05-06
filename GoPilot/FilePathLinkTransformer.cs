using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GoPilot;

/// <summary>
/// Detects bare file-path references in Copilot's assistant markdown
/// (e.g. <c>MainForm.cs</c>, <c>src/auth/index.ts</c>,
/// <c>D:\path\to\bar.cs</c>) and rewrites each occurrence as a standard
/// markdown link that targets a custom URL scheme:
///
/// <code>[path](kp-path:url-encoded-resolved-path)</code>
///
/// Using markdown link syntax (rather than raw inline HTML) means the
/// link survives every CommonMark context that <c>marked.js</c> renders:
/// table cells, blockquotes, list items, etc. The previous approach
/// emitted an <c>&lt;a class="kp-path-link"&gt;</c> tag directly, which
/// occasionally leaked through as visible escaped text whenever the
/// surrounding markdown context made <c>marked.js</c> treat raw HTML as
/// literal characters.
///
/// The WebView2 click handler in <c>output.js</c> recognises any anchor
/// whose <c>href</c> starts with <c>kp-path:</c> and dispatches the same
/// open / reveal actions as before. The visible link text is preserved
/// verbatim so the rendered transcript reads exactly as Copilot wrote it.
///
/// References inside fenced code blocks, existing markdown link syntax
/// (<c>[text](url)</c>), markdown image syntax (<c>![alt](url)</c>), and
/// inline HTML anchors (<c>&lt;a&gt;...&lt;/a&gt;</c>) are intentionally
/// left untouched. Paths with image extensions are still wrapped as
/// links so the user can click the path text to open the file --
/// <see cref="ImageReferenceTransformer"/> independently appends an
/// inline thumbnail beneath the same line. The two treatments compose:
/// the path text becomes a clickable link AND a preview is shown.
///
/// Resolution rules mirror <see cref="ImageReferenceTransformer"/>:
/// <list type="bullet">
///   <item>Relative paths are resolved against the open workspace.</item>
///   <item>Absolute Windows paths (with drive letter) are used as-is.</item>
///   <item>Bare-rooted paths (<c>\foo</c>) are skipped as ambiguous.</item>
///   <item>Remote URLs (<c>http://</c>, <c>https://</c>, <c>file://</c>)
///         are skipped to avoid spoofed link wrapping.</item>
///   <item>References that don't resolve to an existing file on disk
///         are left as plain text.</item>
/// </list>
/// </summary>
internal static class FilePathLinkTransformer
{
	private static readonly Regex CodeFenceRegex = new(
		@"```[\s\S]*?```",
		RegexOptions.Compiled);

	// Mask both [text](url) and ![alt](url) so we don't relabel the visible
	// text or rewrite an already-targeted URL.
	private static readonly Regex MarkdownLinkRegex = new(
		@"!?\[[^\]]*\]\([^)]+\)",
		RegexOptions.Compiled);

	// Mask raw HTML anchor elements (some models emit HTML directly,
	// and our own previously-generated anchors must be skipped if this
	// transformer is ever chained over its own output).
	private static readonly Regex HtmlAnchorRegex = new(
		@"<a\b[^>]*>[\s\S]*?</a>",
		RegexOptions.Compiled | RegexOptions.IgnoreCase);

	// Boundary-anchored path regex. Same boundary set as the image
	// transformer so we behave consistently when paths appear inside
	// inline backtick spans, parens, or quotes. The path itself is:
	//
	//   - optional '@' prefix
	//   - optional Windows drive root  ([A-Za-z]:[\/])
	//   - one or more name segments separated by / or \
	//   - each segment is word + dot + dash characters
	//   - the final segment must contain at least one '.' (extension)
	//     OR the path must contain at least one separator. This keeps
	//     us from linking every word that happens to look like an
	//     identifier; "MainForm.cs" still matches via the extension.
	//
	// Trailing line / column suffixes (":42", ":42:7") are intentionally
	// excluded from the match so they remain visible after the anchor
	// closes; the file itself still opens.
	private static readonly Regex PathReferenceRegex = new(
		@"(^|[\s(\[""'`])" +
		@"(@?(?:[A-Za-z]:[\\/])?" +
		@"[\w\-]+(?:\.[\w\-]+)*" +
		@"(?:[\\/][\w.\-]+)*" +
		@")" +
		@"(`?)" +
		@"(?=$|[\s)\]""'`,;:!?]|\.(?:\s|$))",
		RegexOptions.Compiled);

	// Single-backtick inline code span. We only match the simple
	// non-empty form on a single line. Double-backtick (`` `` ``) and
	// multi-line spans are intentionally ignored — they're rare for
	// path references and the simple form covers every case Copilot
	// emits in practice (`path/to/file.cs`).
	private static readonly Regex InlineCodeSpanRegex = new(
		@"`([^`\r\n]+)`",
		RegexOptions.Compiled);

	/// <summary>
	/// Returns <paramref name="content"/> with each resolvable file path
	/// rewritten as a markdown link of the form
	/// <c>[path](kp-path:url-encoded-resolved-path)</c>. Paths that don't
	/// resolve, sit inside masked regions, or have an image extension
	/// are left as plain text.
	///
	/// Inline code spans wrapping a single path (<c>`path/to/file.cs`</c>)
	/// get a special treatment: the entire span is rewritten as
	/// <c>[`path`](kp-path:...)</c> so the link wraps the
	/// <c>&lt;code&gt;</c> element in the rendered DOM. Without this,
	/// the markdown link would land inside the code span and render
	/// literally as text.
	/// </summary>
	public static string Apply(string? content, string? workspaceRoot)
	{
		if (string.IsNullOrEmpty(content)) return content ?? "";

		var masks = new List<string>();
		string Mask(Match m)
		{
			var token = "\u0000KPLNKMASK" + masks.Count + "\u0000";
			masks.Add(m.Value);
			return token;
		}

		var masked = CodeFenceRegex.Replace(content, Mask);
		masked = MarkdownLinkRegex.Replace(masked, Mask);
		masked = HtmlAnchorRegex.Replace(masked, Mask);

		// Process inline code spans BEFORE the plain-text path pass so
		// (a) we can lift a single-path code span into a code-link and
		// (b) every other code span is masked off, keeping the plain-
		// text pass from injecting markdown into a literal code region.
		masked = InlineCodeSpanRegex.Replace(masked, m =>
		{
			var inner = m.Groups[1].Value;
			var trimmed = inner.Trim();

			// A single ']' would terminate the link's visible text
			// early; bail out and mask the span verbatim.
			if (trimmed.Length == 0 || trimmed.IndexOf(']') >= 0)
				return Mask(m);

			if (!HasSeparator(trimmed) && !HasExtension(trimmed))
				return Mask(m);

			var resolvedPath = ResolveDiskPath(trimmed, workspaceRoot);
			if (resolvedPath == null) return Mask(m);

			// Backticks insulate the visible text from markdown
			// interpretation, so the inner is emitted verbatim
			// (no escape pass) inside the wrapping link. The whole
			// produced link is masked off so the plain-text pass
			// below doesn't re-process the path inside its visible
			// text portion.
			var link = "[`" + inner + "`](kp-path:"
				+ Uri.EscapeDataString(resolvedPath) + ")";
			var token = "\u0000KPLNKMASK" + masks.Count + "\u0000";
			masks.Add(link);
			return token;
		});

		masked = PathReferenceRegex.Replace(masked, match =>
		{
			var leading   = match.Groups[1].Value;
			var reference = match.Groups[2].Value;
			var trailing  = match.Groups[3].Value;

			// Reject obvious non-paths early. A reference must either
			// have a real path separator OR a non-image extension;
			// otherwise plain words like "Hello" or "TODO" would be
			// resolved against the workspace root just to be rejected.
			if (!HasSeparator(reference) && !HasExtension(reference))
				return match.Value;

			var resolvedPath = ResolveDiskPath(reference, workspaceRoot);
			if (resolvedPath == null) return match.Value;

			return leading + BuildMarkdownLink(reference, resolvedPath) + trailing;
		});

		masked = Regex.Replace(masked, @"\u0000KPLNKMASK(\d+)\u0000",
			m => masks[int.Parse(m.Groups[1].Value)]);

		return masked;
	}

	/// <summary>
	/// Maps a textual file reference to an absolute, existing path on
	/// disk. Returns null when the reference is a remote URL, a rooted
	/// path with no drive letter, a relative path with no workspace,
	/// or a path that simply doesn't exist.
	/// </summary>
	private static string? ResolveDiskPath(string reference, string? workspaceRoot)
	{
		var s = reference;
		if (s.Length > 0 && s[0] == '@') s = s.Substring(1);

		if (Regex.IsMatch(s, @"^(https?|file)://", RegexOptions.IgnoreCase))
			return null;

		string candidate;
		var fwd = s.Replace('/', '\\');
		bool hasDrive = fwd.Length >= 2 && char.IsLetter(fwd[0]) && fwd[1] == ':';
		bool isAbsolute = hasDrive || (fwd.Length > 0 && fwd[0] == '\\');

		if (isAbsolute)
		{
			if (!hasDrive) return null;
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

	private static bool HasSeparator(string reference) =>
		reference.IndexOf('/') >= 0 || reference.IndexOf('\\') >= 0;

	private static bool HasExtension(string reference)
	{
		// Path.GetExtension would throw on some inputs; do it manually.
		var dot = reference.LastIndexOf('.');
		if (dot < 0 || dot == reference.Length - 1) return false;
		// Avoid matching things like ".." or "Mr."
		for (int i = dot + 1; i < reference.Length; i++)
		{
			var c = reference[i];
			if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_'))
				return false;
		}
		return true;
	}

	/// <summary>
	/// Builds a CommonMark link of the form
	/// <c>[visibleText](kp-path:encodedPath)</c>. The visible text is
	/// escaped against markdown specials so backslashes (Windows path
	/// separators) and other punctuation render literally, and the URL
	/// is fully percent-encoded so it can never carry an unescaped
	/// space, paren, or quote that would confuse the link parser.
	/// </summary>
	private static string BuildMarkdownLink(string visibleText, string resolvedPath)
	{
		var sb = new StringBuilder(visibleText.Length + 64);
		sb.Append('[');
		AppendMarkdownTextEscaped(sb, visibleText);
		sb.Append("](kp-path:");
		sb.Append(Uri.EscapeDataString(resolvedPath));
		sb.Append(')');
		return sb.ToString();
	}

	/// <summary>
	/// Escapes the characters that CommonMark treats as having syntactic
	/// meaning inside a link's text portion. Backslash is escaped first
	/// so we don't double-process the slashes we introduce for the other
	/// specials.
	/// </summary>
	private static void AppendMarkdownTextEscaped(StringBuilder sb, string value)
	{
		foreach (var c in value)
		{
			switch (c)
			{
				case '\\': sb.Append("\\\\"); break;
				case '[':  sb.Append("\\[");  break;
				case ']':  sb.Append("\\]");  break;
				case '`':  sb.Append("\\`");  break;
				case '*':  sb.Append("\\*");  break;
				case '_':  sb.Append("\\_");  break;
				default:   sb.Append(c);      break;
			}
		}
	}
}
