using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GoPilot.SkillCatalog;

namespace GoPilot;

/// <summary>
/// Modal browser for the Skill Catalog feature: lists items discovered under
/// each configured <see cref="GoPilotSettings.SkillSources"/> URL, lets the
/// user pick a destination tier folder, and downloads selected items to disk
/// via <see cref="CatalogInstaller"/>.
///
/// All network I/O happens off the UI thread; UI updates marshal back via
/// <see cref="Control.BeginInvoke(Delegate)"/>. The dialog never collects
/// credentials -- it surfaces "rate limited", "private or not found", and
/// "sign-in required" states with explicit error messages instead.
///
/// Returns <see cref="DialogResult.OK"/> when at least one item was installed
/// (so the caller can decide whether to schedule a session handoff). Returns
/// <see cref="DialogResult.Cancel"/> on plain close.
/// </summary>
internal sealed class SkillCatalogBrowserDialog : Form
{
	private readonly GoPilotSettings _settings;
	private readonly CopilotService  _copilot;
	private readonly CatalogCache    _cache;

	private readonly ComboBox  _sourceCombo  = new();
	private readonly Button    _refreshBtn   = new();
	private readonly Label     _statusLabel  = new();
	private readonly TreeView  _tree         = new();
	private readonly TextBox   _previewBox   = new();
	private SplitContainer    _split        = new();
	private readonly ComboBox  _destCombo    = new();
	private readonly CheckBox  _allowScripts = new();
	private readonly Button    _downloadBtn  = new();
	private readonly Button    _closeBtn     = new();

	// Cached listings keyed by source URL (case-insensitive).
	private readonly Dictionary<string, CatalogTree> _treesByUrl =
		new(StringComparer.OrdinalIgnoreCase);

	private List<DestOption> _destOptions = new();

	/// <summary>
	/// Set to true after at least one item is installed during the dialog's
	/// lifetime. The caller reads <see cref="InstalledItems"/> to decide
	/// whether to schedule a session handoff.
	/// </summary>
	internal List<InstallResultSummary> InstalledItems { get; } = new();

	public SkillCatalogBrowserDialog(GoPilotSettings settings, CopilotService copilot)
	{
		_settings = settings;
		_copilot  = copilot;
		_cache    = new CatalogCache(_copilot.WorkspaceDataPath);
		BuildUi();
		PopulateSources();
		PopulateDestinations();
	}

