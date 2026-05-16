namespace GoPilot;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;

/// <summary>
/// Renders the bitmap "badges" shown alongside each Options menu item
/// and on the Options button itself.
///
/// Primary path: <see cref="LoadEmbeddedBadge"/> streams a Microsoft
/// Fluent UI Emoji PNG (MIT-licensed, embedded as a manifest resource)
/// and rescales it to the requested badge size with high-quality bicubic
/// interpolation. This is the canonical look -- full colour, vendor-
/// consistent across all Windows versions.
///
/// Fallback path: <see cref="CreateSquareBadge"/> draws a flat coloured
/// square with a monochrome glyph silhouette on top. Reached only if a
/// manifest resource lookup fails (e.g. the resource was renamed or
/// stripped during trimming).
/// </summary>
internal static class OptionIconRenderer
{
    // Manifest resource names; must match the <LogicalName> entries in
    // GoPilot.csproj for the embedded Fluent UI Emoji PNGs.
    public const string AutoApproveResource = "GoPilot.warning.png";
    public const string FleetResource       = "GoPilot.childrencrossing.png";
    public const string CavemanResource     = "GoPilot.bone.png";
    public const string ShowStepsResource   = "GoPilot.speechballoon.png";

    // Colours used only when the embedded PNG cannot be loaded.
    public static readonly Color AutoApproveSquare = Color.FromArgb(215,  75,  75);
    public static readonly Color FleetSquare       = Color.FromArgb(100, 160, 220);
    public static readonly Color CavemanSquare     = Color.FromArgb(230, 140,  40);
    public static readonly Color ShowStepsSquare   = Color.FromArgb(100, 170, 100);

    // Monochrome silhouettes used by the colour-square fallback only.
    public const string AutoApproveGlyph = "\u26A0\uFE0F";
    public const string FleetGlyph       = "\U0001F6B8";
    public const string CavemanGlyph     = "\U0001F9B4";
    public const string ShowStepsGlyph   = "\U0001F4AC";

    private const string EmojiFontFamily = "Segoe UI Emoji";

    /// <summary>
    /// Loads the embedded Fluent UI Emoji PNG identified by
    /// <paramref name="resourceName"/> and rescales it to a square
    /// <paramref name="size"/>x<paramref name="size"/> bitmap. Returns
    /// null if the manifest resource cannot be located (callers should
    /// fall back to <see cref="CreateSquareBadge"/>).
    /// </summary>
    public static Bitmap? LoadEmbeddedBadge(string resourceName, int size = 18)
    {
        var asm = typeof(OptionIconRenderer).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) return null;

        using var source = new Bitmap(stream);

        var result = new Bitmap(size, size,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode  = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode      = SmoothingMode.HighQuality;
        g.PixelOffsetMode    = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.DrawImage(source, new Rectangle(0, 0, size, size));
        return result;
    }

    /// <summary>
    /// Fallback badge: a flat coloured square (no border) with a black
    /// monochrome silhouette of <paramref name="glyph"/> on top. Used
    /// only when the embedded Fluent UI Emoji PNG cannot be loaded.
    /// </summary>
    public static Bitmap CreateSquareBadge(
        Color fill,
        string glyph,
        int size = 18)
    {
        var bmp = new Bitmap(size, size,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using (var brush = new SolidBrush(fill))
            g.FillRectangle(brush, 0, 0, size, size);

        using var font = new Font(EmojiFontFamily, size * 0.78f,
            FontStyle.Regular, GraphicsUnit.Pixel);
        using var sf = new StringFormat
        {
            Alignment     = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        using var text = new SolidBrush(Color.Black);
        g.DrawString(glyph, font, text, new RectangleF(0, 0, size, size), sf);
        return bmp;
    }
}
