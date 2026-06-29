using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OllamaSharp;
using OllamaSharp.Models;

namespace GoPilot;

/// <summary>
/// What the local model decided to do with a prompt.
/// </summary>
internal enum LocalFilterMode
{
	/// <summary>Local model answered with high confidence; the cloud is skipped.</summary>
	Answered,
	/// <summary>Local model only compressed the prompt; forward it to the cloud.</summary>
	Minimized,
	/// <summary>Filter off or unavailable; caller forwards the original prompt.</summary>
	Bypassed,
}

internal sealed class LocalFilterResult
{
	public LocalFilterMode Mode        { get; init; }
	public string          Answer      { get; init; } = "";
	public string          Prompt      { get; init; } = "";
	public double          Confidence  { get; init; }
	public int             OriginalChars { get; init; }
	public int             FinalChars    { get; init; }
	public string          ModelLabel  { get; init; } = "";
	public string?         Note        { get; init; }
}

/// <summary>
/// Local-LLM intermediary that sits between the user and the cloud Copilot model.
/// For every prompt it either (a) answers directly when its self-reported
/// confidence clears the threshold, or (b) rewrites the prompt to the smallest
/// token set that preserves intent and forwards it to the cloud. Designed to be
/// fully best-effort: any failure degrades to <see cref="LocalFilterMode.Bypassed"/>
/// so the cloud always receives the original prompt.
/// </summary>
internal sealed class LocalFilterService
{
	public bool   Enabled   { get; set; }
	public string Endpoint  { get; set; } = "http://localhost:11434";
	public string Model     { get; set; } = "";   // empty => auto-selected from VRAM
	public double Threshold { get; set; } = 0.85;

	/// <summary>
	/// Directive prepended to every forwarded prompt so the cloud model answers
	/// tersely. Output tokens are the costliest line item, so trimming the
	/// response saves far more AIC than trimming the prompt. Kept short and code-safe.
	/// </summary>
	public const string ConciseDirective =
		"Respond concisely, directly, minimal tokens. No preamble, recap, or filler. " +
		"Keep code, paths, and commands intact.";

	public double VramGb       { get; private set; }
	public bool   Available    { get; private set; }
	public string StatusReason { get; private set; } = "not initialised";

	private OllamaApiClient? _client;

	/// <summary>
	/// Detects VRAM, picks a codellama model when none is configured, connects to
	/// Ollama, and verifies the model is installed. Never throws; sets
	/// <see cref="Available"/> and <see cref="StatusReason"/> instead.
	/// </summary>
	public async Task InitializeAsync(CancellationToken ct = default)
	{
		Available = false;
		try
		{
			VramGb = GpuDetector.DetectVramGb();
			if (string.IsNullOrWhiteSpace(Model))
				Model = GpuDetector.RecommendModel(VramGb);

			_client = new OllamaApiClient(new Uri(Endpoint)) { SelectedModel = Model };

			var models = await _client.ListLocalModelsAsync(ct);
			var present = models.Any(m =>
				string.Equals(m.Name, Model, StringComparison.OrdinalIgnoreCase)
				|| m.Name.StartsWith(Model + ":", StringComparison.OrdinalIgnoreCase)
				|| Model.StartsWith(m.Name, StringComparison.OrdinalIgnoreCase));

			if (!present)
			{
				StatusReason = $"model '{Model}' not installed (run: ollama pull {Model})";
				return;
			}

			Available = true;
			StatusReason = $"ready ({Model}, {VramGb:0.#} GB VRAM)";
		}
		catch (Exception ex)
		{
			StatusReason = $"unavailable: {ex.Message}";
		}
	}

