using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace GoPilot;

/// <summary>
/// Modal dialog shown at the start of every new session. Lets the user choose
/// the AI service backing the session -- GitHub Copilot (cloud) or a local
/// OpenAI-compatible server (e.g. Lemonade or llama.cpp) addressed by host/IP,
/// port, and API path -- and independently enable or disable the local
/// "Filter LLM" (Ollama) used for prompt reduction and summarizing.
///
/// The composed values are exposed via the public properties and are only
/// valid after the dialog returns <see cref="DialogResult.OK"/>. Persisting
/// them and (re)connecting is the caller's responsibility.
/// </summary>
public sealed partial class AiServiceDialog : Form
{
	/// <summary>Chosen provider: <c>Copilot</c> or <c>LocalOpenAI</c>. Valid only after OK.</summary>
	public string Provider { get; private set; } = "Copilot";

	/// <summary>Composed local endpoint incl. API path (e.g. <c>http://10.0.0.234:13305/api/v1</c>). Valid only after OK.</summary>
	public string LocalEndpoint { get; private set; } = "";

	/// <summary>API key for the local provider. Valid only after OK.</summary>
	public string LocalApiKey { get; private set; } = "";

	/// <summary>
	/// Prompt-window ceiling to assume for the local provider when the server
	/// advertises none, in tokens. 0 means "not set". Valid only after OK.
	/// </summary>
	public int LocalContextSize { get; private set; }

	/// <summary>Whether the local Filter LLM is enabled. Valid only after OK.</summary>
	public bool FilterEnabled { get; private set; }

	/// <summary>Filter LLM (Ollama) endpoint, edited via Configure. Valid only after OK.</summary>
	public string FilterEndpoint { get; private set; } = "";

	/// <summary>Filter LLM model id (blank = auto-detect). Valid only after OK.</summary>
	public string FilterModel { get; private set; } = "";

	/// <summary>Filter LLM answer-locally confidence threshold. Valid only after OK.</summary>
	public double FilterThreshold { get; private set; }

	public AiServiceDialog(
		string provider,
		string localEndpoint,
		string localApiKey,
		int    localContextSize,
		bool   filterEnabled,
		string filterEndpoint,
		string filterModel,
		double filterThreshold)
	{
		FilterEndpoint  = filterEndpoint  ?? "";
		FilterModel     = filterModel     ?? "";
		FilterThreshold = filterThreshold;

		SplitEndpoint(localEndpoint, out var host, out var port, out var path);
		InitializeComponent();

		_radioCopilot.Checked = !string.Equals(provider, "LocalOpenAI", StringComparison.OrdinalIgnoreCase);
		_radioLocal.Checked   =  string.Equals(provider, "LocalOpenAI", StringComparison.OrdinalIgnoreCase);
		_hostBox.Text   = host;
		_portBox.Text   = port;
		_pathBox.Text   = string.IsNullOrWhiteSpace(path) ? "/api/v1" : path;
		_apiKeyBox.Text = string.IsNullOrWhiteSpace(localApiKey) ? "lemonade" : localApiKey;
		_ctxBox.Text    = localContextSize > 0
			? localContextSize.ToString(CultureInfo.InvariantCulture)
			: "";
		_filterEnabled.Checked = filterEnabled;

		UpdateEnabledStates();
		UpdatePreview();
		UpdateFilterStatus();
	}

	private void RadioProvider_CheckedChanged(object? sender, EventArgs e) => UpdateEnabledStates();

	private void EndpointField_TextChanged(object? sender, EventArgs e) => UpdatePreview();

	private void FilterConfig_Click(object? sender, EventArgs e) => OnConfigureFilter();

	private void Ok_Click(object? sender, EventArgs e) => OnOk();

	private void Cancel_Click(object? sender, EventArgs e)
	{
		DialogResult = DialogResult.Cancel;
		Close();
	}

	private void UpdateEnabledStates()
	{
		var local = _radioLocal.Checked;
		foreach (Control c in _localGroup.Controls)
			c.Enabled = local;
		_localGroup.ForeColor = local ? AppTheme.TextPrimary : AppTheme.TextMuted;
	}

	private void UpdateFilterStatus()
	{
		var host = Uri.TryCreate(FilterEndpoint, UriKind.Absolute, out var u) ? u.Host : FilterEndpoint;
		var model = string.IsNullOrWhiteSpace(FilterModel) ? "auto-detect" : FilterModel;
		_filterStatus.Text = string.IsNullOrWhiteSpace(host)
			? $"Model: {model}"
			: $"{model} @ {host}";
	}

