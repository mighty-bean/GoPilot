using System.IO;
using System.Linq;

namespace GoPilot;

/// <summary>
/// Displays and edits the contents of <c>~/.copilot/permissions-config.json</c>.
///
/// Left pane: list of trusted workspace folders.
/// Right pane: per-folder toggle for each known pre-approved operation kind.
///
/// Adding a folder marks it trusted (appears in the locations map).
/// Revoking a folder removes it entirely -- both trust and all approvals.
/// Toggling an operation kind adds/removes it from tool_approvals for that folder.
/// Unknown kind values found in the file are shown read-only and always preserved
/// on save so no CLI-managed data is silently dropped.
/// </summary>
internal sealed class WorkspaceTrustDialog : Form
{
	// ── State ─────────────────────────────────────────────────────────────────

	private readonly string? _activeFolder;   // currently-connected CWD (may be null)
	private readonly string? _preselectFolder; // folder to select on Load (may be null)
	private PermissionsData   _data;
	private bool              _dirty;
	private bool              _suppressChecks; // prevents check events during programmatic population

	// Snapshot of the active folder's permission state at dialog open time, used
	// to detect whether the user's saved changes touched the currently-connected
	// workspace (in which case the caller should restart the session for them to
	// take effect).  Null when there is no active folder.
	private readonly bool                _initialActiveTrusted;
	private readonly HashSet<string>?    _initialActiveKinds;

	/// <summary>
	/// True after Save when the user's changes affected the currently-connected
	/// workspace folder (revoked entirely, newly added, or kinds added/removed).
	/// The caller should schedule a summary-and-restart handoff so the CLI picks
	/// up the new permission state -- the CLI reads permissions-config.json only
	/// at session startup.
	/// </summary>
	public bool AffectedActiveFolder { get; private set; }

	// ── Controls ──────────────────────────────────────────────────────────────

	private readonly ListView    _folderList;
	private readonly Button      _addBtn;
	private readonly Button      _revokeBtn;
	private readonly Label       _rightHeader;
	private readonly Panel       _checkPanel;   // scrollable; contains _kindBoxes + _unknownLabel
	private readonly CheckBox[]  _kindBoxes;
	private readonly Label       _unknownLabel;
	private readonly Button      _saveBtn;
	private readonly Button      _cancelBtn;

	// Entry whose permissions are shown on the right (null = nothing selected).
	private LocationEntry? _selectedEntry;

	// ── Constructor ───────────────────────────────────────────────────────────

	public WorkspaceTrustDialog(string? activeFolder)
		: this(activeFolder, preselectFolder: activeFolder)
	{
	}

