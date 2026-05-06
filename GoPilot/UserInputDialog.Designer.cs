namespace GoPilot;

partial class UserInputDialog
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
		labelHeading = new System.Windows.Forms.Label();
		richTextBoxQuestion = new System.Windows.Forms.RichTextBox();
		listBoxChoices = new System.Windows.Forms.ListBox();
		labelOrType = new System.Windows.Forms.Label();
		textBoxAnswer = new System.Windows.Forms.TextBox();
		toolTip = new System.Windows.Forms.ToolTip(components);
		panelButtons = new System.Windows.Forms.Panel();
		buttonSubmit = new System.Windows.Forms.Button();
		panelButtons.SuspendLayout();
		this.SuspendLayout();

		// labelHeading
		labelHeading.AutoSize = false;
		labelHeading.Dock = System.Windows.Forms.DockStyle.Top;
		labelHeading.Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Bold);
		labelHeading.ForeColor = AppTheme.TextPrimary;
		labelHeading.Name = "labelHeading";
		labelHeading.Padding = new System.Windows.Forms.Padding(0, 0, 0, 4);
		labelHeading.Size = new System.Drawing.Size(448, 26);
		labelHeading.TabIndex = 0;
		labelHeading.Text = "Copilot needs your input:";

		// richTextBoxQuestion
		richTextBoxQuestion.BackColor = AppTheme.Background;
		richTextBoxQuestion.BorderStyle = System.Windows.Forms.BorderStyle.None;
		richTextBoxQuestion.Dock = System.Windows.Forms.DockStyle.Top;
		richTextBoxQuestion.Font = new System.Drawing.Font("Segoe UI", 9.5F);
		richTextBoxQuestion.ForeColor = AppTheme.TextPrimary;
		richTextBoxQuestion.Name = "richTextBoxQuestion";
		richTextBoxQuestion.ReadOnly = true;
		richTextBoxQuestion.ScrollBars = System.Windows.Forms.RichTextBoxScrollBars.Vertical;
		richTextBoxQuestion.Size = new System.Drawing.Size(448, 100);
		richTextBoxQuestion.TabIndex = 1;
		richTextBoxQuestion.TabStop = false;
		richTextBoxQuestion.WordWrap = true;

		// listBoxChoices
		listBoxChoices.BackColor = AppTheme.InputBox;
		listBoxChoices.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
		listBoxChoices.Dock = System.Windows.Forms.DockStyle.Top;
		listBoxChoices.DrawMode = System.Windows.Forms.DrawMode.OwnerDrawVariable;
		listBoxChoices.Font = new System.Drawing.Font("Segoe UI", 9F);
		listBoxChoices.ForeColor = AppTheme.TextPrimary;
		listBoxChoices.IntegralHeight = false;
		listBoxChoices.Name = "listBoxChoices";
		listBoxChoices.Size = new System.Drawing.Size(448, 90);
		listBoxChoices.TabIndex = 2;
		listBoxChoices.MeasureItem += ListBoxChoices_MeasureItem;
		listBoxChoices.DrawItem   += ListBoxChoices_DrawItem;

		// labelOrType
		labelOrType.AutoSize = false;
		labelOrType.Dock = System.Windows.Forms.DockStyle.Top;
		labelOrType.Font = new System.Drawing.Font("Segoe UI", 9F);
		labelOrType.ForeColor = AppTheme.TextMuted;
		labelOrType.Name = "labelOrType";
		labelOrType.Padding = new System.Windows.Forms.Padding(0, 4, 0, 2);
		labelOrType.Size = new System.Drawing.Size(448, 22);
		labelOrType.TabIndex = 3;
		labelOrType.Text = "Or type a custom answer:";

		// textBoxAnswer
		textBoxAnswer.AcceptsReturn = true;
		textBoxAnswer.BackColor = AppTheme.InputBox;
		textBoxAnswer.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
		textBoxAnswer.Dock = System.Windows.Forms.DockStyle.Top;
		textBoxAnswer.Font = new System.Drawing.Font("Segoe UI", 9.5F);
		textBoxAnswer.ForeColor = AppTheme.TextPrimary;
		textBoxAnswer.Multiline = true;
		textBoxAnswer.Name = "textBoxAnswer";
		textBoxAnswer.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
		textBoxAnswer.Size = new System.Drawing.Size(448, 80);
		textBoxAnswer.TabIndex = 4;
		textBoxAnswer.KeyDown += TextBoxAnswer_KeyDown;
		toolTip.SetToolTip(textBoxAnswer, "Press Ctrl+Enter to submit");

		// panelButtons
		panelButtons.Controls.Add(buttonSubmit);
		panelButtons.BackColor = AppTheme.Background;
		panelButtons.Dock = System.Windows.Forms.DockStyle.Bottom;
		panelButtons.Name = "panelButtons";
		panelButtons.Padding = new System.Windows.Forms.Padding(8, 8, 12, 8);
		panelButtons.Size = new System.Drawing.Size(480, 50);
		panelButtons.TabIndex = 5;

		// buttonSubmit
		buttonSubmit.Anchor = System.Windows.Forms.AnchorStyles.Right;
		buttonSubmit.BackColor = AppTheme.AccentBg;
		buttonSubmit.FlatAppearance.BorderSize = 0;
		buttonSubmit.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
		buttonSubmit.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
		buttonSubmit.ForeColor = AppTheme.AccentText;
		buttonSubmit.Location = new System.Drawing.Point(372, 8);
		buttonSubmit.Name = "buttonSubmit";
		buttonSubmit.Size = new System.Drawing.Size(96, 32);
		buttonSubmit.TabIndex = 0;
		buttonSubmit.Text = "Submit";
		buttonSubmit.UseVisualStyleBackColor = false;
		buttonSubmit.Click += ButtonSubmit_Click;
		toolTip.SetToolTip(buttonSubmit, "Ctrl+Enter");

		// UserInputDialog
		this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
		this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
		this.BackColor = AppTheme.Background;
		this.ClientSize = new System.Drawing.Size(480, 340);
		this.Controls.Add(panelButtons);
		this.Controls.Add(textBoxAnswer);
		this.Controls.Add(labelOrType);
		this.Controls.Add(listBoxChoices);
		this.Controls.Add(richTextBoxQuestion);
		this.Controls.Add(labelHeading);
		this.Font = new System.Drawing.Font("Segoe UI", 9F);
		this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
		this.MaximizeBox = false;
		this.MinimizeBox = false;
		this.MinimumSize = new System.Drawing.Size(480, 200);
		this.Name = "UserInputDialog";
		this.Padding = new System.Windows.Forms.Padding(16, 16, 16, 0);
		this.ShowInTaskbar = false;
		this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
		this.Text = "Copilot Needs Input";

		panelButtons.ResumeLayout(false);
		this.ResumeLayout(false);
		this.PerformLayout();
	}

	#endregion

	private System.Windows.Forms.Label labelHeading;
	private System.Windows.Forms.RichTextBox richTextBoxQuestion;
	private System.Windows.Forms.ListBox listBoxChoices;
	private System.Windows.Forms.Label labelOrType;
	private System.Windows.Forms.TextBox textBoxAnswer;
	private System.Windows.Forms.ToolTip toolTip;
	private System.Windows.Forms.Panel panelButtons;
	private System.Windows.Forms.Button buttonSubmit;
}
