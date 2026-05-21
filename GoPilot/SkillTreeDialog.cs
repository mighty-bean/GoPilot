using System.Collections.Generic;
using System.Linq;

namespace GoPilot;

/// <summary>
/// Modal dialog for editing the Skill Tree folder list persisted in gopilot.ini.
///
/// Each Skill Tree folder is searched at session start for a <c>skills/</c> subdirectory
/// (added to the session's skillDirectories) and an <c>agents/</c> subdirectory (each
/// <c>*.md</c> file becomes a customAgent).  Order matters: later entries override earlier
/// ones for agent-name collisions.
///
/// The caller seeds the dialog with the current list via the constructor, then reads the
/// edited list back from <see cref="Folders"/> on <see cref="DialogResult.OK"/>.
/// <see cref="DialogResult.OK"/> is only returned when the list actually changed, so the
/// caller can use it as the trigger for a session handoff without false positives.
/// </summary>
public sealed class SkillTreeDialog : Form
{
    private readonly ListBox _list = new();
    private readonly Button _buttonAdd = new();
    private readonly Button _buttonRemove = new();
    private readonly Button _buttonUp = new();
    private readonly Button _buttonDown = new();
    private readonly Button _buttonOk = new();
    private readonly Button _buttonCancel = new();
    private readonly List<string> _original;

    /// <summary>The edited list of folders.  Only valid after the dialog returns OK.</summary>
    public List<string> Folders { get; private set; }

    public SkillTreeDialog(IEnumerable<string> initialFolders)
    {
        _original = initialFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList();
        Folders   = new List<string>(_original);

        BuildUi();
        ReloadList();
        UpdateButtonStates();
    }

    private void BuildUi()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode       = AutoScaleMode.Font;

        Text            = "Skill Tree";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition   = FormStartPosition.CenterParent;
        MinimizeBox     = false;
        MaximizeBox     = true;
        ShowInTaskbar   = false;
        ClientSize      = new Size(620, 360);
        MinimumSize     = new Size(480, 280);
        BackColor       = AppTheme.Background;
        ForeColor       = AppTheme.TextPrimary;
        Font            = new Font("Segoe UI", 9F);
        KeyPreview      = true;

        var info = new Label
        {
            AutoSize  = false,
            Dock      = DockStyle.Top,
            Height    = 44,
            Padding   = new Padding(12, 10, 12, 6),
            ForeColor = AppTheme.TextMuted,
            Text      =
                "Folders contributing skills/ and agents/ to each Copilot session. " +
                "Later entries override earlier ones for agent-name collisions. " +
                "The current project folder is always applied last.",
        };

        // Side button column.
        var sidePanel = new Panel
        {
            Dock      = DockStyle.Right,
            Width     = 110,
            Padding   = new Padding(6, 0, 12, 0),
            BackColor = AppTheme.Background,
        };
        StyleButton(_buttonAdd,    "Add...",  0);
        StyleButton(_buttonRemove, "Remove",  34);
        StyleButton(_buttonUp,     "Move Up", 76);
        StyleButton(_buttonDown,   "Move Down", 110);
        sidePanel.Controls.Add(_buttonAdd);
        sidePanel.Controls.Add(_buttonRemove);
        sidePanel.Controls.Add(_buttonUp);
        sidePanel.Controls.Add(_buttonDown);

        // List of folders (centre).
        _list.Dock          = DockStyle.Fill;
        _list.BackColor     = AppTheme.InputBox;
        _list.ForeColor     = AppTheme.TextPrimary;
        _list.BorderStyle   = BorderStyle.FixedSingle;
        _list.IntegralHeight = false;
        _list.HorizontalScrollbar = true;
        _list.SelectionMode = SelectionMode.One;

        var centerPanel = new Panel
        {
            Dock      = DockStyle.Fill,
            Padding   = new Padding(12, 0, 0, 0),
            BackColor = AppTheme.Background,
        };
        centerPanel.Controls.Add(_list);

        // Bottom button row.
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
        _list.DoubleClick           += (_, _) => { if (_list.SelectedIndex >= 0) BrowseAndReplace(_list.SelectedIndex); };

        _buttonAdd.Click    += (_, _) => OnAdd();
        _buttonRemove.Click += (_, _) => OnRemove();
        _buttonUp.Click     += (_, _) => OnMove(-1);
        _buttonDown.Click   += (_, _) => OnMove(+1);
        _buttonOk.Click     += (_, _) => OnOk();
        _buttonCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        ResumeLayout(false);
        PerformLayout();
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
        foreach (var f in Folders) _list.Items.Add(f);
        _list.EndUpdate();
        if (_list.Items.Count > 0)
            _list.SelectedIndex = Math.Min(Math.Max(0, prevIndex), _list.Items.Count - 1);
        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        int i = _list.SelectedIndex;
        _buttonRemove.Enabled = i >= 0;
        _buttonUp.Enabled     = i > 0;
        _buttonDown.Enabled   = i >= 0 && i < Folders.Count - 1;
    }

    private void OnAdd()
    {
        using var fb = new FolderBrowserDialog
        {
            Description            = "Select a Skill Tree folder (will be searched for skills/ and agents/ subfolders)",
            UseDescriptionForTitle = true,
            ShowNewFolderButton    = true,
        };
        if (fb.ShowDialog(this) != DialogResult.OK) return;
        AddFolder(fb.SelectedPath);
    }

    private void BrowseAndReplace(int index)
    {
        if (index < 0 || index >= Folders.Count) return;
        using var fb = new FolderBrowserDialog
        {
            Description            = "Replace this Skill Tree folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton    = true,
            SelectedPath           = Folders[index],
        };
        if (fb.ShowDialog(this) != DialogResult.OK) return;

        var picked = fb.SelectedPath.Trim();
        // Prevent duplicates (case-insensitive).
        if (IsDuplicate(picked, exceptIndex: index))
        {
            MessageBox.Show(this, "That folder is already in the Skill Tree.", "Skill Tree",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Folders[index] = picked;
        ReloadList();
        _list.SelectedIndex = index;
    }

    private bool IsDuplicate(string candidate, int exceptIndex = -1)
    {
        for (int i = 0; i < Folders.Count; i++)
        {
            if (i == exceptIndex) continue;
            if (string.Equals(Folders[i], candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void AddFolder(string path)
    {
        var trimmed = path.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;
        if (IsDuplicate(trimmed))
        {
            MessageBox.Show(this, "That folder is already in the Skill Tree.", "Skill Tree",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Folders.Add(trimmed);
        ReloadList();
        _list.SelectedIndex = Folders.Count - 1;
    }

    private void OnRemove()
    {
        int i = _list.SelectedIndex;
        if (i < 0) return;
        Folders.RemoveAt(i);
        ReloadList();
        if (Folders.Count > 0)
            _list.SelectedIndex = Math.Min(i, Folders.Count - 1);
    }

    private void OnMove(int delta)
    {
        int i = _list.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= Folders.Count) return;
        (Folders[i], Folders[j]) = (Folders[j], Folders[i]);
        ReloadList();
        _list.SelectedIndex = j;
    }

    private void OnOk()
    {
        // Only return OK if the list actually changed; otherwise treat as Cancel
        // so the caller does not trigger a session handoff for a no-op edit.
        DialogResult = ListChanged() ? DialogResult.OK : DialogResult.Cancel;
        Close();
    }

    private bool ListChanged()
    {
        if (Folders.Count != _original.Count) return true;
        for (int i = 0; i < Folders.Count; i++)
        {
            if (!string.Equals(Folders[i], _original[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