	/// <summary>
	/// Constructs the dialog and, if <paramref name="preselectFolder"/> resolves
	/// to one of the persisted location entries, selects it and scrolls it into
	/// view.  Falls back to selecting the first entry when not matched.  Used by
	/// the Options menu's "pre-approvals" shortcut to land the user directly on
	/// the currently-connected workspace.
	/// </summary>
	public WorkspaceTrustDialog(string? activeFolder, string? preselectFolder)
	{
		SuspendLayout();
		_activeFolder     = activeFolder;
		_preselectFolder  = preselectFolder;
		_data             = CopilotPermissionsConfig.LoadAll();

		// Capture the active folder's initial state so SaveBtn_Click can decide
		// whether the workspace was actually touched.
		if (!string.IsNullOrEmpty(_activeFolder))
		{
			var key     = CopilotPermissionsConfig.Normalize(_activeFolder);
			var initial = _data.Locations.FirstOrDefault(e =>
				CopilotPermissionsConfig.Normalize(e.FolderPath)
					.Equals(key, StringComparison.OrdinalIgnoreCase));
			_initialActiveTrusted = initial != null;
			_initialActiveKinds   = initial != null
				? new HashSet<string>(initial.ApprovedKinds,
					StringComparer.OrdinalIgnoreCase)
				: null;
		}

		AutoScaleDimensions = new SizeF(7F, 15F);
		AutoScaleMode       = AutoScaleMode.Font;

		Text            = "Workspace Permissions";
		StartPosition   = FormStartPosition.CenterParent;
		FormBorderStyle = FormBorderStyle.Sizable;
		MinimumSize     = new Size(680, 400);
		Size            = new Size(820, 520);
		BackColor       = AppTheme.Background;
		ForeColor       = AppTheme.TextPrimary;
		Font            = new Font("Segoe UI", 9F);
		ShowInTaskbar   = false;
		KeyPreview      = true;

		// ── Split container ───────────────────────────────────────────────────

		var split = new SplitContainer
		{
			Dock      = DockStyle.Fill,
			BackColor = AppTheme.Background,
		};

		// ── Left pane: folder list ────────────────────────────────────────────

		_folderList = new ListView
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

		_folderList.DrawColumnHeader += (_, e) =>
		{
			using var bg  = new SolidBrush(AppTheme.Surface);
			using var pen = new Pen(AppTheme.ButtonBorder);
			e.Graphics.FillRectangle(bg, e.Bounds);
			e.Graphics.DrawLine(pen,
				e.Bounds.Left,  e.Bounds.Bottom - 1,
				e.Bounds.Right, e.Bounds.Bottom - 1);
			TextRenderer.DrawText(e.Graphics, e.Header!.Text, Font, e.Bounds,
				AppTheme.TextPrimary,
				TextFormatFlags.Left | TextFormatFlags.VerticalCenter
					| TextFormatFlags.EndEllipsis);
		};
		_folderList.DrawSubItem += (_, e) => e.DrawDefault = true;
		_folderList.DrawItem    += (_, e) => e.DrawDefault = true;

		_folderList.Columns.Add("Trusted Folder", 268);
		_folderList.Columns.Add("Approvals",        64);

		foreach (var entry in _data.Locations)
			_folderList.Items.Add(BuildListItem(entry));

		_folderList.SelectedIndexChanged += FolderList_SelectionChanged;

		var leftButtons = new Panel
		{
			Dock      = DockStyle.Bottom,
			Height    = 36,
			BackColor = AppTheme.Background,
			Padding   = new Padding(4, 4, 4, 4),
		};

		_addBtn = new Button
		{
			Text      = "Add Folder",
			FlatStyle = FlatStyle.Flat,
			BackColor = AppTheme.ButtonBg,
			ForeColor = AppTheme.TextPrimary,
			Size      = new Size(90, 26),
			Location  = new Point(4, 5),
		};
		_addBtn.FlatAppearance.BorderColor = AppTheme.ButtonBorder;
		_addBtn.Click += AddFolder_Click;

		_revokeBtn = new Button
		{
			Text      = "Revoke",
			FlatStyle = FlatStyle.Flat,
			BackColor = AppTheme.ButtonBg,
			ForeColor = AppTheme.TextPrimary,
			Size      = new Size(70, 26),
			Location  = new Point(98, 5),
		};
		_revokeBtn.FlatAppearance.BorderColor = AppTheme.ButtonBorder;
		_revokeBtn.Click += RevokeFolder_Click;

		leftButtons.Controls.Add(_addBtn);
		leftButtons.Controls.Add(_revokeBtn);

		split.Panel1.Controls.Add(_folderList);
		split.Panel1.Controls.Add(leftButtons);

		// ── Right pane: permission checkboxes ─────────────────────────────────

		_rightHeader = new Label
		{
			Dock      = DockStyle.Top,
			Height    = 38,
			Padding   = new Padding(10, 10, 0, 0),
			Text      = "Select a folder to view its permissions",
			ForeColor = AppTheme.TextMuted,
			BackColor = AppTheme.Surface,
		};

		_checkPanel = new Panel
		{
			Dock       = DockStyle.Fill,
			BackColor  = AppTheme.InputBox,
			AutoScroll = true,
		};

		_kindBoxes = new CheckBox[CopilotPermissionsConfig.KnownKinds.Count];
		int y = 10;
		for (int i = 0; i < CopilotPermissionsConfig.KnownKinds.Count; i++)
		{
			var (kind, label) = CopilotPermissionsConfig.KnownKinds[i];
			var cb = new CheckBox
			{
				Text      = label,
				Tag       = kind,
				AutoSize  = false,
				Location  = new Point(12, y),
				Width     = 220,
				Height    = 24,
				ForeColor = AppTheme.TextPrimary,
				BackColor = AppTheme.InputBox,
				FlatStyle = FlatStyle.Flat,
				Enabled   = false,
			};
			cb.CheckedChanged += KindBox_CheckedChanged;
			_checkPanel.Controls.Add(cb);
			_kindBoxes[i] = cb;
			y += 26;
		}

		_unknownLabel = new Label
		{
			AutoSize  = false,
			Location  = new Point(12, y + 8),
			Width     = 360,
			Height    = 36,
			ForeColor = AppTheme.TextMuted,
			BackColor = AppTheme.InputBox,
			Visible   = false,
		};
		_checkPanel.Controls.Add(_unknownLabel);

		// Order matters: Fill control added before Top so docking resolves correctly.
		split.Panel2.Controls.Add(_checkPanel);
		split.Panel2.Controls.Add(_rightHeader);

		// ── Bottom strip ──────────────────────────────────────────────────────

		var bottomPanel = new Panel
		{
			Dock      = DockStyle.Bottom,
			Height    = 44,
			BackColor = AppTheme.Background,
			Padding   = new Padding(8, 6, 8, 6),
		};

		var noteLabel = new Label
		{
			AutoSize  = false,
			Dock      = DockStyle.Left,
			Width     = 380,
			TextAlign = ContentAlignment.MiddleLeft,
			ForeColor = AppTheme.TextMuted,
			BackColor = AppTheme.Background,
			Padding   = new Padding(4, 0, 0, 0),
			Text      = "Changes affecting the current workspace restart the session.",
		};

		_saveBtn = new Button
		{
			Text      = "Save",
			FlatStyle = FlatStyle.Flat,
			BackColor = AppTheme.AccentBg,
			ForeColor = AppTheme.AccentText,
			Font      = new Font("Segoe UI", 9F, FontStyle.Bold),
			Size      = new Size(80, 30),
			Anchor    = AnchorStyles.Right,
		};
		_saveBtn.FlatAppearance.BorderSize = 0;
		_saveBtn.Click += SaveBtn_Click;

		_cancelBtn = new Button
		{
			Text      = "Cancel",
			FlatStyle = FlatStyle.Flat,
			BackColor = AppTheme.ButtonBg,
			ForeColor = AppTheme.TextPrimary,
			Font      = new Font("Segoe UI", 9F),
			Size      = new Size(80, 30),
			Anchor    = AnchorStyles.Right,
		};
		_cancelBtn.FlatAppearance.BorderColor = AppTheme.ButtonBorder;
		_cancelBtn.Click += CancelBtn_Click;

		bottomPanel.Controls.Add(noteLabel);
		bottomPanel.Controls.Add(_cancelBtn);
		bottomPanel.Controls.Add(_saveBtn);

		void LayoutBottom()
		{
			int right = bottomPanel.ClientSize.Width - 8;
			_cancelBtn.Location = new Point(right - _cancelBtn.Width, 6);
			_saveBtn.Location   = new Point(_cancelBtn.Left - _saveBtn.Width - 8, 6);
		}
		LayoutBottom();
		bottomPanel.Resize += (_, _) => LayoutBottom();

		// Fill first, Bottom second -- WinForms docks in reverse z-order.
		Controls.Add(split);
		Controls.Add(bottomPanel);

		Load += (_, _) =>
		{
			// All SplitContainer size constraints must be deferred to Load.
			// Setting Panel1/2MinSize or SplitterDistance during construction
			// triggers validation against the control's default 150px width
			// and throws InvalidOperationException.
			split.Panel1MinSize    = LogicalToDeviceUnits(200);
			split.Panel2MinSize    = LogicalToDeviceUnits(240);
			split.SplitterDistance = Math.Max(
				split.Panel1MinSize,
				Math.Min(ClientSize.Width - split.Panel2MinSize - split.SplitterWidth,
					(int)(ClientSize.Width * 0.44)));
		};

		KeyDown += (_, e) =>
		{
			if (e.KeyCode == Keys.Escape) _cancelBtn.PerformClick();
		};

		// Initial state: prefer the explicit preselectFolder match; fall back
		// to the first entry so the right pane is never blank when entries exist.
		ListViewItem? toSelect = null;
		if (!string.IsNullOrEmpty(_preselectFolder))
		{
			var key = CopilotPermissionsConfig.Normalize(_preselectFolder);
			foreach (ListViewItem lvi in _folderList.Items)
			{
				if (lvi.Tag is LocationEntry entry
					&& CopilotPermissionsConfig.Normalize(entry.FolderPath)
						.Equals(key, StringComparison.OrdinalIgnoreCase))
				{
					toSelect = lvi;
					break;
				}
			}
		}
		toSelect ??= _folderList.Items.Count > 0 ? _folderList.Items[0] : null;
		if (toSelect != null)
		{
			toSelect.Selected = true;
			toSelect.EnsureVisible();
		}

		UpdateButtonState();
		ResumeLayout(false);
		PerformLayout();
	}

