namespace GoPilot;

public partial class PermissionDialog : Form
{
    private readonly PermissionEventArgs _args;

    public PermissionDialog(PermissionEventArgs args)
    {
        _args = args;
        InitializeComponent();
        PopulateDetails();
    }

    private void PopulateDetails()
    {
        labelKind.Text = $"Operation: {DescribeKind(_args.OperationKind)}";

        var details = _args.OperationKind switch
        {
            "shell" => $"Command: {_args.CommandText ?? "(unknown)"}",
            "write" => $"File: {_args.FileName ?? "(unknown)"}",
            "read"  => $"File: {_args.FileName ?? "(unknown)"}",
            "mcp" or "custom_tool" => $"Tool: {_args.ToolName ?? "(unknown)"}",
            _ => _args.ToolName ?? _args.FileName ?? _args.CommandText ?? "",
        };
        labelDetails.Text = details;
    }

    private static string DescribeKind(string kind) => kind switch
    {
        "shell"       => "🖥️  Execute shell command",
        "write"       => "✏️  Write / edit file",
        "read"        => "📖  Read file",
        "mcp"         => "🔌  Call MCP tool",
        "custom_tool" => "🔧  Call custom tool",
        "url"         => "🌐  Fetch URL",
        "memory"      => "🧠  Access memory",
        "hook"        => "🪝  Invoke hook",
        _             => kind,
    };

    public bool ApproveSimilar { get; private set; }

    private void ButtonApproveSimilar_Click(object? sender, EventArgs e)
    {
        ApproveSimilar = true;
        _args.ApproveSimilar = true;
        _args.Decision.TrySetResult(true);
        DialogResult = DialogResult.Yes;
        Close();
    }

    private void ButtonAllow_Click(object? sender, EventArgs e)
    {
        _args.Decision.TrySetResult(true);
        DialogResult = DialogResult.Yes;
        Close();
    }

    private void ButtonDeny_Click(object? sender, EventArgs e)
    {
        _args.Decision.TrySetResult(false);
        DialogResult = DialogResult.No;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _args.Decision.TrySetResult(false);
        base.OnFormClosing(e);
    }
}
