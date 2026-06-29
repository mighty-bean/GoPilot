namespace GoPilot;

partial class MainForm
{
    /// <summary>
    /// Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    /// Clean up any resources being used.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
            components.Dispose();
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    /// Required method for Designer support - do not modify
    /// the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
        splitContainerMain = new SplitContainer();
        richTextBoxPrompt = new PlainRichTextBox();
        panelAttachments = new Panel();
        flowLayoutPanelChips = new FlowLayoutPanel();
        labelAttach = new Label();
        panelHistoryNav = new Panel();
        buttonHistoryNext = new Button();
        buttonHistoryPrev = new Button();
        panelActions = new Panel();
        buttonOptions = new Button();
        labelModel = new Label();
        comboBoxModel = new ComboBox();
        labelMode = new Label();
        comboBoxMode = new ComboBox();
        labelEffort = new Label();
        comboBoxEffort = new ComboBox();
        buttonStop = new Button();
        buttonSend = new Button();
        tabControlOutput = new DarkTabControl();
        tabPageRendered = new TabPage();
        webViewOutput = new Microsoft.Web.WebView2.WinForms.WebView2();
        tabPageRaw = new TabPage();
        richTextBoxOutput = new RichTextBox();
        contextMenuOptions = new ContextMenuStrip(components);
        menuOptionAutoApprove = new ToolStripMenuItem();
        menuOptionFleet = new ToolStripMenuItem();
        menuSessionCaveman = new ToolStripMenuItem();
        menuSessionShowSteps = new ToolStripMenuItem();
        menuOptionLocalFilter = new ToolStripMenuItem();
        menuStripMain = new MenuStrip();
        menuSession = new ToolStripMenuItem();
        menuSessionNew = new ToolStripMenuItem();
        menuSessionPast = new ToolStripMenuItem();
        menuSessionSeparatorNew = new ToolStripSeparator();
        menuSessionSummarize = new ToolStripMenuItem();
        menuSessionClear = new ToolStripMenuItem();
        menuSessionRefresh = new ToolStripMenuItem();
        menuSessionRefreshCompact = new ToolStripMenuItem();
        menuSessionRefreshRestart = new ToolStripMenuItem();
        menuSessionRefreshFresh = new ToolStripMenuItem();
        menuReferences = new ToolStripMenuItem();
        menuReferencesAddFile = new ToolStripMenuItem();
        menuReferencesAddFolder = new ToolStripMenuItem();
        menuReferencesSeparator1 = new ToolStripSeparator();
        menuReferencesListAgents = new ToolStripMenuItem();
        menuReferencesListSkills = new ToolStripMenuItem();
        menuReferencesListPrompts = new ToolStripMenuItem();
        menuTools = new ToolStripMenuItem();
        menuToolsPowershell = new ToolStripMenuItem();
        menuToolsExplorer = new ToolStripMenuItem();
        menuToolsVSCode = new ToolStripMenuItem();
        menuToolsSeparator1 = new ToolStripSeparator();
        menuToolsSkillTree = new ToolStripMenuItem();
        menuToolsSkillSources = new ToolStripMenuItem();
        menuToolsBrowseCatalog = new ToolStripMenuItem();
        menuToolsSeparator2 = new ToolStripSeparator();
        menuToolsPermissions = new ToolStripMenuItem();
        menuHelp = new ToolStripMenuItem();
        menuHelpShow = new ToolStripMenuItem();
        menuHelpAbout = new ToolStripMenuItem();
        statusStrip = new StatusStrip();
        toolStripStatusLabelConnection = new ToolStripStatusLabel();
        toolStripStatusLabelVersion = new ToolStripStatusLabel();
        toolStripStatusLabelSep = new ToolStripSeparator();
        toolStripStatusLabelAgentStatus = new ToolStripStatusLabel();
        toolStripProgressBarContext = new ToolStripProgressBar();
        toolStripStatusLabelContext = new ToolStripStatusLabel();
        toolStripStatusLabelAic = new ToolStripStatusLabel();
        toolStripStatusLabelSession = new ToolStripStatusLabel();
        toolTipMain = new ToolTip(components);
        ((System.ComponentModel.ISupportInitialize)splitContainerMain).BeginInit();
        splitContainerMain.Panel1.SuspendLayout();
        splitContainerMain.Panel2.SuspendLayout();
        splitContainerMain.SuspendLayout();
        panelAttachments.SuspendLayout();
        panelHistoryNav.SuspendLayout();
        panelActions.SuspendLayout();
        tabControlOutput.SuspendLayout();
        tabPageRendered.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)webViewOutput).BeginInit();
        tabPageRaw.SuspendLayout();
        contextMenuOptions.SuspendLayout();
        menuStripMain.SuspendLayout();
        statusStrip.SuspendLayout();
        SuspendLayout();
        // 
        // splitContainerMain
        // 
        splitContainerMain.BackColor = Color.FromArgb(64, 64, 64);
        splitContainerMain.Dock = DockStyle.Fill;
        splitContainerMain.Location = new Point(0, 24);
        splitContainerMain.Name = "splitContainerMain";
        splitContainerMain.Orientation = Orientation.Horizontal;
        // 
        // splitContainerMain.Panel1
        // 
        splitContainerMain.Panel1.Controls.Add(richTextBoxPrompt);
        splitContainerMain.Panel1.Controls.Add(panelAttachments);
        splitContainerMain.Panel1.Controls.Add(panelHistoryNav);
        splitContainerMain.Panel1.Controls.Add(panelActions);
        splitContainerMain.Panel1MinSize = 100;
        // 
        // splitContainerMain.Panel2
        // 
        splitContainerMain.Panel2.Controls.Add(tabControlOutput);
        splitContainerMain.Panel2MinSize = 180;
        splitContainerMain.Size = new Size(940, 570);
        splitContainerMain.SplitterDistance = 250;
        splitContainerMain.TabIndex = 0;
        // 
        // richTextBoxPrompt
        // 
        richTextBoxPrompt.AcceptsTab = true;
        richTextBoxPrompt.AllowDrop = true;
        richTextBoxPrompt.BackColor = Color.FromArgb(52, 52, 52);
        richTextBoxPrompt.BorderStyle = BorderStyle.None;
        richTextBoxPrompt.Dock = DockStyle.Fill;
        richTextBoxPrompt.Font = new Font("Segoe UI", 11F);
        richTextBoxPrompt.ForeColor = Color.FromArgb(218, 218, 218);
        richTextBoxPrompt.Location = new Point(20, 38);
        richTextBoxPrompt.Name = "richTextBoxPrompt";
        richTextBoxPrompt.ScrollBars = RichTextBoxScrollBars.Vertical;
        richTextBoxPrompt.Size = new Size(920, 177);
        richTextBoxPrompt.TabIndex = 1;
        richTextBoxPrompt.Text = "";
        toolTipMain.SetToolTip(richTextBoxPrompt, "Ctrl+Enter to send");
        // 
        // panelAttachments
        // 
        panelAttachments.BackColor = Color.FromArgb(64, 64, 64);
        panelAttachments.Controls.Add(flowLayoutPanelChips);
        panelAttachments.Controls.Add(labelAttach);
        panelAttachments.Dock = DockStyle.Bottom;
        panelAttachments.Location = new Point(20, 215);
        panelAttachments.Name = "panelAttachments";
        panelAttachments.Size = new Size(920, 35);
        panelAttachments.TabIndex = 0;
        panelAttachments.Visible = false;
        // 
        // flowLayoutPanelChips
        // 
        flowLayoutPanelChips.AutoSize = true;
        flowLayoutPanelChips.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        flowLayoutPanelChips.BackColor = Color.FromArgb(64, 64, 64);
        flowLayoutPanelChips.Location = new Point(90, 3);
        flowLayoutPanelChips.Name = "flowLayoutPanelChips";
        flowLayoutPanelChips.Size = new Size(0, 0);
        flowLayoutPanelChips.TabIndex = 1;
        flowLayoutPanelChips.WrapContents = false;
        // 
        // labelAttach
        // 
        labelAttach.AutoSize = true;
        labelAttach.Font = new Font("Segoe UI", 9F);
        labelAttach.ForeColor = Color.FromArgb(148, 148, 148);
        labelAttach.Location = new Point(6, 6);
        labelAttach.Name = "labelAttach";
        labelAttach.Size = new Size(78, 15);
        labelAttach.TabIndex = 0;
        labelAttach.Text = "Attachments:";
        // 
        // panelHistoryNav
        // 
        panelHistoryNav.BackColor = Color.FromArgb(52, 52, 52);
        panelHistoryNav.BorderStyle = BorderStyle.FixedSingle;
        panelHistoryNav.Controls.Add(buttonHistoryNext);
        panelHistoryNav.Controls.Add(buttonHistoryPrev);
        panelHistoryNav.Dock = DockStyle.Left;
        panelHistoryNav.Location = new Point(0, 38);
        panelHistoryNav.Name = "panelHistoryNav";
        panelHistoryNav.Size = new Size(20, 212);
        panelHistoryNav.TabIndex = 3;
        // 
        // buttonHistoryNext
        // 
        buttonHistoryNext.BackColor = Color.FromArgb(52, 52, 52);
        buttonHistoryNext.Dock = DockStyle.Bottom;
        buttonHistoryNext.Enabled = false;
        buttonHistoryNext.FlatAppearance.BorderSize = 0;
        buttonHistoryNext.FlatAppearance.MouseOverBackColor = Color.FromArgb(72, 72, 72);
        buttonHistoryNext.FlatStyle = FlatStyle.Flat;
        buttonHistoryNext.Font = new Font("Segoe UI", 7F);
        buttonHistoryNext.ForeColor = Color.FromArgb(148, 148, 148);
        buttonHistoryNext.Location = new Point(0, 190);
        buttonHistoryNext.Name = "buttonHistoryNext";
        buttonHistoryNext.Size = new Size(18, 20);
        buttonHistoryNext.TabIndex = 0;
        buttonHistoryNext.TabStop = false;
        buttonHistoryNext.Text = "▼";
        toolTipMain.SetToolTip(buttonHistoryNext, "Next prompt (newer)");
        buttonHistoryNext.UseVisualStyleBackColor = false;
        // 
        // buttonHistoryPrev
        // 
        buttonHistoryPrev.BackColor = Color.FromArgb(52, 52, 52);
        buttonHistoryPrev.Dock = DockStyle.Top;
        buttonHistoryPrev.Enabled = false;
        buttonHistoryPrev.FlatAppearance.BorderSize = 0;
        buttonHistoryPrev.FlatAppearance.MouseOverBackColor = Color.FromArgb(72, 72, 72);
        buttonHistoryPrev.FlatStyle = FlatStyle.Flat;
        buttonHistoryPrev.Font = new Font("Segoe UI", 7F);
        buttonHistoryPrev.ForeColor = Color.FromArgb(148, 148, 148);
        buttonHistoryPrev.Location = new Point(0, 0);
        buttonHistoryPrev.Name = "buttonHistoryPrev";
        buttonHistoryPrev.Size = new Size(18, 20);
        buttonHistoryPrev.TabIndex = 1;
        buttonHistoryPrev.TabStop = false;
        buttonHistoryPrev.Text = "▲";
        toolTipMain.SetToolTip(buttonHistoryPrev, "Previous prompt (older)");
        buttonHistoryPrev.UseVisualStyleBackColor = false;
        // 
        // panelActions
        // 
        panelActions.BackColor = Color.FromArgb(64, 64, 64);
        panelActions.Controls.Add(buttonOptions);
        panelActions.Controls.Add(labelModel);
        panelActions.Controls.Add(comboBoxModel);
        panelActions.Controls.Add(labelMode);
        panelActions.Controls.Add(comboBoxMode);
        panelActions.Controls.Add(labelEffort);
        panelActions.Controls.Add(comboBoxEffort);
        panelActions.Controls.Add(buttonStop);
        panelActions.Controls.Add(buttonSend);
        panelActions.Dock = DockStyle.Top;
        panelActions.Location = new Point(0, 0);
        panelActions.Name = "panelActions";
        panelActions.Padding = new Padding(4, 4, 8, 4);
        panelActions.Size = new Size(940, 38);
        panelActions.TabIndex = 2;
        // 
        // buttonOptions
        // 
        buttonOptions.BackColor = Color.FromArgb(86, 86, 86);
        buttonOptions.FlatAppearance.BorderColor = Color.FromArgb(108, 108, 108);
        buttonOptions.FlatStyle = FlatStyle.Flat;
        buttonOptions.Font = new Font("Segoe UI", 9F);
        buttonOptions.ForeColor = Color.FromArgb(218, 218, 218);
        buttonOptions.Location = new Point(554, 4);
        buttonOptions.Name = "buttonOptions";
        buttonOptions.Size = new Size(210, 28);
        buttonOptions.TabIndex = 0;
        buttonOptions.Text = "Options: ▾";
        toolTipMain.SetToolTip(buttonOptions, "Toggle session options (Auto-approve tools, Fleet mode, Caveman Mode, Show Working Steps)");
        buttonOptions.UseVisualStyleBackColor = false;
        // 
        // labelModel
        // 
        labelModel.AutoSize = true;
        labelModel.Font = new Font("Segoe UI", 9F);
        labelModel.ForeColor = Color.FromArgb(218, 218, 218);
        labelModel.Location = new Point(7, 11);
        labelModel.Name = "labelModel";
        labelModel.Size = new Size(44, 15);
        labelModel.TabIndex = 1;
        labelModel.Text = "Model:";
        // 
        // comboBoxModel
        // 
        comboBoxModel.BackColor = Color.FromArgb(52, 52, 52);
        comboBoxModel.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBoxModel.FlatStyle = FlatStyle.Flat;
        comboBoxModel.Font = new Font("Segoe UI", 9F);
        comboBoxModel.ForeColor = Color.FromArgb(218, 218, 218);
        comboBoxModel.FormattingEnabled = true;
        comboBoxModel.Location = new Point(55, 7);
        comboBoxModel.Name = "comboBoxModel";
        comboBoxModel.Size = new Size(184, 23);
        comboBoxModel.TabIndex = 2;
        // 
        // labelMode
        // 
        labelMode.AutoSize = true;
        labelMode.Font = new Font("Segoe UI", 9F);
        labelMode.ForeColor = Color.FromArgb(218, 218, 218);
        labelMode.Location = new Point(251, 11);
        labelMode.Name = "labelMode";
        labelMode.Size = new Size(41, 15);
        labelMode.TabIndex = 7;
        labelMode.Text = "Mode:";
        // 
        // comboBoxMode
        // 
        comboBoxMode.BackColor = Color.FromArgb(52, 52, 52);
        comboBoxMode.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBoxMode.FlatStyle = FlatStyle.Flat;
        comboBoxMode.Font = new Font("Segoe UI", 9F);
        comboBoxMode.ForeColor = Color.FromArgb(218, 218, 218);
        comboBoxMode.FormattingEnabled = true;
        comboBoxMode.Location = new Point(299, 7);
        comboBoxMode.Name = "comboBoxMode";
        comboBoxMode.Size = new Size(92, 23);
        comboBoxMode.TabIndex = 8;
        toolTipMain.SetToolTip(comboBoxMode, "Standard: normal chat  |  Plan: plan before acting  |  Autopilot: fully autonomous");
        // 
        // labelEffort
        // 
        labelEffort.AutoSize = true;
        labelEffort.Font = new Font("Segoe UI", 9F);
        labelEffort.ForeColor = Color.FromArgb(218, 218, 218);
        labelEffort.Location = new Point(401, 11);
        labelEffort.Name = "labelEffort";
        labelEffort.Size = new Size(39, 15);
        labelEffort.TabIndex = 9;
        labelEffort.Text = "Effort:";
        // 
        // comboBoxEffort
        // 
        comboBoxEffort.BackColor = Color.FromArgb(52, 52, 52);
        comboBoxEffort.DropDownStyle = ComboBoxStyle.DropDownList;
        comboBoxEffort.Enabled = false;
        comboBoxEffort.FlatStyle = FlatStyle.Flat;
        comboBoxEffort.Font = new Font("Segoe UI", 9F);
        comboBoxEffort.ForeColor = Color.FromArgb(218, 218, 218);
        comboBoxEffort.FormattingEnabled = true;
        comboBoxEffort.Location = new Point(443, 7);
        comboBoxEffort.Name = "comboBoxEffort";
        comboBoxEffort.Size = new Size(81, 23);
        comboBoxEffort.TabIndex = 10;
        toolTipMain.SetToolTip(comboBoxEffort, "Reasoning effort for the selected model. Disabled when the model does not advertise reasoning-effort support.");
        // 
        // buttonStop
        // 
        buttonStop.Anchor = AnchorStyles.Right;
        buttonStop.BackColor = Color.FromArgb(86, 86, 86);
        buttonStop.Enabled = false;
        buttonStop.FlatAppearance.BorderColor = Color.FromArgb(108, 108, 108);
        buttonStop.FlatStyle = FlatStyle.Flat;
        buttonStop.Font = new Font("Segoe UI", 9F);
        buttonStop.ForeColor = Color.FromArgb(218, 218, 218);
        buttonStop.Location = new Point(793, 4);
        buttonStop.Name = "buttonStop";
        buttonStop.Size = new Size(73, 28);
        buttonStop.TabIndex = 4;
        buttonStop.Text = "⬛ Stop";
        toolTipMain.SetToolTip(buttonStop, "Stop the current Copilot response");
        buttonStop.UseVisualStyleBackColor = false;
        // 
        // buttonSend
        // 
        buttonSend.Anchor = AnchorStyles.Right;
        buttonSend.BackColor = Color.FromArgb(60, 112, 160);
        buttonSend.Enabled = false;
        buttonSend.FlatAppearance.BorderSize = 0;
        buttonSend.FlatStyle = FlatStyle.Flat;
        buttonSend.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        buttonSend.ForeColor = Color.FromArgb(235, 235, 235);
        buttonSend.Location = new Point(871, 4);
        buttonSend.Name = "buttonSend";
        buttonSend.Size = new Size(60, 28);
        buttonSend.TabIndex = 5;
        buttonSend.Text = "▶ Send";
        toolTipMain.SetToolTip(buttonSend, "Send prompt to Copilot (Ctrl+Enter)");
        buttonSend.UseVisualStyleBackColor = false;
        // 
        // tabControlOutput
        // 
        tabControlOutput.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        tabControlOutput.Controls.Add(tabPageRendered);
        tabControlOutput.Controls.Add(tabPageRaw);
        tabControlOutput.DrawMode = TabDrawMode.OwnerDrawFixed;
        tabControlOutput.ForeColor = Color.FromArgb(218, 218, 218);
        tabControlOutput.Location = new Point(6, 0);
        tabControlOutput.Margin = new Padding(0);
        tabControlOutput.Name = "tabControlOutput";
        tabControlOutput.SelectedIndex = 0;
        tabControlOutput.Size = new Size(925, 316);
        tabControlOutput.TabIndex = 0;
        tabControlOutput.DrawItem += TabControlOutput_DrawItem;
        // 
        // tabPageRendered
        // 
        tabPageRendered.BackColor = Color.FromArgb(0, 0, 0);
        tabPageRendered.Controls.Add(webViewOutput);
        tabPageRendered.Location = new Point(4, 24);
        tabPageRendered.Margin = new Padding(3, 2, 3, 2);
        tabPageRendered.Name = "tabPageRendered";
        tabPageRendered.Size = new Size(917, 288);
        tabPageRendered.TabIndex = 0;
        tabPageRendered.Text = "Rendered";
        // 
        // webViewOutput
        // 
        webViewOutput.AllowExternalDrop = true;
        webViewOutput.CreationProperties = null;
        webViewOutput.DefaultBackgroundColor = Color.FromArgb(0, 0, 0);
        webViewOutput.Dock = DockStyle.Fill;
        webViewOutput.ForeColor = Color.DimGray;
        webViewOutput.Location = new Point(0, 0);
        webViewOutput.Margin = new Padding(3, 2, 3, 2);
        webViewOutput.Name = "webViewOutput";
        webViewOutput.Size = new Size(917, 288);
        webViewOutput.TabIndex = 0;
        webViewOutput.ZoomFactor = 1D;
        webViewOutput.Click += webViewOutput_Click;
        // 
        // tabPageRaw
        // 
        tabPageRaw.BackColor = Color.FromArgb(0, 0, 0);
        tabPageRaw.Controls.Add(richTextBoxOutput);
        tabPageRaw.Location = new Point(4, 24);
        tabPageRaw.Margin = new Padding(3, 2, 3, 2);
        tabPageRaw.Name = "tabPageRaw";
        tabPageRaw.Size = new Size(917, 288);
        tabPageRaw.TabIndex = 1;
        tabPageRaw.Text = "Raw";
        // 
        // richTextBoxOutput
        // 
        richTextBoxOutput.BackColor = Color.FromArgb(0, 0, 0);
        richTextBoxOutput.BorderStyle = BorderStyle.None;
        richTextBoxOutput.DetectUrls = false;
        richTextBoxOutput.Dock = DockStyle.Fill;
        richTextBoxOutput.Font = new Font("Consolas", 10F);
        richTextBoxOutput.ForeColor = Color.FromArgb(218, 218, 218);
        richTextBoxOutput.Location = new Point(0, 0);
        richTextBoxOutput.Margin = new Padding(3, 2, 3, 2);
        richTextBoxOutput.Name = "richTextBoxOutput";
        richTextBoxOutput.ReadOnly = true;
        richTextBoxOutput.ScrollBars = RichTextBoxScrollBars.Vertical;
        richTextBoxOutput.Size = new Size(917, 288);
        richTextBoxOutput.TabIndex = 0;
        richTextBoxOutput.Text = "";
        // 
        // contextMenuOptions
        // 
        contextMenuOptions.BackColor = Color.FromArgb(56, 56, 56);
        contextMenuOptions.ForeColor = Color.FromArgb(218, 218, 218);
        contextMenuOptions.ImageScalingSize = new Size(32, 32);
        contextMenuOptions.Items.AddRange(new ToolStripItem[] { menuOptionAutoApprove, menuOptionFleet, menuSessionCaveman, menuOptionLocalFilter, menuSessionShowSteps });
        contextMenuOptions.Name = "contextMenuOptions";
        contextMenuOptions.Size = new Size(183, 92);
        // 
        // menuOptionAutoApprove
        // 
        menuOptionAutoApprove.BackColor = Color.FromArgb(56, 56, 56);
        menuOptionAutoApprove.CheckOnClick = true;
        menuOptionAutoApprove.ForeColor = Color.FromArgb(218, 218, 218);
        menuOptionAutoApprove.Name = "menuOptionAutoApprove";
        menuOptionAutoApprove.Size = new Size(182, 22);
        menuOptionAutoApprove.Text = "&Auto-approve tools";
        menuOptionAutoApprove.ToolTipText = resources.GetString("menuOptionAutoApprove.ToolTipText");
        // 
        // menuOptionFleet
        // 
        menuOptionFleet.BackColor = Color.FromArgb(56, 56, 56);
        menuOptionFleet.CheckOnClick = true;
        menuOptionFleet.ForeColor = Color.FromArgb(218, 218, 218);
        menuOptionFleet.Name = "menuOptionFleet";
        menuOptionFleet.Size = new Size(182, 22);
        menuOptionFleet.Text = "&Fleet mode";
        menuOptionFleet.ToolTipText = "Activate Fleet mode — Copilot spawns and coordinates multiple sub-agents to work in parallel on complex tasks";
        // 
        // menuSessionCaveman
        // 
        menuSessionCaveman.BackColor = Color.FromArgb(56, 56, 56);
        menuSessionCaveman.CheckOnClick = true;
        menuSessionCaveman.ForeColor = Color.FromArgb(218, 218, 218);
        menuSessionCaveman.Name = "menuSessionCaveman";
        menuSessionCaveman.Size = new Size(182, 22);
        menuSessionCaveman.Text = "Ca&veman Mode";
        menuSessionCaveman.ToolTipText = "Reduce each prompt to caveman speak before sending. Saves tokens; may lose nuance.";
        // 
        // menuOptionLocalFilter
        // 
        menuOptionLocalFilter.BackColor = Color.FromArgb(56, 56, 56);
        menuOptionLocalFilter.CheckOnClick = true;
        menuOptionLocalFilter.ForeColor = Color.FromArgb(218, 218, 218);
        menuOptionLocalFilter.Name = "menuOptionLocalFilter";
        menuOptionLocalFilter.Size = new Size(182, 22);
        menuOptionLocalFilter.Text = "&Local LLM filter";
        menuOptionLocalFilter.ToolTipText = "Pre-process prompts with a local codellama model: answer simple requests directly, otherwise minimize tokens before sending to the cloud.";
        // 
        // menuSessionShowSteps
        // 
        menuSessionShowSteps.BackColor = Color.FromArgb(56, 56, 56);
        menuSessionShowSteps.CheckOnClick = true;
        menuSessionShowSteps.ForeColor = Color.FromArgb(218, 218, 218);
        menuSessionShowSteps.Name = "menuSessionShowSteps";
        menuSessionShowSteps.Size = new Size(182, 22);
        menuSessionShowSteps.Text = "S&how Working Steps";
        menuSessionShowSteps.ToolTipText = "Keep Reasoning and Tool sections expanded after they finish (off = auto-collapse to a one-line summary).";
        // 
        // menuStripMain
        // 
        menuStripMain.BackColor = Color.FromArgb(74, 74, 74);
        menuStripMain.ForeColor = Color.FromArgb(218, 218, 218);
        menuStripMain.ImageScalingSize = new Size(32, 32);
        menuStripMain.Items.AddRange(new ToolStripItem[] { menuSession, menuReferences, menuTools, menuHelp });
        menuStripMain.Location = new Point(0, 0);
        menuStripMain.Name = "menuStripMain";
        menuStripMain.Padding = new Padding(4, 2, 0, 2);
        menuStripMain.Size = new Size(940, 24);
        menuStripMain.TabIndex = 2;
        // 
        // menuSession
        // 
        menuSession.DropDownItems.AddRange(new ToolStripItem[] { menuSessionNew, menuSessionPast, menuSessionSeparatorNew, menuSessionSummarize, menuSessionClear, menuSessionRefresh });
        menuSession.ForeColor = Color.FromArgb(218, 218, 218);
        menuSession.Name = "menuSession";
        menuSession.Size = new Size(58, 20);
        menuSession.Text = "&Session";
        // 
        // menuSessionNew
        // 
        menuSessionNew.BackColor = Color.FromArgb(56, 56, 56);
        menuSessionNew.ForeColor = Color.FromArgb(218, 218, 218);
        menuSessionNew.Name = "menuSessionNew";
        menuSessionNew.Size = new Size(167, 22);
        menuSessionNew.Text = "📂 &New Session...";
        menuSessionNew.ToolTipText = "Select a workspace folder and start a new Copilot session";
        // 
        // menuSessionPast
        // 
        menuSessionPast.BackColor = Color.FromArgb(56, 56, 56);
        menuSessionPast.ForeColor = Color.FromArgb(218, 218, 218);
        menuSessionPast.Name = "menuSessionPast";
        menuSessionPast.Size = new Size(167, 22);
        menuSessionPast.Text = "📋 &Past Sessions...";
        menuSessionPast.ToolTipText = "Browse persisted sessions to resume or delete them";
        // 
        // menuSessionSeparatorNew
        // 
        menuSessionSeparatorNew.BackColor = Color.FromArgb(56, 56, 56);
        menuSessionSeparatorNew.ForeColor = Color.FromArgb(218, 218, 218);
        menuSessionSeparatorNew.Name = "menuSessionSeparatorNew";
        menuSessionSeparatorNew.Size = new Size(164, 6);
        // 
        // menuSessionSummarize
        // 
        menuSessionSummarize.BackColor = Color.FromArgb(56, 56, 56);
        menuSessionSummarize.ForeColor = Color.FromArgb(218, 218, 218);
        menuSessionSummarize.Name = "menuSessionSummarize";
        menuSessionSummarize.Size = new Size(167, 22);
        menuSessionSummarize.Text = "📝 S&ummarize";
        menuSessionSummarize.ToolTipText = "Ask Copilot to summarize the session so far";
        // 
        // menuSessionClear
        // 
        menuSessionClear.BackColor = Color.FromArgb(56, 56, 56);
        menuSessionClear.ForeColor = Color.FromArgb(218, 218, 218);
        menuSessionClear.Name = "menuSessionClear";
        menuSessionClear.Size = new Size(167, 22);
        menuSessionClear.Text = "🗑 &Clear Output";
        menuSessionClear.ToolTipText = "Clear the current output window";
        // 
        // menuSessionRefresh
        // 
        menuSessionRefresh.BackColor = Color.FromArgb(56, 56, 56);
        menuSessionRefresh.DropDownItems.AddRange(new ToolStripItem[] { menuSessionRefreshCompact, menuSessionRefreshRestart, menuSessionRefreshFresh });
        menuSessionRefresh.ForeColor = Color.FromArgb(218, 218, 218);
        menuSessionRefresh.Name = "menuSessionRefresh";
        menuSessionRefresh.Size = new Size(167, 22);
        menuSessionRefresh.Text = "💤 &Refresh";
        menuSessionRefresh.ToolTipText = "Free up context window — Compact (in place) or Restart with summary";
        // 
        // menuSessionRefreshCompact
        // 
        menuSessionRefreshCompact.BackColor = Color.FromArgb(56, 56, 56);
        menuSessionRefreshCompact.ForeColor = Color.FromArgb(218, 218, 218);
        menuSessionRefreshCompact.Name = "menuSessionRefreshCompact";
        menuSessionRefreshCompact.Size = new Size(288, 22);
        menuSessionRefreshCompact.Text = "⚡ &Compact (fast, keeps session)";
        menuSessionRefreshCompact.ToolTipText = "Ask the CLI to summarise history in place. Session ID is preserved.";
        // 
        // menuSessionRefreshRestart
        // 
        menuSessionRefreshRestart.BackColor = Color.FromArgb(56, 56, 56);
        menuSessionRefreshRestart.ForeColor = Color.FromArgb(218, 218, 218);
        menuSessionRefreshRestart.Name = "menuSessionRefreshRestart";
        menuSessionRefreshRestart.Size = new Size(288, 22);
        menuSessionRefreshRestart.Text = "🔄 &Restart with summary (clean window)";
        menuSessionRefreshRestart.ToolTipText = "Save a Markdown dream file, open a fresh session in this folder, and seed it with the summary.";
        // 
        // menuSessionRefreshFresh
        // 
        menuSessionRefreshFresh.BackColor = Color.FromArgb(56, 56, 56);
        menuSessionRefreshFresh.ForeColor = Color.FromArgb(218, 218, 218);
        menuSessionRefreshFresh.Name = "menuSessionRefreshFresh";
        menuSessionRefreshFresh.Size = new Size(288, 22);
        menuSessionRefreshFresh.Text = "🆕 &Fresh start (no carry-over)";
        menuSessionRefreshFresh.ToolTipText = "Discard all context and open a brand-new session in this folder, as if you had just used New Session.";
        // 
        // menuReferences
        // 
        menuReferences.DropDownItems.AddRange(new ToolStripItem[] { menuReferencesAddFile, menuReferencesAddFolder, menuReferencesSeparator1, menuReferencesListAgents, menuReferencesListSkills, menuReferencesListPrompts });
        menuReferences.ForeColor = Color.FromArgb(218, 218, 218);
        menuReferences.Name = "menuReferences";
        menuReferences.Size = new Size(76, 20);
        menuReferences.Text = "&References";
        // 
        // menuReferencesAddFile
        // 
        menuReferencesAddFile.BackColor = Color.FromArgb(56, 56, 56);
        menuReferencesAddFile.ForeColor = Color.FromArgb(218, 218, 218);
        menuReferencesAddFile.Name = "menuReferencesAddFile";
        menuReferencesAddFile.Size = new Size(156, 22);
        menuReferencesAddFile.Text = "📄 Add &File...";
        menuReferencesAddFile.ToolTipText = "Attach a file to the prompt";
        // 
        // menuReferencesAddFolder
        // 
        menuReferencesAddFolder.BackColor = Color.FromArgb(56, 56, 56);
        menuReferencesAddFolder.ForeColor = Color.FromArgb(218, 218, 218);
        menuReferencesAddFolder.Name = "menuReferencesAddFolder";
        menuReferencesAddFolder.Size = new Size(156, 22);
        menuReferencesAddFolder.Text = "📁 Add F&older...";
        menuReferencesAddFolder.ToolTipText = "Attach a folder to the prompt";
        // 
        // menuReferencesSeparator1
        // 
        menuReferencesSeparator1.BackColor = Color.FromArgb(56, 56, 56);
        menuReferencesSeparator1.ForeColor = Color.FromArgb(218, 218, 218);
        menuReferencesSeparator1.Name = "menuReferencesSeparator1";
        menuReferencesSeparator1.Size = new Size(153, 6);
        // 
        // menuReferencesListAgents
        // 
        menuReferencesListAgents.BackColor = Color.FromArgb(56, 56, 56);
        menuReferencesListAgents.ForeColor = Color.FromArgb(218, 218, 218);
        menuReferencesListAgents.Name = "menuReferencesListAgents";
        menuReferencesListAgents.Size = new Size(156, 22);
        menuReferencesListAgents.Text = "List &Agents...";
        menuReferencesListAgents.ToolTipText = "Show every custom agent registered in the current session";
        // 
        // menuReferencesListSkills
        // 
        menuReferencesListSkills.BackColor = Color.FromArgb(56, 56, 56);
        menuReferencesListSkills.ForeColor = Color.FromArgb(218, 218, 218);
        menuReferencesListSkills.Name = "menuReferencesListSkills";
        menuReferencesListSkills.Size = new Size(156, 22);
        menuReferencesListSkills.Text = "List &Skills...";
        menuReferencesListSkills.ToolTipText = "Show every skill discovered across the current session's tier folders";
        // 
        // menuReferencesListPrompts
        // 
        menuReferencesListPrompts.BackColor = Color.FromArgb(56, 56, 56);
        menuReferencesListPrompts.ForeColor = Color.FromArgb(218, 218, 218);
        menuReferencesListPrompts.Name = "menuReferencesListPrompts";
        menuReferencesListPrompts.Size = new Size(156, 22);
        menuReferencesListPrompts.Text = "List &Prompts...";
        menuReferencesListPrompts.ToolTipText = "Show every prompt template found under prompts/ in the current session's tier folders";
        // 
        // menuTools
        // 
        menuTools.DropDownItems.AddRange(new ToolStripItem[] { menuToolsPowershell, menuToolsExplorer, menuToolsVSCode, menuToolsSeparator1, menuToolsSkillTree, menuToolsSkillSources, menuToolsBrowseCatalog, menuToolsSeparator2, menuToolsPermissions });
        menuTools.ForeColor = Color.FromArgb(218, 218, 218);
        menuTools.Name = "menuTools";
        menuTools.Size = new Size(47, 20);
        menuTools.Text = "&Tools";
        // 
        // menuToolsPowershell
        // 
        menuToolsPowershell.BackColor = Color.FromArgb(56, 56, 56);
        menuToolsPowershell.ForeColor = Color.FromArgb(218, 218, 218);
        menuToolsPowershell.Name = "menuToolsPowershell";
        menuToolsPowershell.Size = new Size(222, 22);
        menuToolsPowershell.Text = "⚡ &PowerShell";
        menuToolsPowershell.ToolTipText = "Open PowerShell in the current project folder";
        // 
        // menuToolsExplorer
        // 
        menuToolsExplorer.BackColor = Color.FromArgb(56, 56, 56);
        menuToolsExplorer.ForeColor = Color.FromArgb(218, 218, 218);
        menuToolsExplorer.Name = "menuToolsExplorer";
        menuToolsExplorer.Size = new Size(222, 22);
        menuToolsExplorer.Text = "📂 File &Explorer";
        menuToolsExplorer.ToolTipText = "Open File Explorer in the current session folder";
        // 
        // menuToolsVSCode
        // 
        menuToolsVSCode.BackColor = Color.FromArgb(56, 56, 56);
        menuToolsVSCode.ForeColor = Color.FromArgb(218, 218, 218);
        menuToolsVSCode.Name = "menuToolsVSCode";
        menuToolsVSCode.Size = new Size(222, 22);
        menuToolsVSCode.Text = "💻 &VS Code";
        menuToolsVSCode.ToolTipText = "Open VS Code in the session folder. (Live IDE pairing is not available through the Copilot SDK.)";
        // 
        // menuToolsSeparator1
        // 
        menuToolsSeparator1.BackColor = Color.FromArgb(56, 56, 56);
        menuToolsSeparator1.ForeColor = Color.FromArgb(218, 218, 218);
        menuToolsSeparator1.Name = "menuToolsSeparator1";
        menuToolsSeparator1.Size = new Size(219, 6);
        // 
        // menuToolsSkillTree
        // 
        menuToolsSkillTree.BackColor = Color.FromArgb(56, 56, 56);
        menuToolsSkillTree.ForeColor = Color.FromArgb(218, 218, 218);
        menuToolsSkillTree.Name = "menuToolsSkillTree";
        menuToolsSkillTree.Size = new Size(222, 22);
        menuToolsSkillTree.Text = "🌳 Skill &Tree...";
        menuToolsSkillTree.ToolTipText = "Edit the Skill Tree (folders contributing skills/ and agents/ to the session)";
        // 
        // menuToolsSkillSources
        // 
        menuToolsSkillSources.BackColor = Color.FromArgb(56, 56, 56);
        menuToolsSkillSources.ForeColor = Color.FromArgb(218, 218, 218);
        menuToolsSkillSources.Name = "menuToolsSkillSources";
        menuToolsSkillSources.Size = new Size(222, 22);
        menuToolsSkillSources.Text = "🌐 Skill &Sources...";
        menuToolsSkillSources.ToolTipText = "Edit the list of remote URLs scanned by Browse Skill Catalog";
        // 
        // menuToolsBrowseCatalog
        // 
        menuToolsBrowseCatalog.BackColor = Color.FromArgb(56, 56, 56);
        menuToolsBrowseCatalog.ForeColor = Color.FromArgb(218, 218, 218);
        menuToolsBrowseCatalog.Name = "menuToolsBrowseCatalog";
        menuToolsBrowseCatalog.Size = new Size(222, 22);
        menuToolsBrowseCatalog.Text = "📥 Browse Skill &Catalog...";
        menuToolsBrowseCatalog.ToolTipText = "Browse and download skills, agents, prompts, and instructions from the configured Skill Sources";
        // 
        // menuToolsSeparator2
        // 
        menuToolsSeparator2.BackColor = Color.FromArgb(56, 56, 56);
        menuToolsSeparator2.ForeColor = Color.FromArgb(218, 218, 218);
        menuToolsSeparator2.Name = "menuToolsSeparator2";
        menuToolsSeparator2.Size = new Size(219, 6);
        // 
        // menuToolsPermissions
        // 
        menuToolsPermissions.BackColor = Color.FromArgb(56, 56, 56);
        menuToolsPermissions.ForeColor = Color.FromArgb(218, 218, 218);
        menuToolsPermissions.Name = "menuToolsPermissions";
        menuToolsPermissions.Size = new Size(222, 22);
        menuToolsPermissions.Text = "🔐 Workspace &Permissions...";
        menuToolsPermissions.ToolTipText = "View and edit trusted folders and pre-approved tool operations stored in ~/.copilot/permissions-config.json";
        // 
        // menuHelp
        // 
        menuHelp.DropDownItems.AddRange(new ToolStripItem[] { menuHelpShow, menuHelpAbout });
        menuHelp.ForeColor = Color.FromArgb(218, 218, 218);
        menuHelp.Name = "menuHelp";
        menuHelp.Size = new Size(44, 20);
        menuHelp.Text = "&Help";
        // 
        // menuHelpShow
        // 
        menuHelpShow.BackColor = Color.FromArgb(56, 56, 56);
        menuHelpShow.ForeColor = Color.FromArgb(218, 218, 218);
        menuHelpShow.Name = "menuHelpShow";
        menuHelpShow.Size = new Size(149, 22);
        menuHelpShow.Text = "❓ Show &Help";
        menuHelpShow.ToolTipText = "Ask Copilot for a capabilities and tools overview";
        // 
        // menuHelpAbout
        // 
        menuHelpAbout.BackColor = Color.FromArgb(56, 56, 56);
        menuHelpAbout.ForeColor = Color.FromArgb(218, 218, 218);
        menuHelpAbout.Name = "menuHelpAbout";
        menuHelpAbout.Size = new Size(149, 22);
        menuHelpAbout.Text = "&About GoPilot";
        menuHelpAbout.ToolTipText = "Show version and credits";
        // 
        // statusStrip
        // 
        statusStrip.BackColor = Color.FromArgb(56, 56, 56);
        statusStrip.ImageScalingSize = new Size(20, 20);
        statusStrip.Items.AddRange(new ToolStripItem[] { toolStripStatusLabelConnection, toolStripStatusLabelVersion, toolStripStatusLabelSep, toolStripStatusLabelAgentStatus, toolStripProgressBarContext, toolStripStatusLabelContext, toolStripStatusLabelAic, toolStripStatusLabelSession });
        statusStrip.Location = new Point(0, 594);
        statusStrip.Name = "statusStrip";
        statusStrip.Size = new Size(940, 23);
        statusStrip.TabIndex = 1;
        // 
        // toolStripStatusLabelConnection
        // 
        toolStripStatusLabelConnection.ForeColor = Color.FromArgb(218, 218, 218);
        toolStripStatusLabelConnection.Name = "toolStripStatusLabelConnection";
        toolStripStatusLabelConnection.Size = new Size(86, 18);
        toolStripStatusLabelConnection.Text = "Not connected";
        // 
        // toolStripStatusLabelVersion
        // 
        toolStripStatusLabelVersion.ForeColor = Color.FromArgb(148, 148, 148);
        toolStripStatusLabelVersion.Name = "toolStripStatusLabelVersion";
        toolStripStatusLabelVersion.Padding = new Padding(6, 0, 0, 0);
        toolStripStatusLabelVersion.Size = new Size(6, 18);
        // 
        // toolStripStatusLabelSep
        // 
        toolStripStatusLabelSep.ForeColor = Color.FromArgb(148, 148, 148);
        toolStripStatusLabelSep.Name = "toolStripStatusLabelSep";
        toolStripStatusLabelSep.Size = new Size(6, 23);
        // 
        // toolStripStatusLabelAgentStatus
        // 
        toolStripStatusLabelAgentStatus.AutoSize = false;
        toolStripStatusLabelAgentStatus.ForeColor = Color.FromArgb(200, 200, 200);
        toolStripStatusLabelAgentStatus.Name = "toolStripStatusLabelAgentStatus";
        toolStripStatusLabelAgentStatus.Size = new Size(320, 18);
        toolStripStatusLabelAgentStatus.Text = "Ready for next command";
        toolStripStatusLabelAgentStatus.TextAlign = ContentAlignment.MiddleLeft;
        // 
        // toolStripProgressBarContext
        // 
        toolStripProgressBarContext.BackColor = Color.FromArgb(46, 46, 46);
        toolStripProgressBarContext.ForeColor = Color.FromArgb(148, 220, 148);
        toolStripProgressBarContext.Margin = new Padding(4, 3, 0, 3);
        toolStripProgressBarContext.Name = "toolStripProgressBarContext";
        toolStripProgressBarContext.Size = new Size(80, 17);
        toolStripProgressBarContext.Style = ProgressBarStyle.Continuous;
        // 
        // toolStripStatusLabelContext
        // 
        toolStripStatusLabelContext.ForeColor = Color.FromArgb(148, 148, 148);
        toolStripStatusLabelContext.Name = "toolStripStatusLabelContext";
        toolStripStatusLabelContext.Padding = new Padding(8, 0, 8, 0);
        toolStripStatusLabelContext.Size = new Size(16, 18);
        // 
        // toolStripStatusLabelAic
        // 
        toolStripStatusLabelAic.ForeColor = Color.FromArgb(148, 148, 148);
        toolStripStatusLabelAic.Name = "toolStripStatusLabelAic";
        toolStripStatusLabelAic.Padding = new Padding(8, 0, 8, 0);
        toolStripStatusLabelAic.Size = new Size(16, 18);
        // 
        // toolStripStatusLabelSession
        // 
        toolStripStatusLabelSession.ForeColor = Color.FromArgb(148, 148, 148);
        toolStripStatusLabelSession.Name = "toolStripStatusLabelSession";
        toolStripStatusLabelSession.Size = new Size(0, 18);
        toolStripStatusLabelSession.TextAlign = ContentAlignment.MiddleRight;
        // 
        // MainForm
        // 
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        BackColor = Color.FromArgb(64, 64, 64);
        ClientSize = new Size(940, 617);
        Controls.Add(splitContainerMain);
        Controls.Add(statusStrip);
        Controls.Add(menuStripMain);
        Font = new Font("Segoe UI", 9F);
        Icon = (Icon)resources.GetObject("$this.Icon");
        MainMenuStrip = menuStripMain;
        MinimumSize = new Size(954, 574);
        Name = "MainForm";
        Text = "GoPilot";
        splitContainerMain.Panel1.ResumeLayout(false);
        splitContainerMain.Panel2.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)splitContainerMain).EndInit();
        splitContainerMain.ResumeLayout(false);
        panelAttachments.ResumeLayout(false);
        panelAttachments.PerformLayout();
        panelHistoryNav.ResumeLayout(false);
        panelActions.ResumeLayout(false);
        panelActions.PerformLayout();
        tabControlOutput.ResumeLayout(false);
        tabPageRendered.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)webViewOutput).EndInit();
        tabPageRaw.ResumeLayout(false);
        contextMenuOptions.ResumeLayout(false);
        menuStripMain.ResumeLayout(false);
        menuStripMain.PerformLayout();
        statusStrip.ResumeLayout(false);
        statusStrip.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private System.Windows.Forms.SplitContainer splitContainerMain;
	private DarkTabControl tabControlOutput;
	private System.Windows.Forms.TabPage tabPageRendered;
	private System.Windows.Forms.TabPage tabPageRaw;
	private Microsoft.Web.WebView2.WinForms.WebView2 webViewOutput;
    private System.Windows.Forms.RichTextBox richTextBoxOutput;
    private System.Windows.Forms.MenuStrip menuStripMain;
    private System.Windows.Forms.ToolStripMenuItem menuTools;
    private System.Windows.Forms.ToolStripMenuItem menuToolsPowershell;
    private System.Windows.Forms.ToolStripMenuItem menuToolsExplorer;
    private System.Windows.Forms.ToolStripMenuItem menuToolsVSCode;
    private System.Windows.Forms.ToolStripSeparator menuToolsSeparator1;
    private System.Windows.Forms.ToolStripMenuItem menuToolsSkillTree;
    private System.Windows.Forms.ToolStripMenuItem menuToolsSkillSources;
    private System.Windows.Forms.ToolStripMenuItem menuToolsBrowseCatalog;
    private System.Windows.Forms.ToolStripSeparator menuToolsSeparator2;
    private System.Windows.Forms.ToolStripMenuItem menuToolsPermissions;
    private System.Windows.Forms.ToolStripMenuItem menuSession;
    private System.Windows.Forms.ToolStripMenuItem menuSessionNew;
    private System.Windows.Forms.ToolStripSeparator menuSessionSeparatorNew;
    private System.Windows.Forms.ToolStripMenuItem menuSessionSummarize;
    private System.Windows.Forms.ToolStripMenuItem menuSessionClear;
    private System.Windows.Forms.ToolStripMenuItem menuSessionRefresh;
    private System.Windows.Forms.ToolStripMenuItem menuSessionRefreshCompact;
    private System.Windows.Forms.ToolStripMenuItem menuSessionRefreshRestart;
    private System.Windows.Forms.ToolStripMenuItem menuSessionRefreshFresh;
    private System.Windows.Forms.ToolStripMenuItem menuSessionCaveman;
    private System.Windows.Forms.ToolStripMenuItem menuSessionShowSteps;
    private System.Windows.Forms.ToolStripMenuItem menuOptionLocalFilter;
    private System.Windows.Forms.ToolStripMenuItem menuSessionPast;
    private System.Windows.Forms.ToolStripMenuItem menuHelp;
    private System.Windows.Forms.ToolStripMenuItem menuHelpShow;
    private System.Windows.Forms.ToolStripMenuItem menuHelpAbout;
    private System.Windows.Forms.ToolStripMenuItem menuReferences;
    private System.Windows.Forms.ToolStripMenuItem menuReferencesAddFile;
    private System.Windows.Forms.ToolStripMenuItem menuReferencesAddFolder;
    private System.Windows.Forms.ToolStripSeparator menuReferencesSeparator1;
    private System.Windows.Forms.ToolStripMenuItem menuReferencesListAgents;
    private System.Windows.Forms.ToolStripMenuItem menuReferencesListSkills;
    private System.Windows.Forms.ToolStripMenuItem menuReferencesListPrompts;
    private System.Windows.Forms.Panel panelAttachments;
    private System.Windows.Forms.Label labelAttach;
    private System.Windows.Forms.FlowLayoutPanel flowLayoutPanelChips;
    private PlainRichTextBox richTextBoxPrompt;
    private System.Windows.Forms.Panel panelActions;
    private System.Windows.Forms.Panel panelHistoryNav;
    private System.Windows.Forms.Button buttonHistoryPrev;
    private System.Windows.Forms.Button buttonHistoryNext;
    private System.Windows.Forms.Button buttonOptions;
    private System.Windows.Forms.ContextMenuStrip contextMenuOptions;
    private System.Windows.Forms.ToolStripMenuItem menuOptionAutoApprove;
    private System.Windows.Forms.ToolStripMenuItem menuOptionFleet;
    private System.Windows.Forms.Label labelModel;
    private System.Windows.Forms.ComboBox comboBoxModel;
    private System.Windows.Forms.Label labelMode;
    private System.Windows.Forms.ComboBox comboBoxMode;
    private System.Windows.Forms.Label labelEffort;
    private System.Windows.Forms.ComboBox comboBoxEffort;
    private System.Windows.Forms.Button buttonStop;
    private System.Windows.Forms.Button buttonSend;
    private System.Windows.Forms.StatusStrip statusStrip;
    private System.Windows.Forms.ToolStripStatusLabel toolStripStatusLabelConnection;
    private System.Windows.Forms.ToolStripStatusLabel toolStripStatusLabelVersion;
    private System.Windows.Forms.ToolStripSeparator toolStripStatusLabelSep;
    private System.Windows.Forms.ToolStripStatusLabel toolStripStatusLabelAgentStatus;
    private System.Windows.Forms.ToolStripProgressBar toolStripProgressBarContext;
    private System.Windows.Forms.ToolStripStatusLabel toolStripStatusLabelContext;
    private System.Windows.Forms.ToolStripStatusLabel toolStripStatusLabelAic;
    private System.Windows.Forms.ToolStripStatusLabel toolStripStatusLabelSession;
    private System.Windows.Forms.ToolTip toolTipMain;
}
