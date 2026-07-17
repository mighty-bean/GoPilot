using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace GoPilot;

/// <summary>
/// Modal manager for MCP servers. Shows the user's manually configured servers
/// (persisted in <c>gopilot.ini</c> under <c>[McpServers]</c>) together with the
/// servers discovered from <c>.mcp.json</c> files in the workspace, user, and app
/// folders. Each row has an enable checkbox; manual entries can be added, edited,
/// and removed, while discovered entries are read-only but can be switched off
/// (persisted as a disabled-name list). Edits a single manual server through the
/// nested <see cref="McpServerEditDialog"/>. OK is only returned when something
/// actually changed so callers can decide whether to trigger a session handoff.
///
/// MCP servers are read when a session is created or resumed, so changing them
/// requires a new session -- the caller is responsible for that.
/// </summary>
public sealed class McpServersDialog : Form
{
	private readonly ListView _list        = new();
	private readonly Button _buttonAdd    = new();
	private readonly Button _buttonEdit   = new();
	private readonly Button _buttonRemove = new();
	private readonly Button _buttonOk     = new();
	private readonly Button _buttonCancel = new();
	private readonly Label  _sourceLabel  = new();

	// Manual (editable) entries, cloned so edits do not touch the live list.
	private readonly List<McpServerEntry> _manual;
	// Discovered entries (read-only display; each carries its .mcp.json SourcePath).
	private readonly List<McpServerEntry> _discovered;
	// Names of discovered servers the user has switched off.
	private readonly HashSet<string> _disabled;

	private readonly List<string> _originalTokens;
	private readonly HashSet<string> _originalDisabled;
	private bool _populating;

	/// <summary>The edited manual server list. Only valid after the dialog returns OK.</summary>
	public List<McpServerEntry> Servers { get; private set; }

	/// <summary>
	/// Names of discovered servers the user switched off. Only meaningful after
	/// the dialog returns OK.
	/// </summary>
	public List<string> DisabledDiscovered => _disabled.ToList();