	// ── List item builder ─────────────────────────────────────────────────────

	private ListViewItem BuildListItem(LocationEntry entry)
	{
		bool exists    = Directory.Exists(entry.FolderPath);
		bool isCurrent = !string.IsNullOrEmpty(_activeFolder)
			&& CopilotPermissionsConfig.Normalize(entry.FolderPath)
				.Equals(CopilotPermissionsConfig.Normalize(_activeFolder),
					StringComparison.OrdinalIgnoreCase);

		var count = entry.ApprovedKinds.Count;
		var color = isCurrent  ? AppTheme.AccentBg
			: exists           ? AppTheme.TextPrimary
			: Color.FromArgb(210, 160, 50);  // amber = folder not found on disk

		var item = new ListViewItem(entry.FolderPath)
		{
			Tag       = entry,
			ForeColor = color,
		};
		item.SubItems.Add(count == 0 ? "-" : count.ToString());
		return item;
	}

	// ── Event handlers ────────────────────────────────────────────────────────

	private void FolderList_SelectionChanged(object? sender, EventArgs e)
	{
		_selectedEntry = _folderList.SelectedItems.Count == 1
			? _folderList.SelectedItems[0].Tag as LocationEntry
			: null;

		PopulateRightPane(_selectedEntry);
		UpdateButtonState();
	}

