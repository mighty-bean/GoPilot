using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace GoPilot;

/// <summary>
/// Modal dialog for configuring the Local LLM filter's connection. Lets the
/// user point the filter at an Ollama host on the local machine OR anywhere on
/// the home network by host name / IP address and port, choose the model
/// (blank = auto-detect), and set the answer-locally confidence threshold.
///
/// The composed values are exposed via <see cref="Endpoint"/>,
/// <see cref="Model"/>, and <see cref="Threshold"/> and are only valid after
/// the dialog returns <see cref="DialogResult.OK"/>. Persisting them and
/// re-initialising the filter is the caller's responsibility.
/// </summary>
public sealed class LocalLlmSettingsDialog : Form
{
	private const int DefaultPort = 11434;

	private readonly TextBox _hostBox      = new();
	private readonly TextBox _portBox      = new();
	private readonly Label   _previewLabel = new();
	private readonly TextBox _modelBox     = new();
	private readonly TextBox _thresholdBox = new();
	private readonly Button  _ok           = new();
	private readonly Button  _cancel       = new();

	/// <summary>Composed Ollama endpoint URL (e.g. <c>http://192.168.1.50:11434</c>). Valid only after OK.</summary>
	public string Endpoint { get; private set; } = "";

	/// <summary>Model id, or empty string to auto-detect. Valid only after OK.</summary>
	public string Model { get; private set; } = "";

	/// <summary>Answer-locally confidence threshold in [0, 1]. Valid only after OK.</summary>
	public double Threshold { get; private set; }

	public LocalLlmSettingsDialog(string endpoint, string model, double threshold)
	{
		SplitEndpoint(endpoint, out var host, out var port);
		BuildUi();

		_hostBox.Text      = host;
		_portBox.Text      = port.ToString(CultureInfo.InvariantCulture);
		_modelBox.Text     = model ?? "";
		_thresholdBox.Text = threshold.ToString("0.##", CultureInfo.InvariantCulture);
		UpdatePreview();
	}

	private void BuildUi()
	{
		SuspendLayout();
		AutoScaleDimensions = new SizeF(7F, 15F);
		AutoScaleMode       = AutoScaleMode.Font;

		Text            = "Local LLM Settings";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		StartPosition   = FormStartPosition.CenterParent;
		MinimizeBox     = false;
		MaximizeBox     = false;
		ShowInTaskbar   = false;
		ClientSize      = new Size(520, 300);
		BackColor       = AppTheme.Background;
		ForeColor       = AppTheme.TextPrimary;
		Font            = new Font("Segoe UI", 9F);
		KeyPreview      = true;

		var intro = new Label
		{
			Text =
				"Point the Local LLM filter at an Ollama server. Use localhost for "
				+ "this machine, or the host name / IP address of another machine on "
				+ "your network. The model must be installed on that server.",
			AutoSize  = false,
			Bounds    = new Rectangle(12, 10, 496, 48),
			ForeColor = AppTheme.TextMuted,
		};

		var hostLabel = new Label
		{
			Text      = "Server host or IP:",
			AutoSize  = false,
			Bounds    = new Rectangle(12, 66, 130, 20),
			ForeColor = AppTheme.TextMuted,
		};
		StyleInput(_hostBox);
		_hostBox.Bounds = new Rectangle(146, 64, 232, 22);

		var portLabel = new Label
		{
			Text      = "Port:",
			AutoSize  = false,
			Bounds    = new Rectangle(388, 66, 36, 20),
			ForeColor = AppTheme.TextMuted,
		};
		StyleInput(_portBox);
		_portBox.Bounds = new Rectangle(426, 64, 82, 22);

		_previewLabel.AutoSize  = false;
		_previewLabel.Bounds    = new Rectangle(146, 90, 362, 18);
		_previewLabel.ForeColor = AppTheme.TextMuted;

		var modelLabel = new Label
		{
			Text      = "Model (blank = auto-detect):",
			AutoSize  = false,
			Bounds    = new Rectangle(12, 124, 200, 20),
			ForeColor = AppTheme.TextMuted,
		};
		StyleInput(_modelBox);
		_modelBox.Bounds = new Rectangle(12, 146, 496, 22);

		var modelHint = new Label
		{
			Text =
				"e.g. codellama:13b-instruct. Leave blank to use an installed "
				+ "codellama model on the server (auto-selected by VRAM when local).",
			AutoSize  = false,
			Bounds    = new Rectangle(12, 170, 496, 32),
			ForeColor = AppTheme.TextMuted,
		};

		var thresholdLabel = new Label
		{
			Text      = "Answer-locally confidence (0.00 - 1.00):",
			AutoSize  = false,
			Bounds    = new Rectangle(12, 210, 260, 20),
			ForeColor = AppTheme.TextMuted,
		};
		StyleInput(_thresholdBox);
		_thresholdBox.Bounds = new Rectangle(278, 208, 100, 22);

		StyleButton(_ok,     "OK");
		StyleButton(_cancel, "Cancel");
		_ok.Bounds     = new Rectangle(326, 258, 90, 28);
		_cancel.Bounds = new Rectangle(418, 258, 90, 28);

		_hostBox.TextChanged += (_, _) => UpdatePreview();
		_portBox.TextChanged += (_, _) => UpdatePreview();
		_ok.Click            += (_, _) => OnOk();
		_cancel.Click        += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

		Controls.Add(intro);
		Controls.Add(hostLabel);
		Controls.Add(_hostBox);
		Controls.Add(portLabel);
		Controls.Add(_portBox);
		Controls.Add(_previewLabel);
		Controls.Add(modelLabel);
		Controls.Add(_modelBox);
		Controls.Add(modelHint);
		Controls.Add(thresholdLabel);
		Controls.Add(_thresholdBox);
		Controls.Add(_ok);
		Controls.Add(_cancel);

		AcceptButton = _ok;
		CancelButton = _cancel;
		ResumeLayout(false);
	}