	public McpServersDialog(
		IEnumerable<McpServerEntry> manual,
		IEnumerable<McpServerEntry> discovered,
		IEnumerable<string> disabledDiscovered)
	{
		_manual = (manual ?? Enumerable.Empty<McpServerEntry>())
			.Where(s => s != null && !string.IsNullOrWhiteSpace(s.Name))
			.Select(s => s.Clone())
			.ToList();
		_discovered = (discovered ?? Enumerable.Empty<McpServerEntry>())
			.Where(s => s != null && !string.IsNullOrWhiteSpace(s.Name))
			.ToList();
		_disabled = new HashSet<string>(
			disabledDiscovered ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

		_originalTokens   = _manual.Select(s => s.Encode()).ToList();
		_originalDisabled = new HashSet<string>(_disabled, StringComparer.OrdinalIgnoreCase);
		Servers = _manual;

		BuildUi();
		ReloadList();
		UpdateButtonStates();
	}

	private void BuildUi()
	{
		SuspendLayout();
		AutoScaleDimensions = new SizeF(7F, 15F);
		AutoScaleMode       = AutoScaleMode.Font;

		Text            = "MCP Servers";
		FormBorderStyle = FormBorderStyle.Sizable;
		StartPosition   = FormStartPosition.CenterParent;
		MinimizeBox     = false;
		MaximizeBox     = true;
		ShowInTaskbar   = false;
		ClientSize      = new Size(760, 440);
		MinimumSize     = new Size(600, 360);
		BackColor       = AppTheme.Background;
		ForeColor       = AppTheme.TextPrimary;
		Font            = new Font("Segoe UI", 9F);
		KeyPreview      = true;

		var info = new Label
		{
			AutoSize  = false,
			Dock      = DockStyle.Top,
			Height    = 80,
			Padding   = new Padding(12, 10, 12, 6),
			ForeColor = AppTheme.TextMuted,
			Text      =
				"MCP servers attached to every session GoPilot starts. Manual entries plus any " +
				"servers found in a .mcp.json file in the workspace folder, your user folder, or " +
				"the GoPilot app folder (e.g. the file the Unreal Engine editor writes). Untick a " +
				"row to stop loading that server; discovered rows are read-only but can still be " +
				"disabled. Use Add.../Edit.../Remove for your own entries. Changes take effect on " +
				"the next session.",
		};

		var sidePanel = new Panel
		{
			Dock      = DockStyle.Right,
			Width     = 110,
			Padding   = new Padding(6, 0, 12, 0),
			BackColor = AppTheme.Background,
		};
		StyleButton(_buttonAdd,    "Add...",  0);
		StyleButton(_buttonEdit,   "Edit...", 34);
		StyleButton(_buttonRemove, "Remove",  68);
		sidePanel.Controls.Add(_buttonAdd);
		sidePanel.Controls.Add(_buttonEdit);
		sidePanel.Controls.Add(_buttonRemove);

		_list.Dock          = DockStyle.Fill;
		_list.View          = View.Details;
		_list.CheckBoxes    = true;
		_list.FullRowSelect = true;
		_list.MultiSelect   = false;
		_list.HideSelection = false;
		_list.BackColor     = AppTheme.InputBox;
		_list.ForeColor     = AppTheme.TextPrimary;
		_list.BorderStyle   = BorderStyle.FixedSingle;
		_list.Columns.Add("Server", 150);
		_list.Columns.Add("Type", 55);
		_list.Columns.Add("Target", 230);
		_list.Columns.Add("Source", 300);

		var centerPanel = new Panel
		{
			Dock      = DockStyle.Fill,
			Padding   = new Padding(12, 0, 0, 0),
			BackColor = AppTheme.Background,
		};
		centerPanel.Controls.Add(_list);

		var bottomPanel = new Panel
		{
			Dock      = DockStyle.Bottom,
			Height    = 72,
			Padding   = new Padding(12, 8, 12, 8),
			BackColor = AppTheme.Background,
		};
		_sourceLabel.AutoSize     = false;
		_sourceLabel.AutoEllipsis = true;
		_sourceLabel.ForeColor    = AppTheme.TextMuted;
		_sourceLabel.Anchor       = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
		_sourceLabel.Bounds       = new Rectangle(12, 6, bottomPanel.ClientSize.Width - 24, 20);
		_sourceLabel.Text         = "";

		StyleButton(_buttonOk,     "OK",     0);
		StyleButton(_buttonCancel, "Cancel", 0);
		_buttonOk.Anchor     = AnchorStyles.Top | AnchorStyles.Right;
		_buttonCancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
		_buttonOk.Width      = 90;
		_buttonCancel.Width  = 90;

		bottomPanel.Resize += (_, _) => LayoutBottom(bottomPanel);
		bottomPanel.Controls.Add(_sourceLabel);
		bottomPanel.Controls.Add(_buttonOk);
		bottomPanel.Controls.Add(_buttonCancel);
		LayoutBottom(bottomPanel);

		Controls.Add(centerPanel);
		Controls.Add(sidePanel);
		Controls.Add(bottomPanel);
		Controls.Add(info);

		AcceptButton = _buttonOk;
		CancelButton = _buttonCancel;

		_list.SelectedIndexChanged += (_, _) => OnSelectionChanged();
		_list.DoubleClick          += (_, _) => EditSelected();
		_list.ItemChecked          += OnItemChecked;
		_list.Resize               += (_, _) => SizeSourceColumn();

		_buttonAdd.Click    += (_, _) => OnAdd();
		_buttonEdit.Click   += (_, _) => EditSelected();
		_buttonRemove.Click += (_, _) => OnRemove();
		_buttonOk.Click     += (_, _) => OnOk();
		_buttonCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

		ResumeLayout(false);
		PerformLayout();
		SizeSourceColumn();
	}

	private static void StyleButton(Button b, string text, int top)
	{
		b.Text      = text;
		b.Top       = top;
		b.Left      = 0;
		b.Width     = 92;
		b.Height    = 28;
		b.FlatStyle = FlatStyle.Flat;
		b.FlatAppearance.BorderColor = AppTheme.ButtonBorder;
		b.BackColor = AppTheme.ButtonBg;
		b.ForeColor = AppTheme.TextPrimary;
		b.UseVisualStyleBackColor = false;
	}

	private void LayoutBottom(Panel bottomPanel)
	{
		int right = bottomPanel.ClientSize.Width - bottomPanel.Padding.Right;
		const int buttonsY = 40;
		_buttonOk.Location     = new Point(right - _buttonOk.Width, buttonsY);
		_buttonCancel.Location = new Point(_buttonOk.Left - _buttonCancel.Width - 8, buttonsY);
		_sourceLabel.Width     = bottomPanel.ClientSize.Width - 24;
	}

	private void SizeSourceColumn()
	{
		if (_list.Columns.Count < 4) return;
		int fixedCols = _list.Columns[0].Width + _list.Columns[1].Width + _list.Columns[2].Width;
		int fill = _list.ClientSize.Width - fixedCols - 4;
		if (fill > 120) _list.Columns[3].Width = fill;
	}

	private void ReloadList()
	{
		_populating = true;
		_list.BeginUpdate();
		_list.Items.Clear();

		foreach (var e in _manual)
			_list.Items.Add(MakeRow(e, isDiscovered: false));
		foreach (var e in _discovered)
			_list.Items.Add(MakeRow(e, isDiscovered: true));

		_list.EndUpdate();
		_populating = false;

		if (_list.Items.Count > 0)
			_list.Items[0].Selected = true;
		OnSelectionChanged();
	}

	private ListViewItem MakeRow(McpServerEntry e, bool isDiscovered)
	{
		var target = e.IsHttp
			? e.Url
			: (e.Command + (e.Args.Count > 0 ? " " + string.Join(" ", e.Args) : "")).Trim();

		var item = new ListViewItem(e.Name) { Tag = e };
		item.SubItems.Add(e.IsHttp ? "http" : "stdio");
		item.SubItems.Add(target);
		item.SubItems.Add(isDiscovered ? e.SourcePath : "manual");
		item.Checked = isDiscovered ? !_disabled.Contains(e.Name.Trim()) : e.Enabled;
		if (isDiscovered) item.ForeColor = AppTheme.ColorSubAgent;
		return item;
	}

	private McpServerEntry? SelectedEntry() =>
		_list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as McpServerEntry : null;

	private void OnSelectionChanged()
	{
		var e = SelectedEntry();
		_sourceLabel.Text = e == null
			? ""
			: e.IsDiscovered
				? $"Discovered from: {e.SourcePath}  (read-only; untick to disable)"
				: "Manual entry (editable).";
		UpdateButtonStates();
	}

	private void UpdateButtonStates()
	{
		var e = SelectedEntry();
		bool manual = e != null && !e.IsDiscovered;
		_buttonEdit.Enabled   = manual;
		_buttonRemove.Enabled = manual;
	}

	private void OnItemChecked(object? sender, ItemCheckedEventArgs args)
	{
		if (_populating) return;
		if (args.Item.Tag is not McpServerEntry e) return;

		if (e.IsDiscovered)
		{
			if (args.Item.Checked) _disabled.Remove(e.Name.Trim());
			else                   _disabled.Add(e.Name.Trim());
		}
		else
		{
			e.Enabled = args.Item.Checked;
		}
	}

	private void OnAdd()
	{
		var entry = new McpServerEntry();
		using var dlg = new McpServerEditDialog(entry, UsedNames(except: null));
		if (dlg.ShowDialog(this) != DialogResult.OK) return;

		_manual.Add(dlg.Result);
		ReloadList();
		SelectManual(_manual.Count - 1);
	}

	private void EditSelected()
	{
		var e = SelectedEntry();
		if (e == null || e.IsDiscovered) return;
		int index = _manual.IndexOf(e);
		if (index < 0) return;

		using var dlg = new McpServerEditDialog(e.Clone(), UsedNames(except: e));
		if (dlg.ShowDialog(this) != DialogResult.OK) return;

		_manual[index] = dlg.Result;
		ReloadList();
		SelectManual(index);
	}

	private void OnRemove()
	{
		var e = SelectedEntry();
		if (e == null || e.IsDiscovered) return;
		int index = _manual.IndexOf(e);
		if (index < 0) return;

		_manual.RemoveAt(index);
		ReloadList();
		SelectManual(Math.Min(index, _manual.Count - 1));
	}

	private void SelectManual(int index)
	{
		if (index < 0 || index >= _manual.Count || index >= _list.Items.Count) return;
		_list.Items[index].Selected = true;
		_list.Items[index].EnsureVisible();
	}

	private HashSet<string> UsedNames(McpServerEntry? except)
	{
		var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var m in _manual)
		{
			if (ReferenceEquals(m, except)) continue;
			set.Add(m.Name.Trim());
		}
		foreach (var d in _discovered)
			set.Add(d.Name.Trim());
		return set;
	}