	/// <summary>
	/// Runs the prompt through the local model. Returns Bypassed if the filter is
	/// off, unavailable, or anything goes wrong so the caller forwards as-is.
	/// </summary>
	public async Task<LocalFilterResult> ProcessAsync(string prompt, IReadOnlyList<(string Name, string Content)>? files, CancellationToken ct = default)
	{
		var original = prompt?.Length ?? 0;
		if (!Enabled || !Available || _client == null || string.IsNullOrWhiteSpace(prompt))
			return new LocalFilterResult { Mode = LocalFilterMode.Bypassed, Prompt = prompt ?? "", OriginalChars = original, FinalChars = original };

		try
		{
			var fileBlock = new StringBuilder();
			if (files != null)
			{
				foreach (var f in files)
				{
					fileBlock.Append("\n--- FILE: ").Append(f.Name).Append(" ---\n").Append(f.Content);
				}
			}
			var hasFiles = fileBlock.Length > 0;

			var instruction =
				"You are a local pre-processor between a user and a powerful cloud LLM. " +
				"Reply with ONLY a single JSON object, no prose, shaped: " +
				"{\"confidence\":0.0-1.0,\"answer\":\"\",\"minimized\":\"\"}. " +
				"If you can fully and correctly answer the request yourself, set answer to that answer and confidence to your certainty. " +
				(hasFiles
					? "The attached file contents are provided below; use them to answer if sufficient. "
					: "If unsure or the task needs codebase/tool access, leave answer empty and set confidence low. ") +
				"Always set minimized to the prompt rewritten to the fewest tokens that preserve full intent (keep code, paths, names verbatim). " +
				(hasFiles ? "ATTACHED FILES:" + fileBlock + "\n" : "") +
				"USER PROMPT:\n" + prompt;

			var sb = new StringBuilder();
			var req = new GenerateRequest
			{
				Model = Model,
				Prompt = instruction,
				Stream = true,
				Format = "json",
				Options = new RequestOptions { Temperature = 0f },
			};
			await foreach (var chunk in _client.GenerateAsync(req).WithCancellation(ct))
				sb.Append(chunk?.Response);

			var (confidence, answer, minimized) = ParseJson(sb.ToString());
			var fwd = string.IsNullOrWhiteSpace(minimized) ? prompt : minimized.Trim();

			if (confidence >= Threshold && !string.IsNullOrWhiteSpace(answer))
			{
				return new LocalFilterResult
				{
					Mode = LocalFilterMode.Answered,
					Answer = answer.Trim(),
					Confidence = confidence,
					OriginalChars = original,
					FinalChars = answer.Trim().Length,
					ModelLabel = Model,
				};
			}

			string reason;
			if (confidence <= 0 && string.IsNullOrWhiteSpace(answer))
				reason = "no parseable local reply";
			else if (string.IsNullOrWhiteSpace(answer))
				reason = $"no local answer (conf {confidence:0.00}), needs cloud";
			else
				reason = $"low confidence {confidence:0.00} < {Threshold:0.00}";

			return new LocalFilterResult
			{
				Mode = LocalFilterMode.Minimized,
				Prompt = ConciseDirective + "\n\n" + fwd,
				Confidence = confidence,
				OriginalChars = original,
				FinalChars = fwd.Length,
				ModelLabel = Model,
				Note = reason,
			};
		}
		catch (Exception ex)
		{
			return new LocalFilterResult { Mode = LocalFilterMode.Bypassed, Prompt = prompt, OriginalChars = original, FinalChars = original, Note = ex.Message };
		}
	}

	private static (double confidence, string answer, string minimized) ParseJson(string raw)
	{
		var s = raw.IndexOf('{');
		var e = raw.LastIndexOf('}');
		if (s < 0 || e <= s) return (0, "", "");
		try
		{
			using var doc = JsonDocument.Parse(raw.Substring(s, e - s + 1));
			var root = doc.RootElement;
			double conf = 0;
			if (root.TryGetProperty("confidence", out var c))
			{
				if (c.ValueKind == JsonValueKind.Number) conf = c.GetDouble();
				else if (c.ValueKind == JsonValueKind.String && double.TryParse(c.GetString(), out var pc)) conf = pc;
			}
			var ans = root.TryGetProperty("answer", out var a) ? (a.GetString() ?? "") : "";
			var min = root.TryGetProperty("minimized", out var m) ? (m.GetString() ?? "") : "";
			return (conf, ans, min);
		}
		catch { return (0, "", ""); }
	}
}
