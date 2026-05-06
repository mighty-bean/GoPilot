using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GoPilot;

/// <summary>
/// Generic list dialog for picking a custom agent or skill to insert into the
/// prompt. Renders a multi-column dark <see cref="ListView"/> sorted alphabetically
/// by name and returns the selected reference name in <see cref="SelectedName"/>.
/// </summary>
internal sealed class ReferenceListDialog : Form
{
	private readonly ListView _list;
	private readonly Button   _insertBtn;

	/// <summary>The name of the selected agent/skill/prompt, or null if the dialog was cancelled.</summary>
	public string? SelectedName { get; private set; }

	/// <summary>The on-disk path of the selected row (an agent file, a skill folder,
	/// or a prompt file), or null if the dialog was cancelled. Lets the caller treat
	/// the chosen entry as a regular file/folder attachment without having to look
	/// the path up again from the underlying cache.</summary>
	public string? SelectedPath { get; private set; }

	/// <summary>Holds both the reference name and the on-disk path so the
	/// context menu can reveal the item in Explorer.</summary>
	private sealed record RowTag(string Name, string Path, bool PathIsFile);

	private ReferenceListDialog(string title, IReadOnlyList<(string[] Cells, string Name, string Path, bool PathIsFile, string Tooltip)> rows, string[] columnHeaders, int[] columnWidths)
	{
		Text            = title;
		StartPosition   = FormStartPosition.CenterParent;
		FormBorderStyle = FormBorderStyle.Sizable;
		MinimumSize     = new Size(640, 360);
		Size            = new Size(820, 480);
		BackColor       = AppTheme.Background;
		ForeColor       = AppTheme.TextPrimary;
		Font            = new Font("Segoe UI", 9F);
		ShowInTaskbar   = false;

		_list = new ListView
		{
			Dock          = DockStyle.Fill,
			View          = View.Details,
			FullRowSelect = true,
			GridLines     = false,
			HideSelection = false,
			MultiSelect   = false,
			BackColor     = AppTheme.InputBox,
			ForeColor     = AppTheme.TextPrimary,
			BorderStyle   = BorderStyle.FixedSingle,
			OwnerDraw     = true,
		};

		// Owner-draw the header to match the dark palette (default is grey-on-grey).
		_list.DrawColumnHeader += (_, e) =>
		{
			using var bg = new SolidBrush(AppTheme.Surface);
			e.Graphics.FillRectangle(bg, e.Bounds);
			using var pen = new Pen(AppTheme.ButtonBorder);
			e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
			TextRenderer.DrawText(e.Graphics, e.Header!.Text, Font, e.Bounds, AppTheme.TextPrimary,
				TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
		};
		// Owner-drawing rows is more invasive than we need; defer to the system
		// for sub-items so platform selection rendering still works correctly.
		_list.DrawSubItem += (_, e) => e.DrawDefault = true;
		_list.DrawItem    += (_, e) => e.DrawDefault = true;

		for (int i = 0; i < columnHeaders.Length; i++)
			_list.Columns.Add(columnHeaders[i], columnWidths[i]);

		// Auto-size the last column to fill any remaining ListView width.
		// Without this, the area to the right of the final column paints with
		// the system default (near-white) and shows as a bright bar against
		// the dark theme. Keeping the last column flush with the right edge
		// both removes that gap and gives the description more room.
		void SizeLastColumnToFill()
		{
			if (_list.Columns.Count == 0) return;
			int fixedWidth = 0;
			for (int i = 0; i < _list.Columns.Count - 1; i++)
				fixedWidth += _list.Columns[i].Width;
			int available = _list.ClientSize.Width - fixedWidth;
			// Leave a couple of pixels so a vertical scrollbar, if it appears,
			// doesn't force a horizontal scrollbar.
			int target = Math.Max(80, available - 2);
			var last = _list.Columns[_list.Columns.Count - 1];
			if (last.Width != target) last.Width = target;
		}
		_list.Resize    += (_, _) => SizeLastColumnToFill();
		_list.HandleCreated += (_, _) =>
		{
			SizeLastColumnToFill();
			HeaderBackgroundPainter.Attach(_list, AppTheme.Surface);
		};

		foreach (var (cells, name, path, pathIsFile, tooltip) in rows)
		{
			var item = new ListViewItem(cells)
			{
				Tag         = new RowTag(name, path, pathIsFile),
				ToolTipText = tooltip,
			};
			_list.Items.Add(item);
		}
		_list.ShowItemToolTips = true;

		// ── Right-click context menu: reveal in Explorer ──────────────────
		var contextMenu = new ContextMenuStrip
		{
			BackColor = AppTheme.Surface,
			ForeColor = AppTheme.TextPrimary,
		};
		var revealItem = new ToolStripMenuItem("Show in &Explorer")
		{
			BackColor = AppTheme.Surface,
			ForeColor = AppTheme.TextPrimary,
		};
		revealItem.Click += (_, _) =>
		{
			if (_list.SelectedItems.Count == 0) return;
			if (_list.SelectedItems[0].Tag is RowTag tag)
				RevealInExplorer(tag);
		};
		contextMenu.Items.Add(revealItem);
		contextMenu.Opening += (_, e) =>
		{
			if (_list.SelectedItems.Count == 0)
			{
				e.Cancel = true;
				return;
			}
			revealItem.Enabled = _list.SelectedItems[0].Tag is RowTag t
				&& !string.IsNullOrEmpty(t.Path);
		};
		_list.ContextMenuStrip = contextMenu;

		// Make right-click select the row under the cursor before the menu opens
		// so the action targets what the user clicked, not whatever was selected.
		_list.MouseDown += (_, e) =>
		{
			if (e.Button != MouseButtons.Right) return;
			var hit = _list.HitTest(e.Location);
			if (hit.Item != null)
			{
				_list.SelectedItems.Clear();
				hit.Item.Selected = true;
				hit.Item.Focused  = true;
			}
		};

		var buttonPanel = new Panel
		{
			Dock      = DockStyle.Bottom,
			Height    = 44,
			BackColor = AppTheme.Surface,
			Padding   = new Padding(8),
		};

		_insertBtn = new Button
		{
			Text      = "Insert into Prompt",
			Width     = 150,
			Height    = 28,
			Dock      = DockStyle.Right,
			BackColor = AppTheme.AccentBg,
			ForeColor = AppTheme.AccentText,
			FlatStyle = FlatStyle.Flat,
			Enabled   = false,
		};
		_insertBtn.FlatAppearance.BorderColor = AppTheme.ButtonBorder;
		_insertBtn.Click += (_, _) => Accept();

		var cancelBtn = new Button
		{
			Text      = "Cancel",
			Width     = 90,
			Height    = 28,
			Dock      = DockStyle.Right,
			BackColor = AppTheme.ButtonBg,
			ForeColor = AppTheme.TextPrimary,
			FlatStyle = FlatStyle.Flat,
			Margin    = new Padding(0, 0, 8, 0),
			DialogResult = DialogResult.Cancel,
		};
		cancelBtn.FlatAppearance.BorderColor = AppTheme.ButtonBorder;

		// Right-aligned: add cancel first, then insert (Dock = Right stacks RTL).
		buttonPanel.Controls.Add(_insertBtn);
		var spacer = new Panel { Dock = DockStyle.Right, Width = 8, BackColor = AppTheme.Surface };
		buttonPanel.Controls.Add(spacer);
		buttonPanel.Controls.Add(cancelBtn);

		Controls.Add(_list);
		Controls.Add(buttonPanel);

		_list.SelectedIndexChanged += (_, _) =>
			_insertBtn.Enabled = _list.SelectedItems.Count > 0;
		_list.DoubleClick          += (_, _) => Accept();
		_list.KeyDown              += (_, e) =>
		{
			if (e.KeyCode == Keys.Enter)
			{
				Accept();
				e.Handled = true;
			}
		};

		AcceptButton = _insertBtn;
		CancelButton = cancelBtn;

		if (_list.Items.Count > 0)
		{
			_list.Items[0].Selected = true;
			_list.Items[0].Focused  = true;
			_list.Select();
		}
	}

	private void Accept()
	{
		if (_list.SelectedItems.Count == 0) return;
		if (_list.SelectedItems[0].Tag is RowTag tag)
		{
			SelectedName = tag.Name;
			SelectedPath = tag.Path;
		}
		DialogResult = DialogResult.OK;
		Close();
	}

	/// <summary>
	/// Opens Windows Explorer focused on the agent file or skill folder. For a
	/// file, the parent folder is opened with the file pre-selected; for a
	/// folder, the folder itself is opened.
	/// </summary>
	private void RevealInExplorer(RowTag tag)
	{
		try
		{
			if (string.IsNullOrEmpty(tag.Path)) return;

			if (tag.PathIsFile)
			{
				if (!System.IO.File.Exists(tag.Path))
				{
					MessageBox.Show(this, $"File no longer exists:\r\n{tag.Path}",
						"Show in Explorer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
					return;
				}
				System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{tag.Path}\"");
			}
			else
			{
				if (!System.IO.Directory.Exists(tag.Path))
				{
					MessageBox.Show(this, $"Folder no longer exists:\r\n{tag.Path}",
						"Show in Explorer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
					return;
				}
				System.Diagnostics.Process.Start("explorer.exe", $"\"{tag.Path}\"");
			}
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, $"Could not open Explorer:\r\n{ex.Message}",
				"Show in Explorer", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
	}

	/// <summary>Show a dialog listing custom agents.</summary>
	public static ReferenceListDialog ForAgents(IReadOnlyList<AgentInfo> agents)
	{
		var rows = agents
			.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
			.Select(a => (
				Cells:   new[]
				{
					a.Name,
					string.IsNullOrWhiteSpace(a.Description) ? "" : a.Description!,
				},
				Name:       a.Name,
				Path:       a.FilePath ?? "",
				PathIsFile: true,
				Tooltip:    $"{a.Name}\nTier: {a.Tier}\n{a.FilePath}\n\n{a.Description ?? "(no description)"}"
			))
			.ToList();

		return new ReferenceListDialog(
			title:          $"Agents in Session ({rows.Count})",
			rows:           rows,
			columnHeaders:  new[] { "Name", "Description" },
			columnWidths:   new[] { 200, 560 });
	}

	/// <summary>Show a dialog listing skills.</summary>
	public static ReferenceListDialog ForSkills(IReadOnlyList<SkillInfo> skills)
	{
		var rows = skills
			.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
			.Select(s => (
				Cells:   new[]
				{
					s.Name,
					string.IsNullOrWhiteSpace(s.Description) ? "" : s.Description!,
				},
				Name:       s.Name,
				Path:       s.FolderPath ?? "",
				PathIsFile: false,
				Tooltip:    $"{s.Name}\nTier: {s.Tier}\n{s.FolderPath}\n\n{s.Description ?? "(no description)"}"
			))
			.ToList();

		return new ReferenceListDialog(
			title:          $"Skills in Session ({rows.Count})",
			rows:           rows,
			columnHeaders:  new[] { "Name", "Description" },
			columnWidths:   new[] { 200, 560 });
	}

	/// <summary>Show a dialog listing prompt templates.</summary>
	public static ReferenceListDialog ForPrompts(IReadOnlyList<PromptInfo> prompts)
	{
		var rows = prompts
			.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
			.Select(p => (
				Cells:   new[]
				{
					p.Name,
					string.IsNullOrWhiteSpace(p.Description) ? "" : p.Description!,
				},
				Name:       p.Name,
				Path:       p.FilePath ?? "",
				PathIsFile: true,
				Tooltip:    $"{p.Name}\nTier: {p.Tier}\n{p.FilePath}\n\n{p.Description ?? "(no description)"}"
			))
			.ToList();

		return new ReferenceListDialog(
			title:          $"Prompts in Session ({rows.Count})",
			rows:           rows,
			columnHeaders:  new[] { "Name", "Description" },
			columnWidths:   new[] { 200, 560 });
	}
}

/// <summary>
/// Subclasses a <see cref="ListView"/>'s header control so the area past the
/// last column is painted with the dark theme colour instead of the system
/// default (which appears as a bright white/grey bar against the dark UI).
/// </summary>
internal static class HeaderBackgroundPainter
{
	private const int LVM_FIRST       = 0x1000;
	private const int LVM_GETHEADER   = LVM_FIRST + 31;
	private const int WM_PAINT        = 0x000F;
	private const int WM_ERASEBKGND   = 0x0014;

	[DllImport("user32.dll", CharSet = CharSet.Auto)]
	private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

	public static void Attach(ListView list, Color background)
	{
		var header = SendMessage(list.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
		if (header == IntPtr.Zero) return;
		new HeaderWindow(background).AssignHandle(header);
	}

	private sealed class HeaderWindow : NativeWindow
	{
		private readonly Color _bg;
		public HeaderWindow(Color bg) { _bg = bg; }

		protected override void WndProc(ref Message m)
		{
			if (m.Msg == WM_PAINT)
			{
				base.WndProc(ref m);
				// After default paint, overwrite the region to the right of the
				// last header item with the themed background colour.
				using var g = Graphics.FromHwnd(m.HWnd);
				var clip = g.VisibleClipBounds;
				int rightEdge = 0;
				// Query last item's right edge via HDM_GETITEMRECT is complex;
				// simpler: enumerate children by walking the native API. Instead
				// we use a heuristic — find the rightmost pixel that is not the
				// background by asking the parent ListView through its columns
				// is not available here. Use the control's client rect and fill
				// everything to the right of the parent's reported last column.
				var parent = Control.FromHandle(GetParent(m.HWnd)) as ListView;
				if (parent != null)
				{
					foreach (ColumnHeader col in parent.Columns)
						rightEdge += col.Width;
					rightEdge -= GetHorizontalScrollOffset(parent);
				}
				if (rightEdge < (int)clip.Right)
				{
					var fillRect = new Rectangle(rightEdge, (int)clip.Top,
						(int)clip.Right - rightEdge, (int)clip.Height);
					using var brush = new SolidBrush(_bg);
					g.FillRectangle(brush, fillRect);
				}
				return;
			}
			base.WndProc(ref m);
		}

		[DllImport("user32.dll")]
		private static extern IntPtr GetParent(IntPtr hWnd);

		[DllImport("user32.dll")]
		private static extern int GetScrollPos(IntPtr hWnd, int nBar);

		private const int SB_HORZ = 0;

		private static int GetHorizontalScrollOffset(ListView list)
		{
			try { return GetScrollPos(list.Handle, SB_HORZ); }
			catch { return 0; }
		}
	}
}
