namespace GoPilot;

public partial class UserInputDialog : Form
{
	private readonly UserInputEventArgs _args;

	public UserInputDialog(UserInputEventArgs args)
	{
		_args = args;
		InitializeComponent();
		PopulateQuestion();
	}

	private void PopulateQuestion()
	{
		richTextBoxQuestion.Text = _args.Question;

		if (_args.Choices is { Count: > 0 })
		{
			listBoxChoices.Items.Clear();
			foreach (var choice in _args.Choices)
				listBoxChoices.Items.Add(choice);
			listBoxChoices.Visible = true;
			listBoxChoices.SelectedIndex = 0;
			labelOrType.Visible = _args.AllowFreeform;
			textBoxAnswer.Visible = _args.AllowFreeform;
		}
		else
		{
			listBoxChoices.Visible = false;
			labelOrType.Visible = false;
			textBoxAnswer.Visible = true;
		}
		// Do NOT call AdjustFormSize() here -- controls have no handle yet.
		// Sizing is deferred to OnLoad() where all control dimensions are final.
	}

	protected override void OnLoad(EventArgs e)
	{
		base.OnLoad(e);
		SuspendLayout();
		SizeQuestionBox();
		SizeChoicesBox();
		ResumeLayout(false);
		AdjustFormSize();

		if (textBoxAnswer.Visible)
			ActiveControl = textBoxAnswer;
		else if (listBoxChoices.Visible)
			ActiveControl = listBoxChoices;
	}

	/// <summary>
	/// Auto-sizes the question display box to fit its text content,
	/// capped at <paramref name="maxH"/> pixels (scrollable above that).
	/// </summary>
	private void SizeQuestionBox()
	{
		const int minH = 44;
		const int maxH = 200;
		int measureWidth = Math.Max(100, richTextBoxQuestion.Width - 8);
		var sz = TextRenderer.MeasureText(
			richTextBoxQuestion.Text,
			richTextBoxQuestion.Font,
			new Size(measureWidth, int.MaxValue),
			TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
		richTextBoxQuestion.Height = Math.Max(minH, Math.Min(maxH, sz.Height + 12));
	}

	// Horizontal and vertical inner padding applied when drawing each list item.
	private const int _listItemPadH = 6;
	private const int _listItemPadV = 3;

	/// <summary>
	/// Auto-sizes the choices list box to fit all word-wrapped items,
	/// capped at 240 pixels (scrollable above that).
	/// </summary>
	private void SizeChoicesBox()
	{
		if (!listBoxChoices.Visible || listBoxChoices.Items.Count == 0)
			return;
		const int maxH = 240;
		int availW = Math.Max(10, listBoxChoices.ClientSize.Width - 2 * _listItemPadH);
		int total = 4;
		foreach (object item in listBoxChoices.Items)
		{
			string text = item?.ToString() ?? "";
			var sz = TextRenderer.MeasureText(
				text,
				listBoxChoices.Font,
				new Size(availW, int.MaxValue),
				TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
			total += sz.Height + _listItemPadV * 2;
		}
		listBoxChoices.Height = Math.Max(24, Math.Min(maxH, total));
	}

	private void ListBoxChoices_MeasureItem(object? sender, MeasureItemEventArgs e)
	{
		if (e.Index < 0 || e.Index >= listBoxChoices.Items.Count)
			return;
		string text = listBoxChoices.Items[e.Index]?.ToString() ?? "";
		int availW = Math.Max(10, listBoxChoices.ClientSize.Width - 2 * _listItemPadH);
		var sz = TextRenderer.MeasureText(
			text,
			listBoxChoices.Font,
			new Size(availW, int.MaxValue),
			TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
		e.ItemHeight = sz.Height + _listItemPadV * 2;
	}

	private void ListBoxChoices_DrawItem(object? sender, DrawItemEventArgs e)
	{
		if (e.Index < 0 || e.Index >= listBoxChoices.Items.Count)
			return;
		string text = listBoxChoices.Items[e.Index]?.ToString() ?? "";
		bool selected = (e.State & DrawItemState.Selected) != 0;
		Color fg = selected ? AppTheme.AccentText : AppTheme.TextPrimary;
		Color bg = selected ? AppTheme.AccentBg   : AppTheme.InputBox;

		using var brush = new SolidBrush(bg);
		e.Graphics.FillRectangle(brush, e.Bounds);

		var textRect = new Rectangle(
			e.Bounds.X + _listItemPadH,
			e.Bounds.Y + _listItemPadV,
			e.Bounds.Width  - 2 * _listItemPadH,
			e.Bounds.Height - 2 * _listItemPadV);

		TextRenderer.DrawText(
			e.Graphics, text, e.Font, textRect, fg,
			TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.Top);

		if ((e.State & DrawItemState.Focus) != 0)
			e.DrawFocusRectangle();
	}

	/// <summary>
	/// Computes the exact client height needed for all visible content
	/// and re-centers the dialog over its owner.
	/// </summary>
	private void AdjustFormSize()
	{
		int contentH = this.Padding.Top;
		foreach (Control c in this.Controls)
		{
			if (c.Dock == DockStyle.Top && c.Visible)
				contentH += c.Height;
		}
		this.ClientSize = new Size(
			this.ClientSize.Width,
			contentH + panelButtons.Height + this.Padding.Bottom + 8);

		// Re-center over the owner now that the final size is known.
		if (Owner != null)
		{
			Rectangle ownerRect = Owner.Bounds;
			Rectangle workArea  = Screen.GetWorkingArea(this);
			int x = ownerRect.X + (ownerRect.Width  - Width)  / 2;
			int y = ownerRect.Y + (ownerRect.Height - Height) / 2;
			Location = new Point(
				Math.Max(workArea.Left, Math.Min(workArea.Right  - Width,  x)),
				Math.Max(workArea.Top,  Math.Min(workArea.Bottom - Height, y)));
		}
	}

	private void ButtonSubmit_Click(object? sender, EventArgs e)
	{
		bool hasText   = textBoxAnswer.Visible && !string.IsNullOrWhiteSpace(textBoxAnswer.Text);
		bool hasChoice = listBoxChoices.Visible && listBoxChoices.SelectedItem is string;

		string answer;
		if (hasText)
			answer = textBoxAnswer.Text.Trim();
		else if (hasChoice)
			answer = (string)listBoxChoices.SelectedItem!;
		else
			answer = "";

		_args.Answer.TrySetResult(answer);
		DialogResult = DialogResult.OK;
		Close();
	}

	private void TextBoxAnswer_KeyDown(object? sender, KeyEventArgs e)
	{
		if (e.KeyCode == Keys.Enter && e.Control)
		{
			e.SuppressKeyPress = true;   // prevents the newline AND sets Handled
			ButtonSubmit_Click(sender, e);
		}
	}

	protected override void OnFormClosing(FormClosingEventArgs e)
	{
		if (!_args.Answer.Task.IsCompleted)
		{
			string fallback = listBoxChoices.SelectedItem as string ?? "";
			_args.Answer.TrySetResult(fallback);
		}
		base.OnFormClosing(e);
	}
}