	private void OnOk()
	{
		bool changed = ManualChanged() || !_disabled.SetEquals(_originalDisabled);
		DialogResult = changed ? DialogResult.OK : DialogResult.Cancel;
		Close();
	}

	private bool ManualChanged()
	{
		var current = _manual.Select(s => s.Encode()).ToList();
		if (current.Count != _originalTokens.Count) return true;
		for (int i = 0; i < current.Count; i++)
		{
			if (!string.Equals(current[i], _originalTokens[i], StringComparison.Ordinal))
				return true;
		}
		return false;
	}

	/// <summary>
	/// Modal editor for a single <see cref="McpServerEntry"/>. Shows the stdio
	/// or HTTP field group depending on the selected transport and validates the
	/// required fields (unique name, plus command or URL) before returning OK.
	/// </summary>
	private sealed class McpServerEditDialog : Form
	{
		private readonly TextBox  _nameBox    = new();
		private readonly CheckBox _enabledBox = new();
		private readonly RadioButton _stdioRadio = new();
		private readonly RadioButton _httpRadio  = new();

		private readonly Panel   _stdioPanel = new();
		private readonly TextBox _commandBox = new();
		private readonly TextBox _argsBox    = new();
		private readonly TextBox _workDirBox = new();
		private readonly TextBox _envBox     = new();

