using System;
using System.Collections.Generic;

namespace GoPilot.SkillCatalog;

/// <summary>
/// Tiny YAML front-matter reader that mirrors the behaviour already used by
/// <c>CopilotService.ParseAgentFile</c> / <c>ParsePromptFile</c> /
/// <c>ParseSkillFile</c>: a leading <c>---\n</c> block, terminated by
/// <c>\n---\n</c>, with simple <c>key: value</c> pairs (quoted or not).
/// We only need the small subset of fields used to populate
/// <see cref="CatalogItem"/> -- <c>name</c> and <c>description</c> -- so this
/// is a deliberately minimal parser, not a real YAML implementation.
/// </summary>
internal static class FrontMatter
{
	/// <summary>
	/// Returns a case-insensitive <see cref="Dictionary{TKey,TValue}"/> of the
	/// scalar key/value pairs in the leading YAML front-matter block, or an
	/// empty dictionary when the file has no front matter. List values
	/// (e.g. <c>tools:</c>) are ignored.
	/// </summary>
	public static Dictionary<string, string> Parse(string fileContent)
	{
		var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (string.IsNullOrEmpty(fileContent)) return result;

		var content = fileContent.Replace("\r\n", "\n");
		if (!content.StartsWith("---\n", StringComparison.Ordinal)) return result;

		var end = content.IndexOf("\n---\n", 4, StringComparison.Ordinal);
		if (end < 0) return result;

		var block = content[4..end];

		foreach (var rawLine in block.Split('\n'))
		{
			var line = rawLine.TrimEnd();
			if (string.IsNullOrEmpty(line)) continue;

			if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("  - ", StringComparison.Ordinal))
			{
				// Continuation of a previous list value -- skip.
				continue;
			}

			var col = line.IndexOf(':');
			if (col < 0) continue;

			var key = line[..col].Trim();
			var val = line[(col + 1)..].Trim();

			// Empty value (e.g. "tools:") opens a list block whose entries we skip
			// via the "- " check above, so we just drop the key here.
			if (val.Length == 0) continue;

			if ((val.StartsWith('"') && val.EndsWith('"') && val.Length >= 2)
				|| (val.StartsWith('\'') && val.EndsWith('\'') && val.Length >= 2))
			{
				val = val[1..^1];
			}

			result[key] = val;
		}

		return result;
	}
}