	private void BuildUi()
	{
		Text            = "Skill Catalog";
		FormBorderStyle = FormBorderStyle.Sizable;
		StartPosition   = FormStartPosition.CenterParent;
		MinimizeBox     = false;
		MaximizeBox     = true;
		ShowInTaskbar   = false;
		ClientSize      = new Size(960, 600);
		MinimumSize     = new Size(720, 460);
		BackColor       = AppTheme.Background;
		ForeColor       = AppTheme.TextPrimary;
		Font            = new Font("Segoe UI", 9F);
		KeyPreview      = true;

		// Top row: source dropdown + refresh + status.
		var topPanel = new Panel
		{
			Dock      = DockStyle.Top,
			Height    = 64,
			Padding   = new Padding(12, 10, 12, 6),
			BackColor = AppTheme.Background,
		};

		var sourceLabel = new Label
		{
			Text     = "Source:",
			AutoSize = true,
			Location = new Point(12, 14),
		};

		_sourceCombo.DropDownStyle = ComboBoxStyle.DropDownList;
		_sourceCombo.Bounds        = new Rectangle(70, 10, 600, 24);
		_sourceCombo.BackColor     = AppTheme.InputBox;
		_sourceCombo.ForeColor     = AppTheme.TextPrimary;
		_sourceCombo.FlatStyle     = FlatStyle.Flat;
		_sourceCombo.SelectedIndexChanged += (_, _) => OnSourceChanged();

		StyleButton(_refreshBtn, "Refresh");
		_refreshBtn.Bounds = new Rectangle(680, 8, 92, 28);
		_refreshBtn.Click += (_, _) => RefreshSelectedSource(forceRevalidate: true);

		_statusLabel.AutoSize = false;
		_statusLabel.Bounds   = new Rectangle(12, 38, 760, 18);
		_statusLabel.ForeColor = AppTheme.TextMuted;
		_statusLabel.Text     = "";

		topPanel.Controls.Add(sourceLabel);
		topPanel.Controls.Add(_sourceCombo);
		topPanel.Controls.Add(_refreshBtn);
		topPanel.Controls.Add(_statusLabel);

		// Bottom row: destination + allow-scripts + download/close.
		var bottomPanel = new Panel
		{
			Dock      = DockStyle.Bottom,
			Height    = 84,
			Padding   = new Padding(12, 8, 12, 8),
			BackColor = AppTheme.Background,
		};

		var destLabel = new Label
		{
			Text     = "Install to:",
			AutoSize = true,
			Location = new Point(12, 12),
		};
		_destCombo.DropDownStyle = ComboBoxStyle.DropDownList;
		_destCombo.Bounds        = new Rectangle(80, 8, 600, 24);
		_destCombo.BackColor     = AppTheme.InputBox;
		_destCombo.ForeColor     = AppTheme.TextPrimary;
		_destCombo.FlatStyle     = FlatStyle.Flat;
		_destCombo.SelectedIndexChanged += (_, _) =>
		{
			if (_destCombo.SelectedItem is DestOption opt && opt.IsNewFolder)
			{
				PromptNewDestinationFolder();
			}
		};

		_allowScripts.Text       = "Allow scripts (.ps1 / .sh / .py / .js / .ts / binaries)";
		_allowScripts.AutoSize   = true;
		_allowScripts.Location   = new Point(12, 40);
		_allowScripts.BackColor  = AppTheme.Background;
		_allowScripts.ForeColor  = AppTheme.TextMuted;

		StyleButton(_downloadBtn, "Download Selected");
		_downloadBtn.Width   = 160;
		_downloadBtn.Anchor  = AnchorStyles.Top | AnchorStyles.Right;
		StyleButton(_closeBtn, "Close");
		_closeBtn.Width      = 90;
		_closeBtn.Anchor     = AnchorStyles.Top | AnchorStyles.Right;

		bottomPanel.Controls.Add(destLabel);
		bottomPanel.Controls.Add(_destCombo);
		bottomPanel.Controls.Add(_allowScripts);
		bottomPanel.Controls.Add(_downloadBtn);
		bottomPanel.Controls.Add(_closeBtn);
		bottomPanel.Resize += (_, _) => LayoutBottomButtons(bottomPanel);
		LayoutBottomButtons(bottomPanel);

		_downloadBtn.Click += async (_, _) => await OnDownloadAsync();
		_closeBtn.Click    += (_, _) => Close();

		// Splitter: left = TreeView with checkboxes, right = preview.
		// SplitterDistance / Panel{1,2}MinSize are NOT set here -- the
		// SplitContainer's default Width is 150 px before it is added to a
		// docked parent, and setting Panel2MinSize=240 with SplitterDistance=360
		// at that point trips the runtime "must be between Panel1MinSize and
		// Width - Panel2MinSize" assertion. We apply them in OnLoad once the
		// container has its real size.
		_split = new SplitContainer
		{
			Dock = DockStyle.Fill,
			Orientation = Orientation.Vertical,
			BackColor = AppTheme.Background,
		};

		_tree.Dock        = DockStyle.Fill;
		_tree.CheckBoxes  = true;
		_tree.BackColor   = AppTheme.InputBox;
		_tree.ForeColor   = AppTheme.TextPrimary;
		_tree.BorderStyle = BorderStyle.FixedSingle;
		_tree.HideSelection = false;
		_tree.AfterSelect += (_, e) => UpdatePreviewFor(e.Node);
		_tree.AfterCheck  += Tree_AfterCheck;

		_previewBox.Dock        = DockStyle.Fill;
		_previewBox.Multiline   = true;
		_previewBox.ReadOnly    = true;
		_previewBox.WordWrap    = false;
		_previewBox.ScrollBars  = ScrollBars.Both;
		_previewBox.BackColor   = AppTheme.InputBox;
		_previewBox.ForeColor   = AppTheme.TextPrimary;
		_previewBox.BorderStyle = BorderStyle.FixedSingle;
		_previewBox.Font        = new Font("Cascadia Mono", 9F, FontStyle.Regular);

		_split.Panel1.Controls.Add(_tree);
		_split.Panel2.Controls.Add(_previewBox);

		Controls.Add(_split);
		Controls.Add(bottomPanel);
		Controls.Add(topPanel);

		AcceptButton = _downloadBtn;
		CancelButton = _closeBtn;
	}

