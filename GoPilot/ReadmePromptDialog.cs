namespace GoPilot;

/// <summary>
/// Result of the README prompt shown when a workspace folder is opened.
/// </summary>
public enum ReadmePromptResult
{
    No,
    Yes,
    OpenInVSCode,
}

/// <summary>
/// Small modal dialog asking the user whether GoPilot may read a README file
/// it discovered in the workspace root. Offers a third option to preview the
/// file in VS Code before deciding.
/// </summary>
public sealed class ReadmePromptDialog : Form
{
    public ReadmePromptResult Result { get; private set; } = ReadmePromptResult.No;

    public ReadmePromptDialog(string fileName)
    {
        Text = "Read README?";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 170);

        var label = new Label
        {
            AutoSize = false,
            Location = new Point(16, 16),
            Size = new Size(428, 90),
            Text =
                $"A '{fileName}' was found in the workspace root.\r\n\r\n" +
                "Would you like GoPilot to read it so it can better understand " +
                "the project? You can also preview it in VS Code first.",
        };
        Controls.Add(label);

        var buttonYes = new Button
        {
            Text = "&Yes, read it",
            DialogResult = DialogResult.Yes,
            Location = new Point(16, 122),
            Size = new Size(110, 28),
        };
        buttonYes.Click += (_, _) => { Result = ReadmePromptResult.Yes; Close(); };
        Controls.Add(buttonYes);

        var buttonOpen = new Button
        {
            Text = "&Open in VS Code",
            Location = new Point(140, 122),
            Size = new Size(150, 28),
        };
        buttonOpen.Click += (_, _) => { Result = ReadmePromptResult.OpenInVSCode; Close(); };
        Controls.Add(buttonOpen);

        var buttonNo = new Button
        {
            Text = "&No",
            DialogResult = DialogResult.No,
            Location = new Point(334, 122),
            Size = new Size(110, 28),
        };
        buttonNo.Click += (_, _) => { Result = ReadmePromptResult.No; Close(); };
        Controls.Add(buttonNo);

        AcceptButton = buttonYes;
        CancelButton = buttonNo;
    }
}
