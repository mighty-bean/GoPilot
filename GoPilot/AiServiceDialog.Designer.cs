namespace GoPilot;

partial class AiServiceDialog
{
	/// <summary>Required designer variable.</summary>
	private System.ComponentModel.IContainer components = null;

	/// <summary>Clean up any resources being used.</summary>
	/// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
	protected override void Dispose(bool disposing)
	{
		if (disposing && (components != null))
		{
			components.Dispose();
		}
		base.Dispose(disposing);
	}

    #region Windows Form Designer generated code

    /// <summary>
    /// Required method for Designer support - do not modify
    /// the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        _introLabel = new Label();
        _radioCopilot = new RadioButton();
        _radioLocal = new RadioButton();
        _localGroup = new GroupBox();
        _hostLabel = new Label();
        _hostBox = new TextBox();
        _portLabel = new Label();
        _portBox = new TextBox();
        _pathLabel = new Label();
        _pathBox = new ComboBox();
        _keyLabel = new Label();
        _apiKeyBox = new ComboBox();
        _ctxLabel = new Label();
        _ctxBox = new TextBox();
        _ctxHintLabel = new Label();
        _trimCheck = new CheckBox();
        _trimBox = new TextBox();
        _trimHintLabel = new Label();
        _pathHintLabel = new Label();
        _previewLabel = new Label();
        _filterGroup = new GroupBox();
        _filterEnabled = new CheckBox();
        _filterConfig = new Button();
        _filterStatus = new Label();
        _ok = new Button();
        _cancel = new Button();
        _localGroup.SuspendLayout();
        _filterGroup.SuspendLayout();
        SuspendLayout();
        // 
        // _introLabel
        // 
        _introLabel.ForeColor = SystemColors.ControlLight;
        _introLabel.Location = new Point(12, 10);
        _introLabel.Name = "_introLabel";
        _introLabel.Size = new Size(516, 46);
        _introLabel.TabIndex = 0;
        _introLabel.Text = "Select the AI service for this session. Copilot runs the cloud model; a local server runs the full agent loop against an OpenAI-compatible endpoint on this machine or your network.";
        // 
        // _radioCopilot
        // 
        _radioCopilot.AutoSize = true;
        _radioCopilot.FlatStyle = FlatStyle.Flat;
        _radioCopilot.ForeColor = SystemColors.ControlLight;
        _radioCopilot.Location = new Point(16, 60);
        _radioCopilot.Name = "_radioCopilot";
        _radioCopilot.Size = new Size(145, 19);
        _radioCopilot.TabIndex = 1;
        _radioCopilot.Text = "GitHub Copilot (cloud)";
        _radioCopilot.CheckedChanged += RadioProvider_CheckedChanged;
        // 
        // _radioLocal
        // 
        _radioLocal.AutoSize = true;
        _radioLocal.FlatStyle = FlatStyle.Flat;
        _radioLocal.ForeColor = SystemColors.ControlLight;
        _radioLocal.Location = new Point(16, 84);
        _radioLocal.Name = "_radioLocal";
        _radioLocal.Size = new Size(324, 19);
        _radioLocal.TabIndex = 2;
        _radioLocal.Text = "Local OpenAI-compatible server (Lemonade / llama.cpp)";
        _radioLocal.CheckedChanged += RadioProvider_CheckedChanged;
        // 
        // _localGroup
        // 
        _localGroup.Controls.Add(_hostLabel);
        _localGroup.Controls.Add(_hostBox);
        _localGroup.Controls.Add(_portLabel);
        _localGroup.Controls.Add(_portBox);
        _localGroup.Controls.Add(_pathLabel);
        _localGroup.Controls.Add(_pathBox);
        _localGroup.Controls.Add(_keyLabel);
        _localGroup.Controls.Add(_apiKeyBox);
        _localGroup.Controls.Add(_ctxLabel);
        _localGroup.Controls.Add(_ctxBox);
        _localGroup.Controls.Add(_ctxHintLabel);
        _localGroup.Controls.Add(_trimCheck);
        _localGroup.Controls.Add(_trimBox);
        _localGroup.Controls.Add(_trimHintLabel);
        _localGroup.Controls.Add(_pathHintLabel);
        _localGroup.Controls.Add(_previewLabel);
        _localGroup.ForeColor = SystemColors.ControlLight;
        _localGroup.Location = new Point(12, 112);
        _localGroup.Name = "_localGroup";
        _localGroup.Size = new Size(516, 276);
        _localGroup.TabIndex = 3;
        _localGroup.TabStop = false;
        _localGroup.Text = "Local server";
        // 
        // _hostLabel
        // 
        _hostLabel.ForeColor = SystemColors.ControlLight;
        _hostLabel.Location = new Point(12, 28);
        _hostLabel.Name = "_hostLabel";
        _hostLabel.Size = new Size(90, 20);
        _hostLabel.TabIndex = 0;
        _hostLabel.Text = "Host or IP:";
        // 
        // _hostBox
        // 
        _hostBox.BackColor = Color.FromArgb(52, 52, 52);
        _hostBox.BorderStyle = BorderStyle.FixedSingle;
        _hostBox.ForeColor = SystemColors.ControlLight;
        _hostBox.Location = new Point(104, 26);
        _hostBox.Name = "_hostBox";
        _hostBox.Size = new Size(224, 23);
        _hostBox.TabIndex = 1;
        _hostBox.TextChanged += EndpointField_TextChanged;
        // 
        // _portLabel
        // 
        _portLabel.ForeColor = SystemColors.ControlLight;
        _portLabel.Location = new Point(339, 28);
        _portLabel.Name = "_portLabel";
        _portLabel.Size = new Size(36, 20);
        _portLabel.TabIndex = 2;
        _portLabel.Text = "Port:";
        // 
        // _portBox
        // 
        _portBox.BackColor = Color.FromArgb(52, 52, 52);
        _portBox.BorderStyle = BorderStyle.FixedSingle;
        _portBox.ForeColor = SystemColors.ControlLight;
        _portBox.Location = new Point(376, 26);
        _portBox.Name = "_portBox";
        _portBox.Size = new Size(124, 23);
        _portBox.TabIndex = 3;
        _portBox.TextChanged += EndpointField_TextChanged;
        // 
        // _pathLabel
        // 
        _pathLabel.ForeColor = SystemColors.ControlLight;
        _pathLabel.Location = new Point(12, 60);
        _pathLabel.Name = "_pathLabel";
        _pathLabel.Size = new Size(90, 20);
        _pathLabel.TabIndex = 4;
        _pathLabel.Text = "API path:";
        // 
        // _pathBox
        // 
        _pathBox.BackColor = Color.FromArgb(52, 52, 52);
        _pathBox.FlatStyle = FlatStyle.Flat;
        _pathBox.ForeColor = SystemColors.ControlLight;
        _pathBox.FormattingEnabled = true;
        _pathBox.Items.AddRange(new object[] { "/api/v1", "/v1" });
        _pathBox.Location = new Point(104, 58);
        _pathBox.Name = "_pathBox";
        _pathBox.Size = new Size(224, 23);
        _pathBox.TabIndex = 5;
        _pathBox.TextChanged += EndpointField_TextChanged;
        // 
        // _keyLabel
        // 
        _keyLabel.ForeColor = SystemColors.ControlLight;
        _keyLabel.Location = new Point(339, 60);
        _keyLabel.Name = "_keyLabel";
        _keyLabel.Size = new Size(36, 20);
        _keyLabel.TabIndex = 6;
        _keyLabel.Text = "API key:";
        // 
        // _apiKeyBox
        // 
        _apiKeyBox.BackColor = Color.FromArgb(52, 52, 52);
        _apiKeyBox.FlatStyle = FlatStyle.Flat;
        _apiKeyBox.ForeColor = SystemColors.ControlLight;
        _apiKeyBox.FormattingEnabled = true;
        _apiKeyBox.Items.AddRange(new object[] { "lemonade", "ollama", "lm-studio", "sk-no-key-required" });
        _apiKeyBox.Location = new Point(376, 58);
        _apiKeyBox.Name = "_apiKeyBox";
        _apiKeyBox.Size = new Size(124, 23);
        _apiKeyBox.TabIndex = 7;
        // 
        // _ctxLabel
        // 
        _ctxLabel.ForeColor = SystemColors.ControlLight;
        _ctxLabel.Location = new Point(12, 92);
        _ctxLabel.Name = "_ctxLabel";
        _ctxLabel.Size = new Size(90, 20);
        _ctxLabel.TabIndex = 8;
        _ctxLabel.Text = "Context size:";
        // 
        // _ctxBox
        // 
        _ctxBox.BackColor = Color.FromArgb(52, 52, 52);
        _ctxBox.BorderStyle = BorderStyle.FixedSingle;
        _ctxBox.ForeColor = SystemColors.ControlLight;
        _ctxBox.Location = new Point(104, 90);
        _ctxBox.Name = "_ctxBox";
        _ctxBox.Size = new Size(100, 23);
        _ctxBox.TabIndex = 9;
        // 
        // _ctxHintLabel
        // 
        _ctxHintLabel.ForeColor = SystemColors.ControlLight;
        _ctxHintLabel.Location = new Point(212, 92);
        _ctxHintLabel.Name = "_ctxHintLabel";
        _ctxHintLabel.Size = new Size(292, 20);
        _ctxHintLabel.TabIndex = 10;
        _ctxHintLabel.Text = "tokens - leave blank to use the server's own value";
        // 
        // _trimCheck
        // 
        _trimCheck.ForeColor = SystemColors.ControlLight;
        _trimCheck.Location = new Point(12, 118);
        _trimCheck.Name = "_trimCheck";
        _trimCheck.Size = new Size(492, 20);
        _trimCheck.TabIndex = 11;
        _trimCheck.Text = "Hide these built-in tools to free up prompt window";
        _trimCheck.CheckedChanged += TrimCheck_CheckedChanged;
        // 
        // _trimBox
        // 
        _trimBox.BackColor = Color.FromArgb(52, 52, 52);
        _trimBox.BorderStyle = BorderStyle.FixedSingle;
        _trimBox.ForeColor = SystemColors.ControlLight;
        _trimBox.Location = new Point(12, 142);
        _trimBox.Name = "_trimBox";
        _trimBox.Size = new Size(492, 23);
        _trimBox.TabIndex = 12;
        // 
        // _trimHintLabel
        // 
        _trimHintLabel.ForeColor = SystemColors.ControlLight;
        _trimHintLabel.Location = new Point(12, 168);
        _trimHintLabel.Name = "_trimHintLabel";
        _trimHintLabel.Size = new Size(492, 32);
        _trimHintLabel.TabIndex = 13;
        _trimHintLabel.Text = "Comma-separated names. The default hides sub-agent dispatch, saving about 3,000 tokens per prompt; add skill and sql for 1,300 more. Ignored while Fleet mode is on.";
        // 
        // _pathHintLabel
        // 
        _pathHintLabel.ForeColor = SystemColors.ControlLight;
        _pathHintLabel.Location = new Point(12, 204);
        _pathHintLabel.Name = "_pathHintLabel";
        _pathHintLabel.Size = new Size(492, 32);
        _pathHintLabel.TabIndex = 14;
        _pathHintLabel.Text = "Lemonade uses /api/v1; llama.cpp uses /v1. Any non-empty key is accepted by most servers.";
        // 
        // _previewLabel
        // 
        _previewLabel.ForeColor = SystemColors.ControlLight;
        _previewLabel.Location = new Point(12, 242);
        _previewLabel.Name = "_previewLabel";
        _previewLabel.Size = new Size(492, 26);
        _previewLabel.TabIndex = 15;
        // 
        // _filterGroup
        // 
        _filterGroup.Controls.Add(_filterEnabled);
        _filterGroup.Controls.Add(_filterConfig);
        _filterGroup.Controls.Add(_filterStatus);
        _filterGroup.ForeColor = SystemColors.ControlLight;
        _filterGroup.Location = new Point(12, 398);
        _filterGroup.Name = "_filterGroup";
        _filterGroup.Size = new Size(516, 100);
        _filterGroup.TabIndex = 4;
        _filterGroup.TabStop = false;
        _filterGroup.Text = "(Optional) Filter LLM for local prompt reduction and summarizing";
        // 
        // _filterEnabled
        // 
        _filterEnabled.AutoSize = true;
        _filterEnabled.FlatStyle = FlatStyle.Flat;
        _filterEnabled.ForeColor = SystemColors.ControlLight;
        _filterEnabled.Location = new Point(12, 28);
        _filterEnabled.Name = "_filterEnabled";
        _filterEnabled.Size = new Size(393, 19);
        _filterEnabled.TabIndex = 0;
        _filterEnabled.Text = "Use a local LLM to minimize prompts and summarize before the cloud";
        // 
        // _filterConfig
        // 
        _filterConfig.BackColor = Color.FromArgb(86, 86, 86);
        _filterConfig.FlatAppearance.BorderColor = Color.FromArgb(108, 108, 108);
        _filterConfig.FlatStyle = FlatStyle.Flat;
        _filterConfig.ForeColor = SystemColors.ControlLight;
        _filterConfig.Location = new Point(12, 56);
        _filterConfig.Name = "_filterConfig";
        _filterConfig.Size = new Size(110, 28);
        _filterConfig.TabIndex = 1;
        _filterConfig.Text = "Configure...";
        _filterConfig.UseVisualStyleBackColor = false;
        _filterConfig.Click += FilterConfig_Click;
        // 
        // _filterStatus
        // 
        _filterStatus.ForeColor = SystemColors.ControlLight;
        _filterStatus.Location = new Point(132, 60);
        _filterStatus.Name = "_filterStatus";
        _filterStatus.Size = new Size(372, 20);
        _filterStatus.TabIndex = 2;
        // 
        // _ok
        // 
        _ok.BackColor = Color.FromArgb(86, 86, 86);
        _ok.FlatAppearance.BorderColor = Color.FromArgb(108, 108, 108);
        _ok.FlatStyle = FlatStyle.Flat;
        _ok.ForeColor = SystemColors.ControlLight;
        _ok.Location = new Point(346, 532);
        _ok.Name = "_ok";
        _ok.Size = new Size(90, 28);
        _ok.TabIndex = 5;
        _ok.Text = "OK";
        _ok.UseVisualStyleBackColor = false;
        _ok.Click += Ok_Click;
        // 
        // _cancel
        // 
        _cancel.BackColor = Color.FromArgb(86, 86, 86);
        _cancel.FlatAppearance.BorderColor = Color.FromArgb(108, 108, 108);
        _cancel.FlatStyle = FlatStyle.Flat;
        _cancel.ForeColor = SystemColors.ControlLight;
        _cancel.Location = new Point(438, 532);
        _cancel.Name = "_cancel";
        _cancel.Size = new Size(90, 28);
        _cancel.TabIndex = 6;
        _cancel.Text = "Cancel";
        _cancel.UseVisualStyleBackColor = false;
        _cancel.Click += Cancel_Click;
        // 
        // AiServiceDialog
        // 
        AcceptButton = _ok;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        BackColor = Color.FromArgb(64, 64, 64);
        CancelButton = _cancel;
        ClientSize = new Size(540, 572);
        Controls.Add(_introLabel);
        Controls.Add(_radioCopilot);
        Controls.Add(_radioLocal);
        Controls.Add(_localGroup);
        Controls.Add(_filterGroup);
        Controls.Add(_ok);
        Controls.Add(_cancel);
        Font = new Font("Segoe UI", 9F);
        ForeColor = Color.FromArgb(218, 218, 218);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        KeyPreview = true;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "AiServiceDialog";
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Choose AI Service";
        _localGroup.ResumeLayout(false);
        _localGroup.PerformLayout();
        _filterGroup.ResumeLayout(false);
        _filterGroup.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private System.Windows.Forms.Label _introLabel;
	private System.Windows.Forms.RadioButton _radioCopilot;
	private System.Windows.Forms.RadioButton _radioLocal;
	private System.Windows.Forms.GroupBox _localGroup;
	private System.Windows.Forms.Label _hostLabel;
	private System.Windows.Forms.TextBox _hostBox;
	private System.Windows.Forms.Label _portLabel;
	private System.Windows.Forms.TextBox _portBox;
	private System.Windows.Forms.Label _pathLabel;
	private System.Windows.Forms.ComboBox _pathBox;
	private System.Windows.Forms.Label _keyLabel;
	private System.Windows.Forms.ComboBox _apiKeyBox;
	private System.Windows.Forms.Label _ctxLabel;
	private System.Windows.Forms.TextBox _ctxBox;
	private System.Windows.Forms.Label _ctxHintLabel;
	private System.Windows.Forms.CheckBox _trimCheck;
	private System.Windows.Forms.TextBox _trimBox;
	private System.Windows.Forms.Label _trimHintLabel;
	private System.Windows.Forms.Label _pathHintLabel;
	private System.Windows.Forms.Label _previewLabel;
	private System.Windows.Forms.GroupBox _filterGroup;
	private System.Windows.Forms.CheckBox _filterEnabled;
	private System.Windows.Forms.Button _filterConfig;
	private System.Windows.Forms.Label _filterStatus;
	private System.Windows.Forms.Button _ok;
	private System.Windows.Forms.Button _cancel;
}
