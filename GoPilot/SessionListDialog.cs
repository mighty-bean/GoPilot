namespace GoPilot;

/// <summary>
/// Modal dialog for browsing persisted Copilot sessions. Supports both
/// resuming a single past session and deleting one or more sessions in
/// bulk without leaving the dialog. Follows the dark visual language of
/// <see cref="ReferenceListDialog"/>.
/// </summary>
internal sealed class SessionListDialog : Form
{
	private readonly ListView _list;
	private readonly Button _resumeBtn;
	private readonly Button _deleteBtn;
	private readonly Button _closeBtn;
	private readonly Label _statusLabel;
	private readonly string? _currentSessionId;
	private readonly Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>>? _deleteAsync;

	/// <summary>
	/// The session row the user chose to resume. Null unless the dialog
	/// closes with <see cref="DialogResult.OK"/> via the Resume button.
	/// </summary>
	public SessionRow? ResumeSelected { get; private set; }

	internal sealed class SessionRow
	{
		public string SessionId { get; init; } = "";
		public string Workspace { get; init; } = "";
		public string Model { get; init; } = "";
		public string Mode { get; init; } = "";
		public DateTime CreatedAt { get; init; }
		public string Description { get; init; } = "";
		public SessionMetadataEntry? Metadata { get; init; }
	}

	/// <summary>
	/// Creates the Past Sessions dialog.
	/// </summary>
	/// <param name="rows">Rows to display, typically already sorted newest-first.</param>
	/// <param name="currentSessionId">
	/// ID of the session currently connected, if any. The matching row is
	/// suffixed "(current)" and protected from both Resume and Delete.
	/// </param>
	/// <param name="deleteAsync">
	/// Callback invoked when the user confirms a delete. Receives the list of
	/// session IDs to delete and returns the IDs that failed (empty on full
	/// success). Successfully deleted rows are removed from the list view.
	/// </param>
	public SessionListDialog(
		IReadOnlyList<SessionRow> rows,
		string? currentSessionId,
		Func<IReadOnlyList<string>, Task<IReadOnlyList<string>>> deleteAsync)
	{
		_currentSessionId = currentSessionId;
		_deleteAsync      = deleteAsync;

		AutoScaleDimensions = new SizeF(7F, 15F);
		AutoScaleMode       = AutoScaleMode.Font;

		Text            = "Past Sessions";
		StartPosition   = FormStartPosition.CenterParent;
		FormBorderStyle = FormBorderStyle.Sizable;
		MinimumSize     = new Size(700, 360);
		Size            = new Size(900, 480);
		BackColor       = AppTheme.Background;
		ForeColor       = AppTheme.TextPrimary;
		Font            = new Font("Segoe UI", 9F);
		ShowInTaskbar   = false;
		KeyPreview      = true;

		_list = new ListView
		{
			Dock          = DockStyle.Fill,
			View          = View.Details,
			FullRowSelect = true,
			GridLines     = false,
			HideSelection = false,
			MultiSelect   = true,
			BackColor     = AppTheme.InputBox,
			ForeColor     = AppTheme.TextPrimary,
			BorderStyle   = BorderStyle.FixedSingle,
			OwnerDraw     = true,
		};

		_list.DrawColumnHeader += (_, e) =>
		{
			using var bg = new SolidBrush(AppTheme.Surface);
			e.Graphics.FillRectangle(bg, e.Bounds);
			using var pen = new Pen(AppTheme.ButtonBorder);
			e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1,
				e.Bounds.Right, e.Bounds.Bottom - 1);
			TextRenderer.DrawText(e.Graphics, e.Header!.Text, Font, e.Bounds,
				AppTheme.TextPrimary,
				TextFormatFlags.Left | TextFormatFlags.VerticalCenter
					| TextFormatFlags.EndEllipsis);
		};
		_list.DrawSubItem += (_, e) => e.DrawDefault = true;
		_list.DrawItem    += (_, e) => e.DrawDefault = true;

