namespace GoPilot;

/// <summary>
/// Tracks prompts sent during the session and supports navigation through
/// history entries — similar to terminal shell history.
/// </summary>
internal sealed class PromptHistory
{
    private readonly List<string> _items = [];
    private int _cursor = -1;           // -1 = at draft (not navigating)
    private string _draft = string.Empty;

    /// <summary>Gets whether there is a previous (older) history item to navigate to.</summary>
    public bool CanGoBack => _items.Count > 0 && (_cursor == -1 || _cursor > 0);

    /// <summary>Gets whether there is a next (newer) item or unsent draft to navigate to.</summary>
    public bool CanGoForward => _cursor != -1;

    /// <summary>Adds a sent prompt to the history and resets the navigation cursor to the draft position.</summary>
    public void Add(string prompt)
    {
        _items.Add(prompt);
        _cursor = -1;
        _draft = string.Empty;
    }

    /// <summary>
    /// Navigates to the previous (older) history entry.
    /// Saves <paramref name="currentDraft"/> when first entering history navigation.
    /// Returns the text that should be loaded into the prompt box.
    /// </summary>
    public string NavigateBack(string currentDraft)
    {
        if (_items.Count == 0) return currentDraft;

        if (_cursor == -1)
        {
            _draft = currentDraft;
            _cursor = _items.Count - 1;
        }
        else if (_cursor > 0)
        {
            _cursor--;
        }

        return _items[_cursor];
    }

    /// <summary>
    /// Navigates to the next (newer) history entry, or restores the unsent draft
    /// when stepping past the most recent item.
    /// Returns the text that should be loaded into the prompt box.
    /// </summary>
    public string NavigateForward()
    {
        if (_cursor == -1) return _draft;

        _cursor++;
        if (_cursor >= _items.Count)
        {
            _cursor = -1;
            return _draft;
        }

        return _items[_cursor];
    }
}
