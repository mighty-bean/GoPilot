namespace GoPilot;

/// <summary>Central colour palette for the neutral mid-grey UI theme.</summary>
internal static class AppTheme
{
    // ── Background layers (darkest → lightest) ─────────────────────────────
    public static readonly Color OutputBox  = Color.FromArgb(0,   0,   0);
    public static readonly Color InputBox   = Color.FromArgb(52,  52,  52);
    public static readonly Color Background = Color.FromArgb(64,  64,  64);
    public static readonly Color Surface    = Color.FromArgb(74,  74,  74);
    public static readonly Color StatusBar  = Color.FromArgb(56,  56,  56);

    // ── Text ───────────────────────────────────────────────────────────────
    public static readonly Color TextPrimary = Color.FromArgb(218, 218, 218);
    public static readonly Color TextMuted   = Color.FromArgb(148, 148, 148);

    // ── Controls ───────────────────────────────────────────────────────────
    public static readonly Color ButtonBg     = Color.FromArgb(86,  86,  86);
    public static readonly Color ButtonBorder = Color.FromArgb(108, 108, 108);
    public static readonly Color AccentBg     = Color.FromArgb(60,  112, 160);
    public static readonly Color AccentText   = Color.FromArgb(235, 235, 235);

    // ── Semantic colours used in the session output box ────────────────────
    public static readonly Color ColorSubAgent  = Color.FromArgb(130, 180, 210);  // agent headers
    public static readonly Color ColorToolDim   = Color.FromArgb(160, 140,  65);  // args / result summaries
    public static readonly Color ColorUser      = Color.FromArgb(100, 160, 220);
    public static readonly Color ColorAssistant = Color.FromArgb(100, 190, 100);
    public static readonly Color ColorTool      = Color.FromArgb(205, 180,  90);
    public static readonly Color ColorReasoning = Color.FromArgb(148, 148, 210);
    public static readonly Color ColorError     = Color.FromArgb(215,  75,  75);
    public static readonly Color ColorMeta      = Color.FromArgb(140, 140, 140);
    public static readonly Color ColorDefault   = Color.FromArgb(210, 210, 210);
}