	private static void StyleInput(TextBox t)
	{
		t.BackColor   = AppTheme.InputBox;
		t.ForeColor   = AppTheme.TextPrimary;
		t.BorderStyle = BorderStyle.FixedSingle;
	}

	private static void StyleButton(Button b, string text)
	{
		b.Text      = text;
		b.FlatStyle = FlatStyle.Flat;
		b.FlatAppearance.BorderColor = AppTheme.ButtonBorder;
		b.BackColor = AppTheme.ButtonBg;
		b.ForeColor = AppTheme.TextPrimary;
		b.UseVisualStyleBackColor = false;
	}

	private void UpdatePreview()
	{
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
	/// Composes the host and port fields into an absolute Ollama URL. Forgiving
	/// of pasted values: strips a leading http(s):// scheme and any trailing
	/// path, and honours a port embedded in the host field (host:port or an
	/// IPv6 literal in brackets). Defaults to http when no scheme is supplied.
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
			// IPv6 literal, e.g. [::1] or [::1]:11434
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
			portText = DefaultPort.ToString(CultureInfo.InvariantCulture);

		if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
			|| port < 1 || port > 65535)
		{
			error = "Port must be a number between 1 and 65535.";
			return false;
		}

		var candidate = scheme + "://" + host + ":" + port.ToString(CultureInfo.InvariantCulture);
		if (!Uri.TryCreate(candidate, UriKind.Absolute, out _))
		{
			error = "Could not form a valid URL from the host and port.";
			return false;
		}

		endpoint = candidate;
		return true;
	}

	private void OnOk()
	{
		if (!TryBuildEndpoint(out var endpoint, out var error))
		{
			MessageBox.Show(this, error, "Local LLM Settings",
				MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}

		var tt = _thresholdBox.Text.Trim();
		if (!double.TryParse(tt, NumberStyles.Any, CultureInfo.InvariantCulture, out var threshold)
			|| threshold < 0 || threshold > 1)
		{
			MessageBox.Show(this,
				"Confidence threshold must be a number between 0.00 and 1.00.",
				"Local LLM Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}

		Endpoint  = endpoint;
		Model     = _modelBox.Text.Trim();
		Threshold = threshold;
		DialogResult = DialogResult.OK;
		Close();
	}

	/// <summary>
	/// Splits an existing endpoint URL into host and port for pre-populating
	/// the fields. Falls back to localhost:11434 when the value is missing or
	/// unparseable so the dialog always opens in a sane state.
	/// </summary>
	private static void SplitEndpoint(string endpoint, out string host, out int port)
	{
		host = "localhost";
		port = DefaultPort;
		if (string.IsNullOrWhiteSpace(endpoint))
			return;

		if (Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
		{
			host = uri.Host;
			port = uri.Port > 0 ? uri.Port : DefaultPort;
		}
	}
}