	private void UpdatePreview()
	{
		if (!_radioLocal.Checked)
		{
			_previewLabel.Text = "";
			return;
		}
		if (TryBuildEndpoint(out var endpoint, out var error))
		{
			_previewLabel.Text      = "Endpoint: " + endpoint;
			_previewLabel.ForeColor = AppTheme.TextMuted;
		}
		else
		{
			_previewLabel.Text      = error;
			_previewLabel.ForeColor = Color.FromArgb(220, 120, 120);
		}
	}

	/// <summary>
	/// Composes host, port, and path into an absolute base URL. Forgiving of a
	/// pasted scheme in the host field, an embedded host:port, or an IPv6
	/// literal in brackets. Defaults to http when no scheme is supplied.
	/// </summary>
	private bool TryBuildEndpoint(out string endpoint, out string error)
	{
		endpoint = "";
		error    = "";

		var host = _hostBox.Text.Trim();
		if (host.Length == 0)
		{
			error = "Enter a host name or IP address.";
			return false;
		}

		var scheme = "http";
		if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
		{
			scheme = "http";
			host   = host.Substring(7);
		}
		else if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
		{
			scheme = "https";
			host   = host.Substring(8);
		}

		var slash = host.IndexOf('/');
		if (slash >= 0)
			host = host.Substring(0, slash);

		var portText = _portBox.Text.Trim();

		if (host.StartsWith("[", StringComparison.Ordinal))
		{
			var end = host.IndexOf(']');
			if (end < 0)
			{
				error = "Invalid IPv6 address (missing ']').";
				return false;
			}
			var after = host.Substring(end + 1);
			if (after.StartsWith(":", StringComparison.Ordinal))
				portText = after.Substring(1);
			host = host.Substring(0, end + 1);
		}
		else
		{
			var colon = host.IndexOf(':');
			if (colon >= 0)
			{
				portText = host.Substring(colon + 1);
				host     = host.Substring(0, colon);
			}
		}

		if (host.Length == 0)
		{
			error = "Enter a host name or IP address.";
			return false;
		}

		if (portText.Length == 0)
		{
			error = "Enter the server port.";
			return false;
		}

		if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
			|| port < 1 || port > 65535)
		{
			error = "Port must be a number between 1 and 65535.";
			return false;
		}

		var path = _pathBox.Text.Trim();
		if (path.Length == 0) path = "/api/v1";
		if (!path.StartsWith("/", StringComparison.Ordinal)) path = "/" + path;
		path = path.TrimEnd('/');

		var candidate = scheme + "://" + host + ":" + port.ToString(CultureInfo.InvariantCulture) + path;
		if (!Uri.TryCreate(candidate, UriKind.Absolute, out _))
		{
			error = "Could not form a valid URL from the host, port, and path.";
			return false;
		}

		endpoint = candidate;
		return true;
	}

	private void OnConfigureFilter()
	{
		using var dlg = new LocalLlmSettingsDialog(FilterEndpoint, FilterModel, FilterThreshold);
		if (dlg.ShowDialog(this) != DialogResult.OK) return;

		FilterEndpoint  = dlg.Endpoint;
		FilterModel     = dlg.Model;
		FilterThreshold = dlg.Threshold;
		if (!_filterEnabled.Checked) _filterEnabled.Checked = true;
		UpdateFilterStatus();
	}

	private void OnOk()
	{
		if (_radioLocal.Checked)
		{
			if (!TryBuildEndpoint(out var endpoint, out var error))
			{
				MessageBox.Show(this, error, "Choose AI Service",
					MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			var ctxText = _ctxBox.Text.Trim();
			var ctx     = 0;
			if (ctxText.Length > 0
				&& (!int.TryParse(ctxText, NumberStyles.Integer, CultureInfo.InvariantCulture, out ctx)
					|| ctx <= 0))
			{
				MessageBox.Show(this,
					"Context size must be a whole number of tokens, or blank to use the server's own value.",
					"Choose AI Service", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			Provider         = "LocalOpenAI";
			LocalEndpoint    = endpoint;
			LocalApiKey      = _apiKeyBox.Text.Trim();
			LocalContextSize = ctx;
		}
		else
		{
			Provider = "Copilot";
		}

		FilterEnabled = _filterEnabled.Checked;
		DialogResult  = DialogResult.OK;
		Close();
	}

	/// <summary>
	/// Splits an existing base URL into host, port, and API path for
	/// pre-populating the fields. Leaves fields blank / defaulted when the
	/// value is missing or unparseable.
	/// </summary>
	private static void SplitEndpoint(string endpoint, out string host, out string port, out string path)
	{
		host = "";
		port = "";
		path = "/api/v1";
		if (string.IsNullOrWhiteSpace(endpoint))
			return;

		if (Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
		{
			host = uri.Host;
			port = uri.Port > 0 ? uri.Port.ToString(CultureInfo.InvariantCulture) : "";
			path = string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/"
				? "/api/v1"
				: uri.AbsolutePath.TrimEnd('/');
		}
	}
}
