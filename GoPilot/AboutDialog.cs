using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

namespace GoPilot;

/// <summary>
/// Minimal dark-themed About dialog displaying app name, version, and a short
/// blurb. Triggered from Help -> About in the main menu.
/// </summary>
internal sealed class AboutDialog : Form
{
	public AboutDialog()
	{
		AutoScaleDimensions = new SizeF(7F, 15F);
		AutoScaleMode       = AutoScaleMode.Font;

		Text            = "About GoPilot";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		StartPosition   = FormStartPosition.CenterParent;
		MaximizeBox     = false;
		MinimizeBox     = false;
		ShowInTaskbar   = false;
		ClientSize      = new Size(420, 230);
		BackColor       = AppTheme.Background;
		ForeColor       = AppTheme.TextPrimary;
		Font            = new Font("Segoe UI", 9F);

		try
		{
			using var stream = typeof(AboutDialog).Assembly.GetManifestResourceStream("GoPilot.icon.ico");
			if (stream != null)
				Icon = new Icon(stream);
		}
		catch { /* icon is cosmetic */ }

		var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "(unknown)";

		var labelTitle = new Label
		{
			Text      = "GoPilot",
			Font      = new Font("Segoe UI", 18F, FontStyle.Bold),
			ForeColor = AppTheme.TextPrimary,
			AutoSize  = true,
			Location  = new Point(20, 18),
		};

		var labelVersion = new Label
		{
			Text      = $"Version {version}",
			ForeColor = AppTheme.TextMuted,
			AutoSize  = true,
			Location  = new Point(22, 60),
		};

		var labelBlurb = new Label
		{
			Text      = "A Windows desktop GUI for the GitHub Copilot SDK.\r\n" +
			            "Wraps the Copilot CLI in a friendly WinForms interface\r\n" +
			            "for editing prompts, approving tools, and managing sessions.",
			ForeColor = AppTheme.TextPrimary,
			AutoSize  = false,
			Size      = new Size(380, 70),
			Location  = new Point(22, 90),
		};

		var linkRepo = new LinkLabel
		{
			Text         = "https://github.com/gsnook/gopilot",
			LinkColor    = Color.FromArgb(120, 180, 230),
			ActiveLinkColor = Color.FromArgb(160, 210, 255),
			VisitedLinkColor = Color.FromArgb(120, 180, 230),
			AutoSize     = true,
			Location     = new Point(22, 162),
		};
		linkRepo.LinkClicked += (_, _) =>
		{
			try { Process.Start(new ProcessStartInfo(linkRepo.Text) { UseShellExecute = true }); }
			catch { /* ignore launch failure */ }
		};

		var buttonOk = new Button
		{
			Text         = "OK",
			DialogResult = DialogResult.OK,
			BackColor    = AppTheme.ButtonBg,
			ForeColor    = AppTheme.TextPrimary,
			FlatStyle    = FlatStyle.Flat,
			Size         = new Size(80, 28),
			Location     = new Point(ClientSize.Width - 100, ClientSize.Height - 40),
		};
		buttonOk.FlatAppearance.BorderColor = AppTheme.ButtonBorder;

		Controls.Add(labelTitle);
		Controls.Add(labelVersion);
		Controls.Add(labelBlurb);
		Controls.Add(linkRepo);
		Controls.Add(buttonOk);

		AcceptButton = buttonOk;
		CancelButton = buttonOk;
	}
}
