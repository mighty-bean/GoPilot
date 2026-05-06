using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace GoPilot;

/// <summary>
/// Reduces a user prompt to a "caveman" token-minimal form before it is sent to the model.
///
/// Goals:
///   * Strip filler / pleasantry / function words that rarely affect intent.
///   * Preserve meaning-critical tokens (negations, code, references, URLs).
///   * Be deterministic and dependency-free so it can be unit tested in isolation.
///
/// Rules applied (aggressive set):
///   1. Normalize CRLF/CR to LF.
///   2. Pull protected spans out of the text (fenced code blocks, inline backticks,
///      @references, URLs) and substitute placeholders so they are never mangled.
///   3. Tokenize the remaining prose by whitespace.
///   4. Drop stopwords (articles, auxiliaries, modals, fillers, pleasantries,
///      sentence-initial subject pronouns "I"/"we").
///   5. Always preserve negations ("not", "no", "never", "don't", contractions).
///   6. Collapse "do/does/did not" / "do/does/did n't" to a single "not".
///   7. Drop -ly adverbs (very, really, basically, ...).
///   8. Spell digits one..ten as 1..10.
///   9. Strip commas, semicolons, em/en dashes, decorative emoji.
///  10. Restore protected spans.
///  11. Collapse multiple spaces, drop empty lines, trim each line.
/// </summary>
internal static class CavemanTransformer
{
	// Function words that rarely carry meaning. Whole-word match, case-insensitive.
	// Negations ("not", "no", "never", "none", "nothing") are intentionally absent.
	private static readonly HashSet<string> Stopwords = new(System.StringComparer.OrdinalIgnoreCase)
	{
		// Articles and determiners
		"a", "an", "the",
		// Prepositions called out by the spec
		"of", "in", "on", "at",
		// Auxiliaries and modals
		"is", "are", "was", "were", "be", "been", "being",
		"will", "would", "could", "should", "shall", "may", "might", "must",
		"do", "does", "did",
		"have", "has", "had",
		// Filler words
		"very", "really", "so", "just", "like", "uh", "um", "ah", "er", "well",
		// Pleasantries / preambles (single tokens; multi-word patterns handled separately)
		"hi", "hello", "hey", "thanks", "please", "cheers",
		"sorry",
		// Sentence-initial subjects (will be removed via dedicated pass to avoid
		// stripping mid-sentence "I" inside e.g. "If I could...". See below.)
	};

