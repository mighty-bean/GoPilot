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

			_client = new OllamaApiClient(new Uri(Endpoint)) { SelectedModel = Model };

			var models = (await _client.ListLocalModelsAsync(ct)).ToList();

			// Auto-select a model when none is configured. The VRAM heuristic
			// describes THIS machine, which is wrong when Endpoint points at
			// another host on the network, so prefer a model actually installed
			// on the target: use the VRAM-recommended id if present, else fall
			// back to any installed codellama, else keep the recommendation so
			// the "not installed" branch can hint at the right pull command.
			if (string.IsNullOrWhiteSpace(Model))
			{
				var reco = GpuDetector.RecommendModel(VramGb);
				if (models.Any(m => NameMatches(m.Name, reco)))
					Model = reco;
				else
				{
					var installed = models.FirstOrDefault(m =>
						m.Name.IndexOf("codellama", StringComparison.OrdinalIgnoreCase) >= 0);
					Model = installed?.Name ?? reco;
				}
				_client.SelectedModel = Model;
			}

			var host = SafeHost(Endpoint);
			var present = models.Any(m => NameMatches(m.Name, Model));

			if (!present)
			{
				StatusReason = $"model '{Model}' not installed on {host} (run: ollama pull {Model})";
				return;
			}

			Available = true;
			StatusReason = $"ready ({Model} @ {host})";
		}
		catch (Exception ex)
		{
			StatusReason = $"unavailable: {ex.Message}";
		}
	}

	/// <summary>
	/// True when an installed model name matches the requested id, tolerating
	/// tag suffixes in either direction (e.g. "codellama:7b" vs "codellama").
	/// </summary>
	private static bool NameMatches(string installed, string wanted) =>
		string.Equals(installed, wanted, StringComparison.OrdinalIgnoreCase)
		|| installed.StartsWith(wanted + ":", StringComparison.OrdinalIgnoreCase)
		|| wanted.StartsWith(installed, StringComparison.OrdinalIgnoreCase);

	/// <summary>Host portion of the endpoint for status display; falls back to the raw value.</summary>
	private static string SafeHost(string endpoint) =>
		Uri.TryCreate(endpoint, UriKind.Absolute, out var u) ? u.Host : endpoint;

	// -----------------------------------------------------------------------
	// Instruction builder
	// -----------------------------------------------------------------------

	// -----------------------------------------------------------------------
	// Instruction builder
	// -----------------------------------------------------------------------

	private const string PassThruPrefix = "PassThru:";

	/// <summary>
	/// Single instruction for every prompt. The model replies in one of exactly
	/// two plain-text forms: a direct answer, or "PassThru:" followed by a
	/// minimised version of the prompt. Plain text is far more reliable than
	/// multi-field JSON for small local models.
	/// </summary>
	private static string BuildInstruction(string fileBlock, string prompt)
	{
		var sb = new StringBuilder();
		sb.Append(
			"You are a pre-processor between a user and a powerful cloud LLM.\n" +
			"Your only job is to take the following USER PROMPT and rewrite it using the least number words required to convey its full intent.\n" +
			"When minimizing this prompt, keep all code, `@` references, file paths and other critical components intact. Reduce unnecessary language. " +
			"Think: Caveman speak, but never reduce to the point where the original instructions are lost." + 
			"Your response will be passed to the cloud LLM verbatim. It must contain ONLY the minimized USER PROMPT.");

		sb.Append("\nUSER PROMPT:\n").Append(prompt);
		return sb.ToString();
	}

	// -----------------------------------------------------------------------
	// Shared helpers
	// -----------------------------------------------------------------------

	private static string BuildFileBlock(IReadOnlyList<(string Name, string Content)>? files)
	{
		if (files == null || files.Count == 0) return "";
		var sb = new StringBuilder();
		foreach (var f in files)
			sb.Append("\n--- FILE: ").Append(f.Name).Append(" ---\n").Append(f.Content);
		return sb.ToString();
	}

	private async Task<string> RunGenerateAsync(string instruction, string? format, CancellationToken ct)
	{
		var sb = new StringBuilder();
		var req = new GenerateRequest
		{
			Model = Model,
			Prompt = instruction,
			Stream = true,
			Format = format,
			Options = new RequestOptions { Temperature = 0f },
		};
		await foreach (var chunk in _client!.GenerateAsync(req).WithCancellation(ct))
			sb.Append(chunk?.Response);
		return sb.ToString();
	}

	private static LocalFilterResult Bypassed(string prompt, int original, string? note = null) =>
		new LocalFilterResult
		{
			Mode = LocalFilterMode.Bypassed,
			Prompt = prompt,
			OriginalChars = original,
			FinalChars = original,
			Note = note,
		};

	// -----------------------------------------------------------------------
	// ProcessAsync
	// -----------------------------------------------------------------------

	/// <summary>
	/// Runs the prompt through the local model. Returns Bypassed if the filter is
	/// off, unavailable, or anything goes wrong so the caller forwards as-is.
	/// </summary>
	public async Task<LocalFilterResult> ProcessAsync(string prompt, IReadOnlyList<(string Name, string Content)>? files, CancellationToken ct = default)
	{
		var original = prompt?.Length ?? 0;
		if (!Enabled || !Available || _client == null || string.IsNullOrWhiteSpace(prompt))
			return Bypassed(prompt ?? "", original);

		try
		{
			var fileBlock = BuildFileBlock(files);
			var response  = (await RunGenerateAsync(BuildInstruction(fileBlock, prompt!), format: null, ct)).Trim();

			if (string.IsNullOrWhiteSpace(response))
				return Bypassed(prompt!, original, "empty local response");

			return new LocalFilterResult
			{
				Mode = LocalFilterMode.Minimized,
				Prompt = response,
				OriginalChars = original,
				FinalChars = response.Length,
				ModelLabel = Model,
			};
		}
		catch (Exception ex)
		{
			return Bypassed(prompt!, original, ex.Message);
		}
	}

	/// <summary>
	/// Reduces and summarizes a document (e.g. a README) with the local model so
	/// the cloud receives a compact digest instead of the full text. Unlike
	/// <see cref="ProcessAsync"/> this never answers on the user's behalf - the
	/// reduced document is always destined for the cloud. Best-effort: if the
	/// filter is off, unavailable, or anything fails, returns
	/// <see cref="LocalFilterMode.Bypassed"/> carrying the original content so the
	/// caller forwards it unchanged.
	/// </summary>
	public async Task<LocalFilterResult> SummarizeAsync(string documentName, string content, CancellationToken ct = default)
	{
		var original = content?.Length ?? 0;
		if (!Enabled || !Available || _client == null || string.IsNullOrWhiteSpace(content))
			return Bypassed(content ?? "", original);

		try
		{
			var instruction =
				"You are a local pre-processor between a user and a powerful cloud LLM. " +
				"Reduce and summarize the document below into the shortest digest that still " +
				"lets the cloud model understand the project: its purpose, features, architecture, " +
				"key files, build and run commands, and configuration. " +
				"Keep code, paths, commands, and proper names verbatim. " +
				"Drop marketing, repetition, pleasantries, and decoration. " +
				"Reply with ONLY the summary text, no preamble or commentary. " +
				"DOCUMENT (" + documentName + "):\n" + content;

			var summary = (await RunGenerateAsync(instruction, format: null, ct)).Trim();
			if (string.IsNullOrWhiteSpace(summary))
				return Bypassed(content!, original, "empty local summary");

			return new LocalFilterResult
			{
				Mode = LocalFilterMode.Minimized,
				Prompt = summary,
				OriginalChars = original,
				FinalChars = summary.Length,
				ModelLabel = Model,
			};
		}
		catch (Exception ex)
		{
			return Bypassed(content!, original, ex.Message);
		}
	}

	private static (string mode, double confidence, string answer, string minimized) ParseJson(string raw)
	{
		// Still used by callers that may extend ProcessAsync in future.
		// Returns safe defaults on any parse failure.
		var s = raw.IndexOf('{');
		var e = raw.LastIndexOf('}');
		if (s < 0 || e <= s) return ("minimize", 0, "", "");
		try
		{
			using var doc = JsonDocument.Parse(raw.Substring(s, e - s + 1));
			var root = doc.RootElement;
			var mode = root.TryGetProperty("mode", out var mv) ? (mv.GetString() ?? "minimize") : "minimize";
			double conf = 0;
			if (root.TryGetProperty("confidence", out var c))
			{
				if (c.ValueKind == JsonValueKind.Number) conf = c.GetDouble();
				else if (c.ValueKind == JsonValueKind.String && double.TryParse(c.GetString(), out var pc)) conf = pc;
			}
			var ans = root.TryGetProperty("answer", out var a) ? (a.GetString() ?? "") : "";
			var min = root.TryGetProperty("minimized", out var m) ? (m.GetString() ?? "") : "";
			return (mode, conf, ans, min);
		}
		catch { return ("minimize", 0, "", ""); }
	}
}