		private readonly Panel   _httpPanel  = new();
		private readonly TextBox _urlBox     = new();
		private readonly TextBox _headersBox = new();

		private readonly Button _ok     = new();
		private readonly Button _cancel = new();

		private readonly McpServerEntry _entry;
		private readonly HashSet<string> _otherNames;

		/// <summary>The edited entry. Only valid after the dialog returns OK.</summary>
		public McpServerEntry Result => _entry;

		public McpServerEditDialog(McpServerEntry entry, HashSet<string> otherNames)
		{
			_entry      = entry;
			_otherNames = otherNames;
			BuildUi();
			LoadFromEntry();
			ApplyTransportVisibility();
		}

		private void BuildUi()
		{
			SuspendLayout();
			AutoScaleDimensions = new SizeF(7F, 15F);
			AutoScaleMode       = AutoScaleMode.Font;

			Text            = "MCP Server";
			FormBorderStyle = FormBorderStyle.FixedDialog;
			StartPosition   = FormStartPosition.CenterParent;
			MinimizeBox     = false;
			MaximizeBox     = false;
			ShowInTaskbar   = false;
			ClientSize      = new Size(560, 476);
			BackColor       = AppTheme.Background;
			ForeColor       = AppTheme.TextPrimary;
			Font            = new Font("Segoe UI", 9F);
			KeyPreview      = true;

			var nameLabel = new Label
			{
				Text = "Name:", AutoSize = false,
				Bounds = new Rectangle(12, 14, 120, 20), ForeColor = AppTheme.TextMuted,
			};
			StyleInput(_nameBox);
			_nameBox.Bounds = new Rectangle(96, 12, 240, 22);

			_enabledBox.Text      = "Enabled";
			_enabledBox.Bounds    = new Rectangle(360, 12, 120, 22);
			_enabledBox.ForeColor = AppTheme.TextPrimary;
			_enabledBox.FlatStyle = FlatStyle.Flat;

			var transportLabel = new Label
			{
				Text = "Transport:", AutoSize = false,
				Bounds = new Rectangle(12, 48, 80, 20), ForeColor = AppTheme.TextMuted,
			};
			_stdioRadio.Text      = "Local process (stdio)";
			_stdioRadio.Bounds    = new Rectangle(96, 46, 170, 22);
			_stdioRadio.ForeColor = AppTheme.TextPrimary;
			_httpRadio.Text       = "HTTP";
			_httpRadio.Bounds     = new Rectangle(276, 46, 100, 22);
			_httpRadio.ForeColor  = AppTheme.TextPrimary;
			_stdioRadio.CheckedChanged += (_, _) => ApplyTransportVisibility();

			BuildStdioPanel();
			BuildHttpPanel();

			StyleButton(_ok,     "OK");
			StyleButton(_cancel, "Cancel");
			_ok.Bounds     = new Rectangle(366, 436, 90, 28);
			_cancel.Bounds = new Rectangle(458, 436, 90, 28);
			_ok.Click     += (_, _) => OnOk();
			_cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

			Controls.Add(nameLabel);
			Controls.Add(_nameBox);
			Controls.Add(_enabledBox);
			Controls.Add(transportLabel);
			Controls.Add(_stdioRadio);
			Controls.Add(_httpRadio);
			Controls.Add(_stdioPanel);
			Controls.Add(_httpPanel);
			Controls.Add(_ok);
			Controls.Add(_cancel);

			AcceptButton = _ok;
			CancelButton = _cancel;
			ResumeLayout(false);
		}