	private void KindBox_CheckedChanged(object? sender, EventArgs e)
	{
		if (_suppressChecks || _selectedEntry == null || sender is not CheckBox cb)
			return;

		var kind = (string)cb.Tag!;
		if (cb.Checked) _selectedEntry.ApprovedKinds.Add(kind);
		else            _selectedEntry.ApprovedKinds.Remove(kind);

		RefreshApprovalCount(_selectedEntry);
		MarkDirty();
	}

	private void AddFolder_Click(object? sender, EventArgs e)
	{
		using var dlg = new FolderBrowserDialog
		{
			Description         = "Select a folder to add as a trusted workspace",
			UseDescriptionForTitle = true,
		};
		if (dlg.ShowDialog(this) != DialogResult.OK) return;

		var path = dlg.SelectedPath;
		var key  = CopilotPermissionsConfig.Normalize(path);

		// Select existing entry if already present.
		foreach (ListViewItem lvi in _folderList.Items)
		{
			if (lvi.Tag is LocationEntry ex
				&& CopilotPermissionsConfig.Normalize(ex.FolderPath)
					.Equals(key, StringComparison.OrdinalIgnoreCase))
			{
				_folderList.SelectedItems.Clear();
				lvi.Selected = true;
				lvi.EnsureVisible();
				return;
			}
		}

		var entry = new LocationEntry { FolderPath = path };
		_data.Locations.Add(entry);
		var item = BuildListItem(entry);
		_folderList.Items.Add(item);
		_folderList.SelectedItems.Clear();
		item.Selected = true;
		item.EnsureVisible();
		MarkDirty();
	}

	private void RevokeFolder_Click(object? sender, EventArgs e)
	{
		var selected = _folderList.SelectedItems.Cast<ListViewItem>().ToList();
		if (selected.Count == 0) return;

		bool removingActive = !string.IsNullOrEmpty(_activeFolder)
			&& selected.Any(lvi => lvi.Tag is LocationEntry entry
				&& CopilotPermissionsConfig.Normalize(entry.FolderPath)
					.Equals(CopilotPermissionsConfig.Normalize(_activeFolder),
						StringComparison.OrdinalIgnoreCase));

		var noun    = selected.Count == 1 ? "this folder" : $"these {selected.Count} folders";
		var extra   = removingActive
			? "\r\n\r\nThis includes the currently-connected folder. GoPilot will prompt for trust again on the next connection."
			: "";
		var confirm = MessageBox.Show(
			$"Remove {noun} from the trusted list?\r\n\r\n"
			+ $"All pre-approved operations will also be revoked.{extra}",
			"Revoke trust?",
			MessageBoxButtons.YesNo,
			MessageBoxIcon.Warning,
			MessageBoxDefaultButton.Button2);

		if (confirm != DialogResult.Yes) return;

		foreach (var lvi in selected)
		{
			if (lvi.Tag is LocationEntry entry)
				_data.Locations.Remove(entry);
			_folderList.Items.Remove(lvi);
		}

		MarkDirty();
	}