	/// <summary>
	/// Applies the SplitContainer's minimum panel widths and initial splitter
	/// position now that the form has its real ClientSize. Doing this in the
	/// constructor crashes because the SplitContainer's default Width is too
	/// narrow to satisfy <c>Panel1MinSize + Panel2MinSize</c>.
	/// </summary>
	protected override void OnLoad(EventArgs e)
	{
		base.OnLoad(e);

		const int minPanel = 200;
		var available = _split.Width - _split.SplitterWidth;
		if (available < minPanel * 2 + 20)
		{
			// Form is unusually small; use proportional minima instead.
			var safe = Math.Max(40, available / 4);
			_split.Panel1MinSize = safe;
			_split.Panel2MinSize = safe;
			_split.SplitterDistance = available / 2;
		}
		else
		{
			_split.Panel1MinSize = minPanel;
			_split.Panel2MinSize = minPanel;
			// Default to ~40 % for the tree, leaving the preview pane wider.
			_split.SplitterDistance = Math.Max(minPanel, _split.Width * 4 / 10);
		}
	}

	private void LayoutBottomButtons(Panel bottomPanel)
	{
		int right = bottomPanel.ClientSize.Width - bottomPanel.Padding.Right;
		_closeBtn.Location    = new Point(right - _closeBtn.Width, 8);
		_downloadBtn.Location = new Point(_closeBtn.Left - _downloadBtn.Width - 8, 8);
	}

	private static void StyleButton(Button b, string text)
	{
		b.Text     = text;
		b.Width    = 92;
		b.Height   = 28;
		b.FlatStyle = FlatStyle.Flat;
		b.FlatAppearance.BorderColor = AppTheme.ButtonBorder;
		b.BackColor = AppTheme.ButtonBg;
		b.ForeColor = AppTheme.TextPrimary;
		b.UseVisualStyleBackColor = false;
	}

	// --- sources ---------------------------------------------------------

	private void PopulateSources()
	{
		_sourceCombo.BeginUpdate();
		_sourceCombo.Items.Clear();
		foreach (var url in _settings.SkillSources)
		{
			if (CatalogSource.TryParse(url, out var src, out _))
				_sourceCombo.Items.Add(src!);
			else
				_sourceCombo.Items.Add(new CatalogSource(url, url, ProviderKind.Unknown));
		}
		_sourceCombo.EndUpdate();
		if (_sourceCombo.Items.Count > 0) _sourceCombo.SelectedIndex = 0;
		else
		{
			_statusLabel.Text = "No sources configured. Use Tools > Skill Sources... to add one.";
			_tree.Nodes.Clear();
			_previewBox.Text = "";
		}
	}

	private void OnSourceChanged()
	{
		var src = _sourceCombo.SelectedItem as CatalogSource;
		if (src == null) return;

		if (src.Kind == ProviderKind.Unknown)
		{
			_statusLabel.Text = $"Unsupported source: {src.Url}";
			_statusLabel.ForeColor = AppTheme.ColorError;
			_tree.Nodes.Clear();
			_previewBox.Text = "";
			return;
		}

		// Cache hit?
		if (_treesByUrl.TryGetValue(src.Url, out var cached))
		{
			RenderTree(cached);
			_statusLabel.ForeColor = AppTheme.TextMuted;
			_statusLabel.Text = $"Cached listing from {cached.FetchedUtc.ToLocalTime():g}. Press Refresh to revalidate.";
			return;
		}

		// On-disk cache?
		var diskEntry = _cache.TryLoad(src);
		if (diskEntry != null)
		{
			_treesByUrl[src.Url] = diskEntry.Tree;
			RenderTree(diskEntry.Tree);
			_statusLabel.ForeColor = AppTheme.TextMuted;
			_statusLabel.Text = $"Cached listing from {diskEntry.Tree.FetchedUtc.ToLocalTime():g}. Press Refresh to revalidate.";
			return;
		}

		// Cold -- fetch.
		RefreshSelectedSource(forceRevalidate: false);
	}

