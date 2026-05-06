using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using GoPilot.SkillCatalog;

namespace GoPilot;

/// <summary>
/// Modal dialog for editing the Skill Sources URL list persisted in
/// <c>gopilot.ini</c> under the <c>[SkillSources]</c> section. Modeled on
/// <see cref="SkillTreeDialog"/>: same Add / Remove / Move-Up / Move-Down /
/// OK / Cancel layout, same "OK only when changed" semantics.
///
/// Editing this list does NOT trigger a session refresh -- it only changes
/// which remotes the Skill Catalog browser will scan.
/// </summary>
public sealed class SkillSourcesDialog : Form
{
	private readonly ListBox _list = new();
	private readonly Button _buttonAdd = new();
	private readonly Button _buttonRemove = new();
	private readonly Button _buttonUp = new();
	private readonly Button _buttonDown = new();
	private readonly Button _buttonOk = new();
	private readonly Button _buttonCancel = new();
	private readonly List<string> _original;

	/// <summary>The edited list of URLs. Only valid after the dialog returns OK.</summary>
	public List<string> Urls { get; private set; }

	public SkillSourcesDialog(IEnumerable<string> initialUrls)
	{
		_original = initialUrls
			.Where(u => !string.IsNullOrWhiteSpace(u))
			.Select(u => u.Trim())
			.ToList();
		Urls = new List<string>(_original);

		BuildUi();
		ReloadList();
		UpdateButtonStates();
	}

	private void BuildUi()
	{
		Text            = "Skill Sources";
		FormBorderStyle = FormBorderStyle.Sizable;
		StartPosition   = FormStartPosition.CenterParent;
		MinimizeBox     = false;
		MaximizeBox     = true;
		ShowInTaskbar   = false;
		ClientSize      = new Size(680, 380);
		MinimumSize     = new Size(520, 300);
		BackColor       = AppTheme.Background;
		ForeColor       = AppTheme.TextPrimary;
		Font            = new Font("Segoe UI", 9F);
		KeyPreview      = true;

		var info = new Label
		{
			AutoSize  = false,
			Dock      = DockStyle.Top,
			Height    = 60,
			Padding   = new Padding(12, 10, 12, 6),
			ForeColor = AppTheme.TextMuted,
			Text      =
				"Remote sources scanned by Tools > Browse Skill Catalog... " +
				"GitHub and Azure DevOps URLs are supported. " +
				"GoPilot does not collect credentials -- only public / anonymously " +
				"readable repositories are accessible. Editing this list does not " +
				"trigger a session refresh.",
		};

		var sidePanel = new Panel
		{
			Dock      = DockStyle.Right,
			Width     = 110,
			Padding   = new Padding(6, 0, 12, 0),
			BackColor = AppTheme.Background,
		};
		StyleButton(_buttonAdd,    "Add...",    0);
		StyleButton(_buttonRemove, "Remove",    34);
		StyleButton(_buttonUp,     "Move Up",   76);
		StyleButton(_buttonDown,   "Move Down", 110);
		sidePanel.Controls.Add(_buttonAdd);
		sidePanel.Controls.Add(_buttonRemove);
		sidePanel.Controls.Add(_buttonUp);
		sidePanel.Controls.Add(_buttonDown);

		_list.Dock                 = DockStyle.Fill;
		_list.BackColor            = AppTheme.InputBox;
		_list.ForeColor            = AppTheme.TextPrimary;
		_list.BorderStyle          = BorderStyle.FixedSingle;
		_list.IntegralHeight       = false;
		_list.HorizontalScrollbar  = true;
		_list.SelectionMode        = SelectionMode.One;

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
			Height    = 48,
			Padding   = new Padding(12, 8, 12, 8),
			BackColor = AppTheme.Background,
		};
		StyleButton(_buttonOk,     "OK",     0);
		StyleButton(_buttonCancel, "Cancel", 0);
		_buttonOk.Anchor     = AnchorStyles.Top | AnchorStyles.Right;
		_buttonCancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
		_buttonOk.Width      = 90;
		_buttonCancel.Width  = 90;
		bottomPanel.Resize  += (_, _) => LayoutBottomButtons(bottomPanel);
		bottomPanel.Controls.Add(_buttonOk);
		bottomPanel.Controls.Add(_buttonCancel);
		LayoutBottomButtons(bottomPanel);

		Controls.Add(centerPanel);
		Controls.Add(sidePanel);
		Controls.Add(bottomPanel);
		Controls.Add(info);

		AcceptButton = _buttonOk;
		CancelButton = _buttonCancel;