		private void BuildStdioPanel()
		{
			_stdioPanel.Bounds    = new Rectangle(12, 78, 536, 348);
			_stdioPanel.BackColor = AppTheme.Background;

			var commandLabel = new Label
			{
				Text = "Command (executable path):", AutoSize = false,
				Bounds = new Rectangle(0, 4, 400, 18), ForeColor = AppTheme.TextMuted,
			};
			StyleInput(_commandBox);
			_commandBox.Bounds = new Rectangle(0, 24, 430, 22);

			var browse = new Button { Text = "Browse..." };
			StyleButton(browse);
			browse.Bounds = new Rectangle(436, 23, 100, 24);
			browse.Click += (_, _) => BrowseForCommand();

			var argsLabel = new Label
			{
				Text = "Arguments (one per line):", AutoSize = false,
				Bounds = new Rectangle(0, 54, 400, 18), ForeColor = AppTheme.TextMuted,
			};
			StyleMultiline(_argsBox);
			_argsBox.Bounds = new Rectangle(0, 74, 536, 92);

			var workDirLabel = new Label
			{
				Text = "Working directory (optional):", AutoSize = false,
				Bounds = new Rectangle(0, 172, 400, 18), ForeColor = AppTheme.TextMuted,
			};
			StyleInput(_workDirBox);
			_workDirBox.Bounds = new Rectangle(0, 192, 536, 22);

			var envLabel = new Label
			{
				Text = "Environment (KEY=VALUE, one per line):", AutoSize = false,
				Bounds = new Rectangle(0, 222, 400, 18), ForeColor = AppTheme.TextMuted,
			};
			StyleMultiline(_envBox);
			_envBox.Bounds = new Rectangle(0, 242, 536, 92);

			_stdioPanel.Controls.Add(commandLabel);
			_stdioPanel.Controls.Add(_commandBox);
			_stdioPanel.Controls.Add(browse);
			_stdioPanel.Controls.Add(argsLabel);
			_stdioPanel.Controls.Add(_argsBox);
			_stdioPanel.Controls.Add(workDirLabel);
			_stdioPanel.Controls.Add(_workDirBox);
			_stdioPanel.Controls.Add(envLabel);
			_stdioPanel.Controls.Add(_envBox);
		}

		private void BuildHttpPanel()
		{
			_httpPanel.Bounds    = new Rectangle(12, 78, 536, 348);
			_httpPanel.BackColor = AppTheme.Background;

			var urlLabel = new Label
			{
				Text = "URL:", AutoSize = false,
				Bounds = new Rectangle(0, 4, 400, 18), ForeColor = AppTheme.TextMuted,
			};
			StyleInput(_urlBox);
			_urlBox.Bounds = new Rectangle(0, 24, 536, 22);

			var headersLabel = new Label
			{
				Text = "Headers (KEY=VALUE, one per line):", AutoSize = false,
				Bounds = new Rectangle(0, 54, 400, 18), ForeColor = AppTheme.TextMuted,
			};
			StyleMultiline(_headersBox);
			_headersBox.Bounds = new Rectangle(0, 74, 536, 140);

			_httpPanel.Controls.Add(urlLabel);
			_httpPanel.Controls.Add(_urlBox);
			_httpPanel.Controls.Add(headersLabel);
			_httpPanel.Controls.Add(_headersBox);
		}

		private static void StyleInput(TextBox t)
		{
			t.BackColor   = AppTheme.InputBox;
			t.ForeColor   = AppTheme.TextPrimary;
			t.BorderStyle = BorderStyle.FixedSingle;
		}

		private static void StyleMultiline(TextBox t)
		{
			t.Multiline   = true;
			t.WordWrap    = false;
			t.ScrollBars  = ScrollBars.Both;
			t.BackColor   = AppTheme.InputBox;
			t.ForeColor   = AppTheme.TextPrimary;
			t.BorderStyle = BorderStyle.FixedSingle;
			t.Font        = new Font("Consolas", 9F);
		}