	private async void RefreshSelectedSource(bool forceRevalidate)
	{
		if (_sourceCombo.SelectedItem is not CatalogSource src) return;
		if (src.Kind == ProviderKind.Unknown) return;

		if (forceRevalidate) _cache.Drop(src);

		_refreshBtn.Enabled = false;
		_statusLabel.ForeColor = AppTheme.TextMuted;
		_statusLabel.Text = $"Fetching {src.Label}...";
		_tree.Nodes.Clear();
		_previewBox.Text = "";

		try
		{
			var diskEntry = forceRevalidate ? null : _cache.TryLoad(src);
			var provider  = CatalogProviderRegistry.For(src);
			CatalogTree tree;
			try
			{
				tree = await Task.Run(() => provider.ListAsync(src, diskEntry?.ETag, CancellationToken.None)).ConfigureAwait(true);
			}
			catch (CatalogTreeNotModifiedException) when (diskEntry != null)
			{
				tree = diskEntry.Tree;
			}

			_treesByUrl[src.Url] = tree;
			_cache.Save(new CacheEntry { Tree = tree, ETag = tree.ETag });
			RenderTree(tree);

			var rate = tree.RateLimit;
			var rateMsg = rate != null
				? $"  GitHub API: {rate.Remaining}/{rate.Limit}" + (rate.ResetUtc.HasValue ? $" (resets {rate.ResetUtc.Value.ToLocalTime():t})" : "")
				: "";
			_statusLabel.ForeColor = AppTheme.TextMuted;
			_statusLabel.Text = $"Listed {tree.Skills.Count} skills, {tree.Agents.Count} agents, {tree.Prompts.Count} prompts, {tree.Instructions.Count} instructions at {tree.ResolvedRef[..Math.Min(8, tree.ResolvedRef.Length)]}.{rateMsg}";
		}
		catch (CatalogProviderException ex)
		{
			_statusLabel.ForeColor = AppTheme.ColorError;
			_statusLabel.Text = ex.Kind switch
			{
				CatalogProviderErrorKind.RateLimited       => "GitHub API rate limit exhausted. Cached results still available; try again later.",
				CatalogProviderErrorKind.PrivateOrNotFound => $"Private or not found. {ex.Message} GoPilot does not collect credentials; clone the repo manually if you need its contents.",
				CatalogProviderErrorKind.AuthRequired      => ex.Message,
				CatalogProviderErrorKind.UnsupportedProvider => $"Unsupported source: {ex.Message}",
				_ => $"Error fetching source: {ex.Message}",
			};
		}
		catch (Exception ex)
		{
			_statusLabel.ForeColor = AppTheme.ColorError;
			_statusLabel.Text = $"Error: {ex.Message}";
		}
		finally
		{
			_refreshBtn.Enabled = true;
		}
	}

	// --- tree rendering --------------------------------------------------

	private void RenderTree(CatalogTree tree)
	{
		_tree.BeginUpdate();
		_tree.Nodes.Clear();

		AddCategory(tree.Skills,       "Skills");
		AddCategory(tree.Agents,       "Agents");
		AddCategory(tree.Prompts,      "Prompts");
		AddCategory(tree.Instructions, "Instructions");

		_tree.EndUpdate();

		bool any = tree.Skills.Count + tree.Agents.Count + tree.Prompts.Count + tree.Instructions.Count > 0;
		if (!any)
		{
			var empty = new TreeNode("(no recognized items in this source)") { ForeColor = AppTheme.TextMuted };
			_tree.Nodes.Add(empty);
		}
	}

	private void AddCategory(List<CatalogItem> items, string title)
	{
		if (items.Count == 0) return;
		var category = new TreeNode($"{title} ({items.Count})")
		{
			Tag = "category",
		};
		foreach (var item in items)
		{
			var label = string.IsNullOrEmpty(item.Description)
				? $"{item.Name}  [{FormatBytes(item.TotalBytes)}]"
				: $"{item.Name}  [{FormatBytes(item.TotalBytes)}]  - {item.Description}";
			var node = new TreeNode(label) { Tag = item };
			category.Nodes.Add(node);
		}
		_tree.Nodes.Add(category);
		category.Expand();
	}

	private static string FormatBytes(long bytes)
	{
		if (bytes < 1024) return $"{bytes} B";
		if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
		return $"{bytes / (1024.0 * 1024.0):0.##} MB";
	}

	private bool _suppressCheckRecursion;

