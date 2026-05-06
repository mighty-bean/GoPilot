namespace GoPilot;

/// <summary>
/// The type of content in an output block, used by the Rendered tab
/// to determine styling and rendering strategy.
/// </summary>
internal enum BlockKind
{
	User,
	Assistant,
	Reasoning,
	Tool,
	SubAgent,
	Error,
	Status,
}

/// <summary>
/// A structured output block that feeds the WebView2 Rendered tab.
/// Each block represents a discrete piece of output (a user prompt,
/// an assistant response, a tool invocation, etc.).
/// </summary>
internal sealed class OutputBlock
{
	private static int _nextId;

	public OutputBlock(BlockKind kind)
	{
		Id = $"blk-{_nextId++}";
		Kind = kind;
	}

	/// <summary>Unique identifier for this block (used as the DOM element ID).</summary>
	public string Id { get; }

	/// <summary>The kind of content this block represents.</summary>
	public BlockKind Kind { get; }

	/// <summary>Display label shown above the content (e.g. "Assistant:", "Tool: edit_file").</summary>
	public string Label { get; set; } = "";

	/// <summary>Accumulated content text. For Assistant blocks this is Markdown; for others it is plain text.</summary>
	public string Content { get; set; } = "";

	/// <summary>Whether this block's content stream is complete.</summary>
	public bool IsComplete { get; set; }

	/// <summary>Whether the content should be rendered as Markdown (true for Assistant blocks).</summary>
	public bool IsMarkdown => Kind == BlockKind.Assistant;

	/// <summary>The CSS class suffix used in the Rendered tab (e.g. "assistant", "tool").</summary>
	public string CssKind => Kind switch
	{
		BlockKind.User      => "user",
		BlockKind.Assistant => "assistant",
		BlockKind.Reasoning => "reasoning",
		BlockKind.Tool      => "tool",
		BlockKind.SubAgent  => "subagent",
		BlockKind.Error     => "error",
		BlockKind.Status    => "status",
		_                   => "status",
	};
}