		private static void StyleButton(Button b, string? text = null)
		{
			if (text != null) b.Text = text;
			b.FlatStyle = FlatStyle.Flat;
			b.FlatAppearance.BorderColor = AppTheme.ButtonBorder;
			b.BackColor = AppTheme.ButtonBg;
			b.ForeColor = AppTheme.TextPrimary;
			b.UseVisualStyleBackColor = false;
		}

		private void LoadFromEntry()
		{
			_nameBox.Text    = _entry.Name;
			_enabledBox.Checked = _entry.Enabled;
			_stdioRadio.Checked = !_entry.IsHttp;
			_httpRadio.Checked  = _entry.IsHttp;

			_commandBox.Text = _entry.Command;
			_argsBox.Text    = string.Join("\r\n", _entry.Args);
			_workDirBox.Text = _entry.WorkingDirectory;
			_envBox.Text     = DictToLines(_entry.Env);

			_urlBox.Text     = _entry.Url;
			_headersBox.Text = DictToLines(_entry.Headers);
		}

		private void ApplyTransportVisibility()
		{
			bool http = _httpRadio.Checked;
			_stdioPanel.Visible = !http;
			_httpPanel.Visible  = http;
		}

		private void BrowseForCommand()
		{
			using var dlg = new OpenFileDialog
			{
				Title  = "Select MCP server executable",
				Filter = "Programs (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|All files (*.*)|*.*",
			};
			if (!string.IsNullOrWhiteSpace(_commandBox.Text))
			{
				try { dlg.InitialDirectory = System.IO.Path.GetDirectoryName(_commandBox.Text.Trim()); }
				catch { /* ignore malformed path */ }
			}
			if (dlg.ShowDialog(this) == DialogResult.OK)
				_commandBox.Text = dlg.FileName;
		}

		private void OnOk()
		{
			var name = _nameBox.Text.Trim();
			if (name.Length == 0)
			{
				Warn("Enter a server name.");
				return;
			}
			if (_otherNames.Contains(name))
			{
				Warn("A server with that name already exists.");
				return;
			}

			bool http = _httpRadio.Checked;
			if (http)
			{
				var url = _urlBox.Text.Trim();
				if (url.Length == 0)
				{
					Warn("Enter the server URL.");
					return;
				}
				if (!Uri.TryCreate(url, UriKind.Absolute, out _))
				{
					Warn("Enter a valid absolute URL (e.g. http://localhost:8080/mcp).");
					return;
				}
			}
			else if (_commandBox.Text.Trim().Length == 0)
			{
				Warn("Enter the command (executable) to launch.");
				return;
			}

			_entry.Name             = name;
			_entry.Enabled          = _enabledBox.Checked;
			_entry.Transport        = http ? McpServerEntry.TransportHttp : McpServerEntry.TransportStdio;
			_entry.Command          = _commandBox.Text.Trim();
			_entry.Args             = LinesToList(_argsBox.Text);
			_entry.WorkingDirectory = _workDirBox.Text.Trim();
			_entry.Env              = LinesToDict(_envBox.Text);
			_entry.Url              = _urlBox.Text.Trim();
			_entry.Headers          = LinesToDict(_headersBox.Text);

			DialogResult = DialogResult.OK;
			Close();
		}

		private void Warn(string message) =>
			MessageBox.Show(this, message, "MCP Server",
				MessageBoxButtons.OK, MessageBoxIcon.Warning);

		private static List<string> LinesToList(string text) =>
			(text ?? "")
				.Replace("\r\n", "\n")
				.Split('\n')
				.Select(l => l.Trim())
				.Where(l => l.Length > 0)
				.ToList();

		private static string DictToLines(Dictionary<string, string> dict)
		{
			if (dict == null || dict.Count == 0) return "";
			return string.Join("\r\n", dict.Select(kv => $"{kv.Key}={kv.Value}"));
		}

		private static Dictionary<string, string> LinesToDict(string text)
		{
			var dict = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
			{
				var line = raw.Trim();
				if (line.Length == 0) continue;
				int eq = line.IndexOf('=');
				if (eq <= 0) continue;
				var key = line.Substring(0, eq).Trim();
				var val = line.Substring(eq + 1).Trim();
				if (key.Length == 0) continue;
				dict[key] = val;
			}
			return dict;
		}
	}
}
