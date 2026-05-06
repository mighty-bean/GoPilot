namespace GoPilot;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();

        string? startupFolder = ParseStartupFolder(args);
        Application.Run(new MainForm(startupFolder));
    }

    /// <summary>
    /// Parses the optional workspace folder argument from the command line.
    /// Accepts the first non-switch positional argument and resolves it to a
    /// full path. Returns null when no folder was supplied or the path does
    /// not refer to an existing directory.
    /// </summary>
    private static string? ParseStartupFolder(string[] args)
    {
        if (args == null || args.Length == 0) return null;

        foreach (var raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (raw.StartsWith("-") || raw.StartsWith("/")) continue;

            try
            {
                var full = System.IO.Path.GetFullPath(raw);
                if (System.IO.Directory.Exists(full))
                    return full;
            }
            catch
            {
                // Ignore malformed paths and fall through.
            }
        }

        return null;
    }
}