	private void Tree_AfterCheck(object? sender, TreeViewEventArgs e)
	{
		if (_suppressCheckRecursion) return;
		_suppressCheckRecursion = true;
		try
		{
			// Category-level check toggles all children.
			if (e.Node?.Tag is "category")
			{
				foreach (TreeNode child in e.Node.Nodes)
					child.Checked = e.Node.Checked;
			}
		}
		finally
		{
			_suppressCheckRecursion = false;
		}
	}

	private async void UpdatePreviewFor(TreeNode? node)
	{
		if (node?.Tag is not CatalogItem item)
		{
			_previewBox.Text = "";
			return;
		}

		var src = _sourceCombo.SelectedItem as CatalogSource;
		if (src == null) return;
		if (!_treesByUrl.TryGetValue(src.Url, out var tree)) return;

		_previewBox.Text = $"Loading preview for {item.Name}...\r\n";

		try
		{
			var provider = CatalogProviderRegistry.For(src);
			var bytes    = await Task.Run(() => provider.FetchFileAsync(src, tree.ResolvedRef, item.PrimaryRepoPath, CancellationToken.None)).ConfigureAwait(true);
			var text     = System.Text.Encoding.UTF8.GetString(bytes);
			var header   =
				$"--- {item.PrimaryRepoPath}  ({FormatBytes(item.TotalBytes)}, {item.Files.Count} file(s)) ---\r\n" +
				$"--- source: {src.Url}  @ {tree.ResolvedRef} ---\r\n\r\n";
			_previewBox.Text = header + text;
			_previewBox.SelectionStart = 0;
			_previewBox.ScrollToCaret();
		}
		catch (Exception ex)
		{
			_previewBox.Text = $"(failed to load preview: {ex.Message})";
		}
	}

	// --- destinations ----------------------------------------------------

	private void PopulateDestinations()
	{
		_destOptions = BuildDestOptions();
		_destCombo.BeginUpdate();
		_destCombo.Items.Clear();
		foreach (var d in _destOptions) _destCombo.Items.Add(d);
		_destCombo.EndUpdate();
		if (_destCombo.Items.Count > 0)
		{
			// Default to the first existing tier (PERSONAL if it exists, otherwise the first in the list).
			var idx = _destOptions.FindIndex(d => d.Exists);
			_destCombo.SelectedIndex = idx >= 0 ? idx : 0;
		}
	}

	/// <summary>
	/// Builds the destination-tier dropdown contents. Mirrors the order of
	/// <see cref="CopilotService.GetTierFolders"/> but always includes every
	/// tier (existing or not) so the user can install into PERSONAL even if
	/// it doesn't exist yet -- the installer creates it lazily.
	/// </summary>
	private List<DestOption> BuildDestOptions()
	{
		var options = new List<DestOption>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

		void AddTier(string label, string path)
		{
			if (string.IsNullOrEmpty(path)) return;
			if (!seen.Add(path)) return;
			options.Add(new DestOption(label, path, Directory.Exists(path), false));
		}

		AddTier("PERSONAL",        Path.Combine(userProfile, ".copilot"));
		AddTier("PERSONAL_GITHUB", Path.Combine(userProfile, ".github"));

		int idx = 1;
		foreach (var folder in _settings.SkillTreeFolders)
		{
			AddTier($"SKILL[{idx}]", folder);
			idx++;
		}

		if (!string.IsNullOrEmpty(_copilot.WorkingDirectory))
		{
			AddTier("PROJECT",        _copilot.WorkingDirectory);
			AddTier("PROJECT_GITHUB", Path.Combine(_copilot.WorkingDirectory, ".github"));
		}

		options.Add(new DestOption("<New folder...>", "", false, true));
		return options;
	}