		_list.SelectedIndexChanged += (_, _) => UpdateButtonStates();
		_list.DoubleClick           += (_, _) => { if (_list.SelectedIndex >= 0) EditSelected(_list.SelectedIndex); };

		_buttonAdd.Click    += (_, _) => OnAdd();
		_buttonRemove.Click += (_, _) => OnRemove();
		_buttonUp.Click     += (_, _) => OnMove(-1);
		_buttonDown.Click   += (_, _) => OnMove(+1);
		_buttonOk.Click     += (_, _) => OnOk();
		_buttonCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
	}

	private static void StyleButton(Button b, string text, int top)
	{
		b.Text     = text;
		b.Top      = top;
		b.Left     = 0;
		b.Width    = 92;
		b.Height   = 28;
		b.FlatStyle = FlatStyle.Flat;
		b.FlatAppearance.BorderColor = AppTheme.ButtonBorder;
		b.BackColor = AppTheme.ButtonBg;
		b.ForeColor = AppTheme.TextPrimary;
		b.UseVisualStyleBackColor = false;
	}

	private void LayoutBottomButtons(Panel bottomPanel)
	{
		int right = bottomPanel.ClientSize.Width - bottomPanel.Padding.Right;
		_buttonOk.Location     = new Point(right - _buttonOk.Width, bottomPanel.Padding.Top);
		_buttonCancel.Location = new Point(_buttonOk.Left - _buttonCancel.Width - 8, bottomPanel.Padding.Top);
	}

	private void ReloadList()
	{
		int prevIndex = _list.SelectedIndex;
		_list.BeginUpdate();
		_list.Items.Clear();
		foreach (var u in Urls) _list.Items.Add(FormatRow(u));
		_list.EndUpdate();
		if (_list.Items.Count > 0)
			_list.SelectedIndex = Math.Min(Math.Max(0, prevIndex), _list.Items.Count - 1);
		UpdateButtonStates();
	}

	private static string FormatRow(string url)
	{
		// Show "[GitHub] https://..." / "[ADO] ..." / "[?] ..." prefix so the
		// user can spot unknown-host entries without leaving the dialog.
		var src = CatalogSource.TryParse(url, out var s, out _) ? s!.Kind : ProviderKind.Unknown;
		var tag = src switch
		{
			ProviderKind.GitHub      => "[GitHub]      ",
			ProviderKind.AzureDevOps => "[ADO]         ",
			_                        => "[?]           ",
		};
		return tag + url;
	}

	private void UpdateButtonStates()
	{
		int i = _list.SelectedIndex;
		_buttonRemove.Enabled = i >= 0;
		_buttonUp.Enabled     = i > 0;
		_buttonDown.Enabled   = i >= 0 && i < Urls.Count - 1;
	}

	private void OnAdd()
	{
		var url = PromptForUrl(initial: "");
		if (string.IsNullOrWhiteSpace(url)) return;
		AddUrl(url.Trim());
	}

	private void EditSelected(int index)
	{
		if (index < 0 || index >= Urls.Count) return;
		var url = PromptForUrl(initial: Urls[index]);
		if (string.IsNullOrWhiteSpace(url)) return;
		var trimmed = url.Trim();
		if (IsDuplicate(trimmed, exceptIndex: index))
		{
			MessageBox.Show(this, "That source URL is already in the list.", "Skill Sources",
				MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}
		Urls[index] = trimmed;
		ReloadList();
		_list.SelectedIndex = index;
	}

	private string? PromptForUrl(string initial)
	{
		using var dlg = new UrlInputDialog(initial);
		return dlg.ShowDialog(this) == DialogResult.OK ? dlg.Url : null;
	}

	private bool IsDuplicate(string candidate, int exceptIndex = -1)
	{
		for (int i = 0; i < Urls.Count; i++)
		{
			if (i == exceptIndex) continue;
			if (string.Equals(Urls[i], candidate, StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	private void AddUrl(string url)
	{
		if (string.IsNullOrEmpty(url)) return;
		if (IsDuplicate(url))
		{
			MessageBox.Show(this, "That source URL is already in the list.", "Skill Sources",
				MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}
		Urls.Add(url);
		ReloadList();
		_list.SelectedIndex = Urls.Count - 1;
	}

	private void OnRemove()
	{
		int i = _list.SelectedIndex;
		if (i < 0) return;
		Urls.RemoveAt(i);
		ReloadList();
		if (Urls.Count > 0)
			_list.SelectedIndex = Math.Min(i, Urls.Count - 1);
	}

	private void OnMove(int delta)
	{
		int i = _list.SelectedIndex;
		int j = i + delta;
		if (i < 0 || j < 0 || j >= Urls.Count) return;
		(Urls[i], Urls[j]) = (Urls[j], Urls[i]);
		ReloadList();
		_list.SelectedIndex = j;
	}

	private void OnOk()
	{
		DialogResult = ListChanged() ? DialogResult.OK : DialogResult.Cancel;
		Close();
	}

	private bool ListChanged()
	{
		if (Urls.Count != _original.Count) return true;
		for (int i = 0; i < Urls.Count; i++)
		{
			if (!string.Equals(Urls[i], _original[i], StringComparison.OrdinalIgnoreCase))
				return true;
		}
		return false;
	}

	/// <summary>
	/// Tiny modal that asks the user for a URL and validates it against
	/// <see cref="CatalogSource.Parse"/>. Refuses to close on OK if the URL
	/// is unparseable, with a "Save anyway" escape hatch for forward
	/// compatibility (a future provider added later may parse URLs we don't
	/// recognise today).
	/// </summary>
	private sealed class UrlInputDialog : Form
	{
		private readonly TextBox _textBox = new();
		private readonly Label   _hintLabel = new();
		private readonly Button  _ok = new();
		private readonly Button  _cancel = new();

		public string Url { get; private set; } = "";

		public UrlInputDialog(string initial)
		{
			Text            = "Add Source";
			FormBorderStyle = FormBorderStyle.FixedDialog;
			StartPosition   = FormStartPosition.CenterParent;
			MinimizeBox     = false;
			MaximizeBox     = false;
			ShowInTaskbar   = false;
			ClientSize      = new Size(560, 160);
			BackColor       = AppTheme.Background;
			ForeColor       = AppTheme.TextPrimary;
			Font            = new Font("Segoe UI", 9F);
			KeyPreview      = true;

			var label = new Label
			{
				Text     = "Source URL (GitHub or Azure DevOps):",
				AutoSize = false,
				Bounds   = new Rectangle(12, 12, 540, 18),
				ForeColor = AppTheme.TextMuted,
			};

			_textBox.Bounds   = new Rectangle(12, 34, 536, 22);
			_textBox.Text     = initial;
			_textBox.BackColor = AppTheme.InputBox;
			_textBox.ForeColor = AppTheme.TextPrimary;
			_textBox.BorderStyle = BorderStyle.FixedSingle;
			_textBox.TextChanged += (_, _) => UpdateHint();

			_hintLabel.Bounds   = new Rectangle(12, 62, 536, 36);
			_hintLabel.AutoSize = false;
			_hintLabel.ForeColor = AppTheme.TextMuted;
			_hintLabel.Text     = "";

			_ok.Text = "OK";       _ok.Bounds     = new Rectangle(366, 118, 90, 28);
			_cancel.Text = "Cancel"; _cancel.Bounds = new Rectangle(458, 118, 90, 28);
			StyleButton(_ok); StyleButton(_cancel);

			_ok.Click     += (_, _) => OnOk();
			_cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

			Controls.Add(label);
			Controls.Add(_textBox);
			Controls.Add(_hintLabel);
			Controls.Add(_ok);
			Controls.Add(_cancel);
			AcceptButton = _ok;
			CancelButton = _cancel;
			UpdateHint();
		}

		private static void StyleButton(Button b)
		{
			b.FlatStyle = FlatStyle.Flat;
			b.FlatAppearance.BorderColor = AppTheme.ButtonBorder;
			b.BackColor = AppTheme.ButtonBg;
			b.ForeColor = AppTheme.TextPrimary;
			b.UseVisualStyleBackColor = false;
		}

		private void UpdateHint()
		{
			var u = _textBox.Text.Trim();
			if (u.Length == 0)
			{
				_hintLabel.Text = "";
				return;
			}
			if (CatalogSource.TryParse(u, out var src, out var err))
			{
				_hintLabel.Text = $"Recognised as: {src!.Kind} ({src.MakeLabel()})";
				_hintLabel.ForeColor = AppTheme.TextMuted;
			}
			else
			{
				_hintLabel.Text = $"Not recognised: {err}";
				_hintLabel.ForeColor = Color.FromArgb(220, 120, 120);
			}
		}

		private void OnOk()
		{
			var u = _textBox.Text.Trim();
			if (u.Length == 0)
			{
				DialogResult = DialogResult.Cancel;
				Close();
				return;
			}
			if (!CatalogSource.TryParse(u, out _, out var err))
			{
				var resp = MessageBox.Show(this,
					$"This URL is not recognised by GoPilot ({err}).\r\n\r\nSave anyway?",
					"Add Source", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
				if (resp != DialogResult.Yes) return;
			}
			Url = u;
			DialogResult = DialogResult.OK;
			Close();
		}
	}
}