		_list.Columns.Add("Session ID",  280);
		_list.Columns.Add("Workspace",   200);
		_list.Columns.Add("Model",       140);
		_list.Columns.Add("Mode",         80);
		_list.Columns.Add("Created",     140);
		_list.Columns.Add("Description", 500);

		foreach (var row in rows)
			_list.Items.Add(BuildItem(row));

		// Select first non-current row by default
		foreach (ListViewItem item in _list.Items)
		{
			if (!IsCurrent(item))
			{
				item.Selected = true;
				break;
			}
		}

		_list.SelectedIndexChanged += (_, _) => UpdateButtonState();
		_list.DoubleClick          += (_, _) =>
		{
			if (_resumeBtn!.Enabled) DoResume();
		};

		// Button panel
		var buttonPanel = new Panel
		{
			Dock      = DockStyle.Bottom,
			Height    = 44,
			BackColor = AppTheme.Background,
			Padding   = new Padding(8, 6, 8, 6),
		};

		_statusLabel = new Label
		{
			AutoSize  = false,
			Dock      = DockStyle.Left,
			Width     = 320,
			TextAlign = ContentAlignment.MiddleLeft,
			ForeColor = AppTheme.TextMuted,
			BackColor = AppTheme.Background,
			Padding   = new Padding(4, 0, 0, 0),
		};

		_resumeBtn = new Button
		{
			Text      = "Resume",
			FlatStyle = FlatStyle.Flat,
			BackColor = AppTheme.AccentBg,
			ForeColor = AppTheme.AccentText,
			Font      = new Font("Segoe UI", 9F, FontStyle.Bold),
			Size      = new Size(100, 30),
			Anchor    = AnchorStyles.Right,
		};
		_resumeBtn.FlatAppearance.BorderSize = 0;
		_resumeBtn.Click += (_, _) => DoResume();

		_deleteBtn = new Button
		{
			Text      = "Delete",
			FlatStyle = FlatStyle.Flat,
			BackColor = AppTheme.ButtonBg,
			ForeColor = AppTheme.TextPrimary,
			Font      = new Font("Segoe UI", 9F),
			Size      = new Size(100, 30),
			Anchor    = AnchorStyles.Right,
		};
		_deleteBtn.FlatAppearance.BorderColor = AppTheme.ButtonBorder;
		_deleteBtn.Click += async (_, _) => await DoDeleteAsync();

		_closeBtn = new Button
		{
			Text      = "Close",
			FlatStyle = FlatStyle.Flat,
			BackColor = AppTheme.ButtonBg,
			ForeColor = AppTheme.TextPrimary,
			Font      = new Font("Segoe UI", 9F),
			Size      = new Size(80, 30),
			Anchor    = AnchorStyles.Right,
		};
		_closeBtn.FlatAppearance.BorderColor = AppTheme.ButtonBorder;
		_closeBtn.Click += (_, _) =>
		{
			DialogResult = DialogResult.Cancel;
			Close();
		};

		buttonPanel.Controls.Add(_statusLabel);
		buttonPanel.Controls.Add(_closeBtn);
		buttonPanel.Controls.Add(_deleteBtn);
		buttonPanel.Controls.Add(_resumeBtn);

		void LayoutButtons()
		{
			int right = buttonPanel.ClientSize.Width - 8;
			_closeBtn.Location  = new Point(right - _closeBtn.Width, 6);
			_deleteBtn.Location = new Point(_closeBtn.Left - _deleteBtn.Width - 8, 6);
			_resumeBtn.Location = new Point(_deleteBtn.Left - _resumeBtn.Width - 8, 6);
		}
		LayoutButtons();
		buttonPanel.Resize += (_, _) => LayoutButtons();

		Controls.Add(_list);
		Controls.Add(buttonPanel);

		KeyDown += (_, e) =>
		{
			if (e.KeyCode == Keys.Escape)
			{
				DialogResult = DialogResult.Cancel;
				Close();
			}
			else if (e.KeyCode == Keys.Delete && _deleteBtn.Enabled)
			{
				_ = DoDeleteAsync();
			}
		};