	private void PromptNewDestinationFolder()
	{
		using var fb = new FolderBrowserDialog
		{
			Description            = "Pick a new Skill Tree folder. It will be added to gopilot.ini and a session handoff scheduled after install.",
			UseDescriptionForTitle = true,
			ShowNewFolderButton    = true,
		};
		if (fb.ShowDialog(this) != DialogResult.OK)
		{
			// Revert to the first existing entry.
			var idx = _destOptions.FindIndex(d => d.Exists);
			_destCombo.SelectedIndex = idx >= 0 ? idx : 0;
			return;
		}

		var picked = fb.SelectedPath.Trim();
		if (_settings.SkillTreeFolders.Any(f => string.Equals(f, picked, StringComparison.OrdinalIgnoreCase)))
		{
			MessageBox.Show(this, "That folder is already in the Skill Tree.", "Skill Catalog",
				MessageBoxButtons.OK, MessageBoxIcon.Information);
			var idx = _destOptions.FindIndex(d => string.Equals(d.Path, picked, StringComparison.OrdinalIgnoreCase));
			if (idx >= 0) _destCombo.SelectedIndex = idx;
			return;
		}

		_settings.SkillTreeFolders.Add(picked);
		try { _settings.Save(); } catch { /* best-effort */ }
		_copilot.SkillTreeFolders = _settings.SkillTreeFolders;

		PopulateDestinations();
		var newIdx = _destOptions.FindIndex(d => string.Equals(d.Path, picked, StringComparison.OrdinalIgnoreCase));
		if (newIdx >= 0) _destCombo.SelectedIndex = newIdx;
	}

	// --- download --------------------------------------------------------

