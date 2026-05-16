using System.Drawing;
using System.Windows.Forms;

namespace GoPilot;

/// <summary>
/// ProfessionalColorTable that paints MenuStrip / ToolStripMenuItem surfaces
/// in the application's dark grey palette so the new top menu blends with the
/// rest of the form (which is owner-drawn dark via AppTheme).
/// </summary>
internal sealed class DarkMenuColorTable : ProfessionalColorTable
{
	public override Color MenuStripGradientBegin     => AppTheme.Surface;
	public override Color MenuStripGradientEnd       => AppTheme.Surface;
	public override Color ToolStripDropDownBackground => Color.FromArgb(56, 56, 56);
	public override Color ImageMarginGradientBegin   => Color.FromArgb(56, 56, 56);
	public override Color ImageMarginGradientMiddle  => Color.FromArgb(56, 56, 56);
	public override Color ImageMarginGradientEnd     => Color.FromArgb(56, 56, 56);
	public override Color MenuItemSelected           => Color.FromArgb(96, 96, 96);
	public override Color MenuItemSelectedGradientBegin => Color.FromArgb(96, 96, 96);
	public override Color MenuItemSelectedGradientEnd   => Color.FromArgb(96, 96, 96);
	public override Color MenuItemPressedGradientBegin  => Color.FromArgb(96, 96, 96);
	public override Color MenuItemPressedGradientMiddle => Color.FromArgb(96, 96, 96);
	public override Color MenuItemPressedGradientEnd    => Color.FromArgb(96, 96, 96);

	// Background painted behind the check glyph on Checked ToolStripMenuItems.
	// The default ProfessionalColorTable returns the bright system accent color,
	// which clashes with the dark menu. Use a muted grey one notch brighter than
	// the dropdown background (56) but well below the hover highlight (96).
	public override Color CheckBackground          => Color.FromArgb(76, 76, 76);
	public override Color CheckSelectedBackground  => Color.FromArgb(76, 76, 76);
	public override Color CheckPressedBackground   => Color.FromArgb(76, 76, 76);

	public override Color MenuItemBorder             => AppTheme.ButtonBorder;
	public override Color MenuBorder                 => AppTheme.ButtonBorder;
	public override Color SeparatorDark              => AppTheme.ButtonBorder;
	public override Color SeparatorLight             => AppTheme.ButtonBorder;
}

/// <summary>
/// Professional renderer wired to the dark color table; also forces selected
/// item text to the bright accent colour so highlighted items stay legible.
/// </summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
	private static readonly Color SelectedFill = Color.FromArgb(96, 96, 96);

	public DarkMenuRenderer() : base(new DarkMenuColorTable()) { }

	// The color table alone is not always honoured (Windows accent colors and
	// visual styles can override the gradient on top-level items). Paint the
	// backgrounds explicitly so the menu always matches the dark theme.

	protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
	{
		// Use StatusBar colour for the StatusStrip; Surface for menus.
		var color = e.ToolStrip is StatusStrip ? AppTheme.StatusBar : AppTheme.Surface;
		using var bg = new SolidBrush(color);
		e.Graphics.FillRectangle(bg, e.AffectedBounds);
	}

	protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
	{
		using var bg = new SolidBrush(Color.FromArgb(56, 56, 56));
		e.Graphics.FillRectangle(bg, e.AffectedBounds);
	}

	protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
	{
		var item    = e.Item;
		var isOpen  = item is ToolStripMenuItem mi && mi.DropDown.Visible;
		var isHot   = item.Selected || isOpen || item.Pressed;
		var onStrip = item.Owner is MenuStrip;

		Color fill = isHot
			? SelectedFill
			: (onStrip ? AppTheme.Surface : Color.FromArgb(56, 56, 56));

		var rect = new Rectangle(Point.Empty, item.Size);
		using (var brush = new SolidBrush(fill))
			e.Graphics.FillRectangle(brush, rect);

		if (isHot)
		{
			using var pen = new Pen(AppTheme.ButtonBorder);
			e.Graphics.DrawRectangle(pen, 0, 0, item.Size.Width - 1, item.Size.Height - 1);
		}
	}

	protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
	{
		if (e.Item.Selected || (e.Item is ToolStripMenuItem mi && mi.DropDown.Visible))
			e.TextColor = AppTheme.AccentText;
		else if (e.Item.Enabled)
			e.TextColor = AppTheme.TextPrimary;
		else
			e.TextColor = AppTheme.TextMuted;
		base.OnRenderItemText(e);
	}

	protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
	{
		e.ArrowColor = AppTheme.TextPrimary;
		base.OnRenderArrow(e);
	}
}
