namespace GoPilot;

/// <summary>
/// A TabControl subclass that paints the tab strip background and
/// content-area border in dark colours, eliminating the bright
/// edges that the default WinForms theme renderer leaves behind.
/// Individual tab headers are still painted by the DrawItem event.
/// </summary>
internal sealed class DarkTabControl : TabControl
{
	private const int WM_PAINT = 0x000F;

	protected override void WndProc(ref Message m)
	{
		base.WndProc(ref m);

		if (m.Msg == WM_PAINT)
		{
			using var g = CreateGraphics();
			PaintDarkOverlay(g);
		}
	}

	private void PaintDarkOverlay(Graphics g)
	{
		using var stripBrush  = new SolidBrush(AppTheme.Surface);
		using var borderBrush = new SolidBrush(AppTheme.OutputBox);

		var r = DisplayRectangle;

		// ── Tab header area ──────────────────────────────────────────
		// Paint over the entire tab strip region (from y=0 to the bottom
		// of the tab headers) then re-draw each tab header from scratch.
		// This completely replaces the native themed rendering so no bright
		// borders can leak through — the DrawItem event's Graphics is
		// system-clipped and cannot reach the outermost border pixels.
		if (TabCount > 0)
		{
			var firstTab = GetTabRect(0);
			int stripBottom = firstTab.Bottom + 2;

			// Wipe the entire strip region
			g.FillRectangle(stripBrush, 0, 0, Width, stripBottom);

			// Cover the seam between the strip and the content area
			g.FillRectangle(borderBrush, 0, stripBottom, Width, r.Y - stripBottom);

			// Re-draw individual tab headers
			using var selectedBrush  = new SolidBrush(AppTheme.OutputBox);
			using var normalBrush    = new SolidBrush(AppTheme.Surface);
			using var activeFg       = new SolidBrush(AppTheme.TextPrimary);
			using var inactiveFg     = new SolidBrush(AppTheme.TextMuted);
			var sf = new StringFormat
			{
				Alignment     = StringAlignment.Center,
				LineAlignment = StringAlignment.Center,
			};

			for (int i = 0; i < TabCount; i++)
			{
				var tab       = GetTabRect(i);
				bool selected = SelectedIndex == i;
				g.FillRectangle(selected ? selectedBrush : normalBrush, tab);
				g.DrawString(TabPages[i].Text, Font,
					selected ? activeFg : inactiveFg, tab, sf);
			}
		}

		// ── Content-area border ──────────────────────────────────────
		// Overpaint the bright 3D border around the tab content area.
		// Derive the band thickness from the actual DisplayRectangle inset
		// so we cover the full native border on any Windows version/DPI.
		int bx = r.X;          // horizontal border thickness
		int by = r.Y - (TabCount > 0 ? GetTabRect(0).Bottom : 0);  // vertical (below tab strip)
		if (bx < 1) bx = 4;
		if (by < 1) by = 4;
		// top band (under the tab strip)
		g.FillRectangle(borderBrush, 0, r.Y - by, Width, by);
		// bottom band
		g.FillRectangle(borderBrush, 0, r.Bottom, Width, Height - r.Bottom);
		// left band
		g.FillRectangle(borderBrush, 0, r.Y - by, bx, r.Height + by + (Height - r.Bottom));
		// right band
		g.FillRectangle(borderBrush, r.Right, r.Y - by, Width - r.Right, r.Height + by + (Height - r.Bottom));
	}
}