	private async Task OnDownloadAsync()
	{
		if (_sourceCombo.SelectedItem is not CatalogSource src)
		{
			MessageBox.Show(this, "Select a source.", "Skill Catalog", MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}
		if (!_treesByUrl.TryGetValue(src.Url, out var tree))
		{
			MessageBox.Show(this, "Refresh the source first.", "Skill Catalog", MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}
		if (_destCombo.SelectedItem is not DestOption dest || dest.IsNewFolder || string.IsNullOrEmpty(dest.Path))
		{
			MessageBox.Show(this, "Pick a destination.", "Skill Catalog", MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		var selected = CollectCheckedItems();
		if (selected.Count == 0)
		{
			MessageBox.Show(this, "No items checked.", "Skill Catalog", MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}

		// Warn about name collisions against the live cached agent / skill names.
		var collisions = FindCollisions(selected);
		if (collisions.Count > 0)
		{
			var msg = "The following items have the same on-disk name as something already loaded by an earlier tier (later tier wins):\r\n\r\n  " +
				string.Join("\r\n  ", collisions) +
				"\r\n\r\nProceed?";
			if (MessageBox.Show(this, msg, "Skill Catalog", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
				return;
		}

		var allowScripts = _allowScripts.Checked;

		_downloadBtn.Enabled = false;
		_refreshBtn.Enabled  = false;
		var savedStatus = _statusLabel.Text;
		var savedColor  = _statusLabel.ForeColor;

		try
		{
			var provider  = CatalogProviderRegistry.For(src);
			var installer = new CatalogInstaller(provider);
			int ok = 0, failed = 0;
			var failures = new List<string>();

			using var cts = new CancellationTokenSource();
			using var progress = new ProgressForm(selected.Count, cts);
			progress.Show(this);

			foreach (var item in selected)
			{
				if (cts.IsCancellationRequested) break;
				progress.ReportItem($"Installing {item.Name}...");
				try
				{
					var result = await Task.Run(() =>
						installer.InstallAsync(tree, item, dest.Path, allowScripts, cts.Token)).ConfigureAwait(true);
					InstalledItems.Add(new InstallResultSummary(item.Kind, item.Name, result.PrimaryDestination, dest.Label, dest.Path));
					ok++;
					progress.AdvanceOne();
				}
				catch (OperationCanceledException)
				{
					break;
				}
				catch (Exception ex)
				{
					failed++;
					failures.Add($"{item.Name}: {ex.Message}");
					progress.AdvanceOne();
				}
			}

			progress.Close();

			_statusLabel.Text = $"Installed {ok} of {selected.Count} item(s) into {dest.Label}." + (failed > 0 ? $" {failed} failed." : "");
			_statusLabel.ForeColor = failed == 0 ? AppTheme.TextMuted : AppTheme.ColorError;

			if (failures.Count > 0)
			{
				MessageBox.Show(this,
					"Some items failed:\r\n\r\n" + string.Join("\r\n", failures),
					"Skill Catalog", MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}

			if (ok > 0)
			{
				DialogResult = DialogResult.OK;
			}
		}
		catch (Exception ex)
		{
			_statusLabel.Text = $"Error: {ex.Message}";
			_statusLabel.ForeColor = AppTheme.ColorError;
			MessageBox.Show(this, ex.Message, "Skill Catalog", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
		finally
		{
			_downloadBtn.Enabled = true;
			_refreshBtn.Enabled  = true;
		}
	}

	private List<CatalogItem> CollectCheckedItems()
	{
		var items = new List<CatalogItem>();
		foreach (TreeNode category in _tree.Nodes)
		{
			foreach (TreeNode child in category.Nodes)
			{
				if (child.Checked && child.Tag is CatalogItem item)
					items.Add(item);
			}
		}
		return items;
	}

	private List<string> FindCollisions(List<CatalogItem> items)
	{
		var collisions = new List<string>();
		var existingAgents = new HashSet<string>(_copilot.CachedAgents.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
		var existingSkills = new HashSet<string>(_copilot.CachedSkills.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

		foreach (var item in items)
		{
			switch (item.Kind)
			{
				case CatalogItemKind.Agent when existingAgents.Contains(item.Name):
					collisions.Add($"agent '{item.Name}' already exists");
					break;
				case CatalogItemKind.Skill when existingSkills.Contains(item.Name):
					collisions.Add($"skill '{item.Name}' already exists");
					break;
			}
		}
		return collisions;
	}

	// --- helper types ----------------------------------------------------

	private sealed record DestOption(string Label, string Path, bool Exists, bool IsNewFolder)
	{
		public override string ToString()
		{
			if (IsNewFolder) return Label;
			var suffix = Exists ? "" : "  (create)";
			return $"{Label}  -  {Path}{suffix}";
		}
	}

	/// <summary>
	/// Tiny modal that shows the per-item progress of a download batch.
	/// Cancel triggers the shared <see cref="CancellationTokenSource"/> the
	/// caller passes in.
	/// </summary>
	private sealed class ProgressForm : Form
	{
		private readonly ProgressBar _bar = new();
		private readonly Label       _label = new();
		private readonly Button      _cancel = new();
		private readonly CancellationTokenSource _cts;
		private int _completed;

		public ProgressForm(int totalSteps, CancellationTokenSource cts)
		{
			_cts = cts;
			Text = "Downloading...";
			FormBorderStyle = FormBorderStyle.FixedDialog;
			ControlBox = false;
			ShowInTaskbar = false;
			StartPosition = FormStartPosition.CenterParent;
			ClientSize = new Size(420, 110);
			BackColor = AppTheme.Background;
			ForeColor = AppTheme.TextPrimary;
			Font = new Font("Segoe UI", 9F);

			_label.Bounds = new Rectangle(12, 12, 396, 18);
			_label.Text = "Preparing...";
			_label.ForeColor = AppTheme.TextPrimary;

			_bar.Bounds  = new Rectangle(12, 36, 396, 18);
			_bar.Minimum = 0;
			_bar.Maximum = Math.Max(1, totalSteps);
			_bar.Value   = 0;

			_cancel.Bounds   = new Rectangle(318, 64, 90, 28);
			_cancel.Text     = "Cancel";
			_cancel.FlatStyle = FlatStyle.Flat;
			_cancel.FlatAppearance.BorderColor = AppTheme.ButtonBorder;
			_cancel.BackColor = AppTheme.ButtonBg;
			_cancel.ForeColor = AppTheme.TextPrimary;
			_cancel.UseVisualStyleBackColor = false;
			_cancel.Click += (_, _) => { _cts.Cancel(); _cancel.Enabled = false; };

			Controls.Add(_label);
			Controls.Add(_bar);
			Controls.Add(_cancel);
		}

		public void ReportItem(string message)
		{
			if (IsHandleCreated) BeginInvoke(new Action(() => _label.Text = message));
			else _label.Text = message;
		}

		public void AdvanceOne()
		{
			if (IsHandleCreated)
				BeginInvoke(new Action(() =>
				{
					_completed++;
					_bar.Value = Math.Min(_bar.Maximum, _completed);
				}));
			else
			{
				_completed++;
				_bar.Value = Math.Min(_bar.Maximum, _completed);
			}
		}
	}
}

/// <summary>
/// One successful install reported back to <see cref="MainForm"/> so it can
/// decide whether to call <c>ScheduleHandoff</c> (only meaningful when a
/// brand-new tier folder was added; same-tier additions are picked up on the
/// next session creation either way).
/// </summary>
internal sealed record InstallResultSummary(
	CatalogItemKind Kind, string Name, string DestinationPath, string TierLabel, string TierPath);