	// Multi-word phrases collapsed to empty before tokenization. Order matters:
	// longer patterns first so "thank you very much" beats "thank you".
	private static readonly (Regex Pattern, string Replacement)[] PhrasePatterns = new[]
	{
		(new Regex(@"\bthank\s+you\s+very\s+much\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		(new Regex(@"\bthank\s+you\b",                RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		(new Regex(@"\byou'?re\s+welcome\b",          RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		(new Regex(@"\bi'?m\s+sorry\b",               RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		(new Regex(@"\bi\s+mean\b",                   RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		(new Regex(@"\byou\s+know\b",                 RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		(new Regex(@"\bkind\s+of\b",                  RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		(new Regex(@"\bsort\s+of\b",                  RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		(new Regex(@"\bgoing\s+to\b",                 RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		(new Regex(@"\bable\s+to\b",                  RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		// Dummy / expletive "it" before a copula or stative verb. Drops only the
		// pronoun; the trailing "is"/"was" is removed later by the stopword pass,
		// while "seems"/"appears" survive because they carry meaning.
		// The contraction form requires a mandatory apostrophe so that the
		// possessive "its" (e.g. "its config file") is left untouched.
		(new Regex(@"\bit's\b",                                 RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		(new Regex(@"\bit\s+(?=(?:is|was|seems|appears)\b)",    RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		// A3 - polite-clause openers. Run BEFORE the negation collapse below so
		// that "if you don't mind" gets eaten as one idiom rather than having its
		// "don't" converted to "not" first. "Please" / "Kindly" are handled by
		// the single-word stopword pass / -ly drop.
		(new Regex(@"\bif\s+you\s+don'?t\s+mind\b",      RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		(new Regex(@"\bif\s+you\s+could\b",              RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		(new Regex(@"\bif\s+you\s+would\b",              RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		(new Regex(@"\b(?:could|can|would|will)\s+you\s+please\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		(new Regex(@"\b(?:could|can|would|will)\s+you\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		// A4 - hedges. Negation-bearing forms ("I don't think") are preserved by
		// running this rule after the one above only catches affirmative hedges.
		(new Regex(@"\bi\s+(?:think|believe|guess|feel|suppose|reckon)\s+that\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		(new Regex(@"\bi\s+(?:think|believe|guess|feel|suppose|reckon)\b",       RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		(new Regex(@"\bin\s+my\s+opinion\b",             RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		(new Regex(@"\b(?:maybe|perhaps|possibly|probably|somewhat|presumably|arguably)\b",
		           RegexOptions.IgnoreCase | RegexOptions.Compiled), ""),
		// "do/does/did not" / "do/does/did n't" -> "not"
		(new Regex(@"\b(?:do|does|did)\s*n[o']t\b",   RegexOptions.IgnoreCase | RegexOptions.Compiled), "not"),
		(new Regex(@"\b(?:do|does|did)\s+not\b",      RegexOptions.IgnoreCase | RegexOptions.Compiled), "not"),
	};

	// Spell-out digits one..ten -> 1..10. Whole-word, case-insensitive.
	private static readonly (Regex Pattern, string Replacement)[] DigitPatterns = new[]
	{
		(new Regex(@"\bone\b",   RegexOptions.IgnoreCase | RegexOptions.Compiled),  "1"),
		(new Regex(@"\btwo\b",   RegexOptions.IgnoreCase | RegexOptions.Compiled),  "2"),
		(new Regex(@"\bthree\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),  "3"),
		(new Regex(@"\bfour\b",  RegexOptions.IgnoreCase | RegexOptions.Compiled),  "4"),
		(new Regex(@"\bfive\b",  RegexOptions.IgnoreCase | RegexOptions.Compiled),  "5"),
		(new Regex(@"\bsix\b",   RegexOptions.IgnoreCase | RegexOptions.Compiled),  "6"),
		(new Regex(@"\bseven\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),  "7"),
		(new Regex(@"\beight\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),  "8"),
		(new Regex(@"\bnine\b",     RegexOptions.IgnoreCase | RegexOptions.Compiled),  "9"),
		(new Regex(@"\bten\b",      RegexOptions.IgnoreCase | RegexOptions.Compiled),  "10"),
		// A8 - extend through twenty plus the common multipliers. We deliberately
		// do not try to fold "two hundred" into 200 etc.; the user can write
		// digits directly when they need precise numbers.
		(new Regex(@"\beleven\b",   RegexOptions.IgnoreCase | RegexOptions.Compiled),  "11"),
		(new Regex(@"\btwelve\b",   RegexOptions.IgnoreCase | RegexOptions.Compiled),  "12"),
		(new Regex(@"\bthirteen\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),  "13"),
		(new Regex(@"\bfourteen\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),  "14"),
		(new Regex(@"\bfifteen\b",  RegexOptions.IgnoreCase | RegexOptions.Compiled),  "15"),
		(new Regex(@"\bsixteen\b",  RegexOptions.IgnoreCase | RegexOptions.Compiled),  "16"),
		(new Regex(@"\bseventeen\b",RegexOptions.IgnoreCase | RegexOptions.Compiled),  "17"),
		(new Regex(@"\beighteen\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),  "18"),
		(new Regex(@"\bnineteen\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),  "19"),
		(new Regex(@"\btwenty\b",   RegexOptions.IgnoreCase | RegexOptions.Compiled),  "20"),
		(new Regex(@"\bthirty\b",   RegexOptions.IgnoreCase | RegexOptions.Compiled),  "30"),
		(new Regex(@"\bforty\b",    RegexOptions.IgnoreCase | RegexOptions.Compiled),  "40"),
		(new Regex(@"\bfifty\b",    RegexOptions.IgnoreCase | RegexOptions.Compiled),  "50"),
		(new Regex(@"\bsixty\b",    RegexOptions.IgnoreCase | RegexOptions.Compiled),  "60"),
		(new Regex(@"\bseventy\b",  RegexOptions.IgnoreCase | RegexOptions.Compiled),  "70"),
		(new Regex(@"\beighty\b",   RegexOptions.IgnoreCase | RegexOptions.Compiled),  "80"),
		(new Regex(@"\bninety\b",   RegexOptions.IgnoreCase | RegexOptions.Compiled),  "90"),
		(new Regex(@"\bhundred\b",  RegexOptions.IgnoreCase | RegexOptions.Compiled),  "100"),
		(new Regex(@"\bthousand\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),  "1000"),
		(new Regex(@"\bmillion\b",  RegexOptions.IgnoreCase | RegexOptions.Compiled),  "1000000"),
		(new Regex(@"\bbillion\b",  RegexOptions.IgnoreCase | RegexOptions.Compiled),  "1000000000"),
	};

	// A7 - trailing punctuation runs. Two or more of [.!?] in a row collapse to
	// a single character. If the run contains '?' the result is '?' (preserves
	// the question), otherwise '.'. Standalone single punctuation is left alone.
	private static readonly Regex TrailingPunctRun = new(@"[.!?]{2,}", RegexOptions.Compiled);

	// Protected-span detection. Order matters: fenced blocks first so their inner
	// triple-backticks are not re-matched as inline code.
	private static readonly Regex FencedCode  = new(@"```[\s\S]*?```",         RegexOptions.Compiled);
	private static readonly Regex InlineCode  = new(@"`[^`\r\n]+`",            RegexOptions.Compiled);
	private static readonly Regex Reference   = new(@"@[\w\-./:\\]+",          RegexOptions.Compiled);
	private static readonly Regex Url         = new(@"https?://\S+",           RegexOptions.Compiled);

	// -ly adverbs to drop. Whitelist a few that are usually meaning-bearing
	// ("only", "early", "likely") so they survive.
	private static readonly HashSet<string> LyKeep = new(System.StringComparer.OrdinalIgnoreCase)
	{
		"only", "early", "likely", "ugly", "family", "apply", "reply",
		"supply", "imply", "rely", "belly", "jelly",
	};
	private static readonly Regex LyWord = new(@"\b[a-z]{3,}ly\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	// Decorative punctuation/whitespace cleanups.
	private static readonly Regex Commas       = new(@"[,;]+",          RegexOptions.Compiled);
	private static readonly Regex Dashes       = new(@"\s+[-\u2013\u2014]+\s+", RegexOptions.Compiled);
	private static readonly Regex MultiSpace   = new(@"[ \t]+",         RegexOptions.Compiled);
	private static readonly Regex MultiBlank   = new(@"\n{2,}",         RegexOptions.Compiled);

	// Sentence-initial subject pronouns to drop. Applied per-line after stopword pass.
	private static readonly Regex LeadingSubject = new(
		@"^\s*(?:i|we|i'?ll|i'?m|we'?ll|we'?re)\s+",
		RegexOptions.IgnoreCase | RegexOptions.Compiled);

	/// <summary>
	/// Apply the full caveman transform. Returns the input unchanged if it is
	/// null, empty, or whitespace-only.
	/// </summary>
	public static string Transform(string input)
	{
		if (string.IsNullOrWhiteSpace(input)) return input ?? string.Empty;

		// 1. Normalize line endings to LF for processing.
		var text = input.Replace("\r\n", "\n").Replace("\r", "\n");

		// C3 - per-line "no-touch" marker. Lines whose first non-whitespace run
		// is "!!" are passed through verbatim (with the marker stripped). We pull
		// them out here, replace each with a placeholder line, run the rest of
		// the transform, then splice the originals back.
		var verbatimLines = new List<string>();
		text = ExtractVerbatimLines(text, verbatimLines);

		// 2. Extract protected spans (fenced code, inline code, @refs, urls).
		var protectedSpans = new List<string>();
		text = ProtectSpans(text, FencedCode, protectedSpans);
		text = ProtectSpans(text, InlineCode, protectedSpans);
		text = ProtectSpans(text, Reference,  protectedSpans);
		text = ProtectSpans(text, Url,        protectedSpans);

		// 3. Multi-word phrase collapses (must run before stopword tokenization).
		foreach (var (pattern, replacement) in PhrasePatterns)
			text = pattern.Replace(text, replacement);

		// 4. Drop -ly adverbs (with whitelist).
		text = LyWord.Replace(text, m => LyKeep.Contains(m.Value) ? m.Value : "");

		// 5. Spell-out digits one..ten -> 1..10 (and the A8 extensions).
		foreach (var (pattern, replacement) in DigitPatterns)
			text = pattern.Replace(text, replacement);

		// 6. Strip commas, semicolons, em/en dashes between words.
		text = Commas.Replace(text, "");
		text = Dashes.Replace(text, " ");

		// A7 - collapse runs of trailing punctuation. "?!?" / "!!!" / "..." -> a
		// single '?' if the run includes a question mark, otherwise '.'.
		text = TrailingPunctRun.Replace(text, m => m.Value.Contains('?') ? "?" : ".");

		// 7. Strip decorative emoji (any non-ASCII char above U+007F).
		text = StripNonAscii(text);

		// 8. Tokenize and drop single-word stopwords (negations preserved).
		var lines = text.Split('\n');
		for (int i = 0; i < lines.Length; i++)
		{
			lines[i] = StripStopwords(lines[i]);
			lines[i] = LeadingSubject.Replace(lines[i], "");
			lines[i] = MultiSpace.Replace(lines[i], " ").Trim();

			// Drop lines whose remaining content is only punctuation / whitespace
			// (e.g. "Thanks!" -> "!" after "thanks" is stripped). Verbatim-line
			// placeholders contain U+0002 and survive this check.
			if (lines[i].Length > 0
				&& !lines[i].Contains('\u0001')
				&& !lines[i].Contains('\u0002')
				&& !ContainsLetterOrDigit(lines[i]))
			{
				lines[i] = string.Empty;
			}
		}
		text = string.Join("\n", lines);

		// 9. Restore protected spans.
		text = RestoreSpans(text, protectedSpans);

		// C3 - restore verbatim no-touch lines.
		text = RestoreVerbatimLines(text, verbatimLines);

		// 10. Collapse runs of blank lines, trim outer whitespace.
		text = MultiBlank.Replace(text, "\n\n").Trim();

		return text;
	}

	/// <summary>
	/// Reduction stats for a single Transform call. Used by the UI to show a
	/// before/after meter ("Caveman: 142 -&gt; 87 chars, -39%").
	/// </summary>
	internal readonly record struct Stats(int OriginalChars, int CavemanChars)
	{
		public int Saved      => OriginalChars - CavemanChars;
		public double Percent => OriginalChars == 0 ? 0.0 : (double)Saved / OriginalChars;
	}

	/// <summary>
	/// Convenience wrapper that runs <see cref="Transform"/> and returns the
	/// reduction stats alongside the rewritten text.
	/// </summary>
	public static (string Text, int OriginalChars, int CavemanChars) TransformWithStats(string input)
	{
		var original = input ?? string.Empty;
		var output = Transform(original);
		return (output, original.Length, output.Length);
	}

	private static string ProtectSpans(string text, Regex pattern, List<string> store)
	{
		return pattern.Replace(text, m =>
		{
			var token = $"\u0001KP{store.Count}\u0001";
			store.Add(m.Value);
			return token;
		});
	}

	private static string RestoreSpans(string text, List<string> store)
	{
		for (int i = 0; i < store.Count; i++)
			text = text.Replace($"\u0001KP{i}\u0001", store[i]);
		return text;
	}

	// C3 - "no-touch" line marker. Any line whose first non-whitespace characters
	// are "!!" is left exactly as the user typed it (with the marker stripped).
	// We swap such lines for placeholders before any other rule runs and put them
	// back at the end.
	private static readonly Regex VerbatimMarker = new(@"^[ \t]*!![ \t]?", RegexOptions.Compiled);

	private static string ExtractVerbatimLines(string text, List<string> store)
	{
		var lines = text.Split('\n');
		for (int i = 0; i < lines.Length; i++)
		{
			var m = VerbatimMarker.Match(lines[i]);
			if (!m.Success) continue;

			// Strip the marker (and one optional space after it) from the saved line.
			store.Add(lines[i].Substring(m.Length));
			lines[i] = $"\u0002KV{store.Count - 1}\u0002";
		}
		return string.Join("\n", lines);
	}

	private static string RestoreVerbatimLines(string text, List<string> store)
	{
		for (int i = 0; i < store.Count; i++)
			text = text.Replace($"\u0002KV{i}\u0002", store[i]);
		return text;
	}

	private static string StripNonAscii(string text)
	{
		var sb = new StringBuilder(text.Length);
		foreach (var ch in text)
		{
			if (ch <= 0x7F || ch == '\u0001' || ch == '\u0002') sb.Append(ch);
		}
		return sb.ToString();
	}

	private static bool ContainsLetterOrDigit(string s)
	{
		foreach (var ch in s)
		{
			if (char.IsLetterOrDigit(ch)) return true;
		}
		return false;
	}

	private static string StripStopwords(string line)
	{
		if (string.IsNullOrWhiteSpace(line)) return line;

		var sb = new StringBuilder(line.Length);
		int i = 0;
		while (i < line.Length)
		{
			// Pass through whitespace.
			if (char.IsWhiteSpace(line[i]))
			{
				sb.Append(line[i++]);
				continue;
			}

			// Read the next "word" run of letters/apostrophes; everything else
			// (punctuation, placeholders) passes through unchanged.
			int start = i;
			while (i < line.Length && (char.IsLetter(line[i]) || line[i] == '\''))
				i++;

			if (i == start)
			{
				sb.Append(line[i++]);
				continue;
			}

			var word = line.Substring(start, i - start);
			if (Stopwords.Contains(word))
			{
				// Drop. Leave surrounding whitespace; a later MultiSpace pass collapses it.
				continue;
			}
			sb.Append(word);
		}
		return sb.ToString();
	}
}