	private void SaveBtn_Click(object? sender, EventArgs e)
	{
		AffectedActiveFolder = ActiveFolderStateChanged();
		CopilotPermissionsConfig.SaveAll(_data);
		_dirty        = false;
		DialogResult  = DialogResult.OK;
		Close();
	}

	/// <summary>
	/// True when the currently-connected workspace folder's trust or
	/// per-kind approvals differ from the snapshot captured when the dialog
	/// opened.  Returns false when there is no active workspace.
	/// </summary>
	private bool ActiveFolderStateChanged()
	{
		if (string.IsNullOrEmpty(_activeFolder)) return false;

		var key     = CopilotPermissionsConfig.Normalize(_activeFolder);
		var current = _data.Locations.FirstOrDefault(e =>
			CopilotPermissionsConfig.Normalize(e.FolderPath)
				.Equals(key, StringComparison.OrdinalIgnoreCase));
		var nowTrusted = current != null;

		if (nowTrusted != _initialActiveTrusted) return true;
		if (!nowTrusted) return false; // wasn't trusted, still isn't

		var before = _initialActiveKinds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var after  = current!.ApprovedKinds;
		return !before.SetEquals(after);
	}

	private void CancelBtn_Click(object? sender, EventArgs e)
	{
		if (_dirty)
		{
			var r = MessageBox.Show(
				"You have unsaved changes. Discard them?",
				"Discard changes?",
				MessageBoxButtons.YesNo,
				MessageBoxIcon.Question,
				MessageBoxDefaultButton.Button2);
			if (r != DialogResult.Yes) return;
		}
		DialogResult = DialogResult.Cancel;
		Close();
	}

	// ── Right-pane population ─────────────────────────────────────────────────

	private void PopulateRightPane(LocationEntry? entry)
	{
		_suppressChecks = true;
		try
		{
			if (entry == null)
			{
				_rightHeader.Text      = "Select a folder to view its permissions";
				_rightHeader.ForeColor = AppTheme.TextMuted;
				foreach (var cb in _kindBoxes)
				{
					cb.Checked = false;
					cb.Enabled = false;
				}
				_unknownLabel.Visible = false;
				return;
			}

			_rightHeader.Text      = entry.FolderPath;
			_rightHeader.ForeColor = AppTheme.TextPrimary;

			foreach (var cb in _kindBoxes)
			{
				cb.Checked = entry.ApprovedKinds.Contains((string)cb.Tag!);
				cb.Enabled = true;
			}

			// Unknown kinds: shown read-only so the user knows they exist.
			var unknowns = entry.RawApprovals
				.Select(n => n["kind"]?.GetValue<string>())
				.Where(k => !string.IsNullOrEmpty(k)
					&& !CopilotPermissionsConfig.KnownKinds
						.Any(x => x.Kind.Equals(k, StringComparison.OrdinalIgnoreCase)))
				.ToList();

			if (unknowns.Count > 0)
			{
				_unknownLabel.Text    = $"Also approved (unrecognised, preserved): {string.Join(", ", unknowns)}";
				_unknownLabel.Visible = true;
			}
			else
			{
				_unknownLabel.Visible = false;
			}
		}
		finally
		{
			_suppressChecks = false;
		}
	}

	// ── Helpers ───────────────────────────────────────────────────────────────

	private void RefreshApprovalCount(LocationEntry entry)
	{
		foreach (ListViewItem lvi in _folderList.Items)
		{
			if (lvi.Tag != entry) continue;
			var count = entry.ApprovedKinds.Count;
			lvi.SubItems[1].Text = count == 0 ? "-" : count.ToString();
			break;
		}
	}

	private void MarkDirty()
	{
		_dirty = true;
		Text   = "Workspace Permissions *";
	}

	private void UpdateButtonState()
	{
		_revokeBtn.Enabled = _folderList.SelectedItems.Count > 0;
	}
}