		UpdateButtonState();
	}

	private ListViewItem BuildItem(SessionRow row)
	{
		bool isCurrent = _currentSessionId != null
			&& string.Equals(row.SessionId, _currentSessionId, StringComparison.Ordinal);

		var idText = isCurrent ? row.SessionId + "  (current)" : row.SessionId;
		var item = new ListViewItem(idText) { Tag = row };
		item.SubItems.Add(row.Workspace);
		item.SubItems.Add(row.Model);
		item.SubItems.Add(row.Mode);
		item.SubItems.Add(row.CreatedAt == default ? "" : row.CreatedAt.ToString("g"));
		item.SubItems.Add(row.Description ?? "");
		if (isCurrent)
			item.ForeColor = AppTheme.TextMuted;
		return item;
	}

	private bool IsCurrent(ListViewItem item) =>
		_currentSessionId != null
		&& item.Tag is SessionRow row
		&& string.Equals(row.SessionId, _currentSessionId, StringComparison.Ordinal);

	private void UpdateButtonState()
	{
		var selected = _list.SelectedItems.Cast<ListViewItem>().ToList();
		var deletable = selected.Where(i => !IsCurrent(i)).ToList();

		// Resume requires exactly one non-current selection
		_resumeBtn.Enabled = selected.Count == 1 && !IsCurrent(selected[0]);

		// Delete requires at least one non-current selection
		_deleteBtn.Enabled = deletable.Count > 0;

		if (selected.Count == 0)
			_statusLabel.Text = $"{_list.Items.Count} session{(_list.Items.Count == 1 ? "" : "s")}";
		else if (selected.Count == 1 && IsCurrent(selected[0]))
			_statusLabel.Text = "Current session: cannot resume or delete";
		else
			_statusLabel.Text = $"{selected.Count} selected"
				+ (selected.Count != deletable.Count ? " (current excluded)" : "");
	}

	private void DoResume()
	{
		var selected = _list.SelectedItems.Cast<ListViewItem>().ToList();
		if (selected.Count != 1) return;
		if (IsCurrent(selected[0])) return;
		if (selected[0].Tag is not SessionRow row) return;

		ResumeSelected = row;
		DialogResult   = DialogResult.OK;
		Close();
	}

	private async Task DoDeleteAsync()
	{
		if (_deleteAsync is null) return;

		var selected = _list.SelectedItems.Cast<ListViewItem>()
			.Where(i => !IsCurrent(i))
			.ToList();
		if (selected.Count == 0) return;

		var ids = selected
			.Select(i => ((SessionRow)i.Tag!).SessionId)
			.ToList();

		var label = ids.Count == 1 ? ids[0] : $"{ids.Count} sessions";
		var confirm = MessageBox.Show(this,
			$"Permanently delete {label}?\r\n\r\nThis cannot be undone.",
			"Confirm Delete",
			MessageBoxButtons.YesNo,
			MessageBoxIcon.Warning,
			MessageBoxDefaultButton.Button2);
		if (confirm != DialogResult.Yes) return;

		// Disable controls during the async delete to prevent re-entrancy
		_resumeBtn.Enabled = false;
		_deleteBtn.Enabled = false;
		_list.Enabled      = false;
		_statusLabel.Text  = $"Deleting {ids.Count}...";

		IReadOnlyList<string> failed;
		try
		{
			failed = await _deleteAsync(ids);
		}
		catch (Exception ex)
		{
			MessageBox.Show(this,
				$"Delete failed:\r\n\r\n{ex.Message}",
				"Delete Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
			_list.Enabled = true;
			UpdateButtonState();
			return;
		}

		var failedSet = new HashSet<string>(failed, StringComparer.Ordinal);
		foreach (var item in selected)
		{
			if (item.Tag is SessionRow r && !failedSet.Contains(r.SessionId))
				_list.Items.Remove(item);
		}

		if (failedSet.Count > 0)
		{
			MessageBox.Show(this,
				$"Failed to delete {failedSet.Count} session(s):\r\n\r\n"
					+ string.Join("\r\n", failedSet),
				"Delete Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
		}

		_list.Enabled = true;
		UpdateButtonState();
	}
}
