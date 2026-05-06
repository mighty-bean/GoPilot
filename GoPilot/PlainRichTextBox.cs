using System.Runtime.InteropServices;
using System.Text;

namespace GoPilot;

/// <summary>
/// A RichTextBox that enforces a single fixed color scheme
/// and always pastes as plain text, stripping all formatting.
///
/// Two-layer defence:
///   1. WM_PASTE + EM_PASTESPECIAL are intercepted so clipboard content is
///      always inserted as plain text with the control's own font/color.
///   2. OnTextChanged normalizes every character's formatting after any edit,
///      catching any pathway that bypasses the paste intercept.
///
/// Drag-and-drop of FILES is accepted and surfaced via FilesDropped; all
/// other drag sources (text, RTF, etc.) are rejected so formatting can't
/// be introduced via drag. Recognised drag formats include the standard
/// shell CF_HDROP (File Explorer, browsers, most apps), the single-file
/// CF_FILENAMEW shell format, and the Visual Studio Solution Explorer
/// formats CF_VSREFPROJECTITEMS (parsed for absolute paths) and
/// CF_VSSTGPROJECTITEMS (accepted; paths come from the ref format that
/// VS publishes alongside it). When a drop arrives in an unrecognised
/// format, UnrecognizedFormatDropped fires with the list of formats that
/// were present so the host can surface a diagnostic.
/// </summary>
internal sealed class PlainRichTextBox : RichTextBox
{
    private const int WM_PASTE       = 0x0302;
    private const int EM_PASTESPECIAL = 0x0440;
    private const int WM_SETREDRAW   = 0x000B;

    private const string CfFileNameW         = "FileNameW";
    private const string CfVsRefProjectItems = "CF_VSREFPROJECTITEMS";
    private const string CfVsStgProjectItems = "CF_VSSTGPROJECTITEMS";

    private static readonly string[] AcceptedFormats =
    {
        DataFormats.FileDrop,
        CfFileNameW,
        CfVsRefProjectItems,
        CfVsStgProjectItems,
    };

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    private bool _normalizing;

    /// <summary>Raised when one or more files (or folders) are dropped onto the editor.</summary>
    public event EventHandler<IReadOnlyList<string>>? FilesDropped;

    /// <summary>
    /// Raised when a drop arrives in a format the editor accepted but could not
    /// parse into file paths. The argument lists every format that was present
    /// on the drag IDataObject, useful for diagnosing new drag sources.
    /// </summary>
    public event EventHandler<IReadOnlyList<string>>? UnrecognizedFormatDropped;

    public PlainRichTextBox()
    {
        BackColor = AppTheme.InputBox;
        ForeColor = AppTheme.TextPrimary;
        AllowDrop = true;
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        e.Effect = ContainsAcceptedFormat(e.Data)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        // Do NOT call base -- prevents RichTextBox accepting text/RTF drags
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        // Some sources (notably VS Solution Explorer) re-query the drop effect
        // continuously and revert to None unless DragOver re-affirms Copy.
        e.Effect = ContainsAcceptedFormat(e.Data)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        // Do NOT call base -- prevents RichTextBox accepting text/RTF drags
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        if (e.Data == null) return;

        var paths = ExtractPaths(e.Data);
        if (paths.Count > 0)
        {
            FilesDropped?.Invoke(this, paths);
            return;
        }

        // We accepted the drop on the strength of a recognised format, but no
        // paths could be extracted. Surface every format that was on the
        // IDataObject so the host can show a diagnostic and we can iterate.
        UnrecognizedFormatDropped?.Invoke(this, SafeGetFormats(e.Data));
        // Do NOT call base -- prevents any text insertion from the drag source
    }

    private static bool ContainsAcceptedFormat(IDataObject? data)
    {
        if (data == null) return false;
        foreach (string fmt in AcceptedFormats)
        {
            if (data.GetDataPresent(fmt, false)) return true;
        }
        return false;
    }

    private static IReadOnlyList<string> ExtractPaths(IDataObject data)
    {
        // 1. CF_HDROP -- File Explorer, browsers, and most apps.
        if (data.GetDataPresent(DataFormats.FileDrop, false))
        {
            try
            {
                if (data.GetData(DataFormats.FileDrop, false) is string[] dropPaths && dropPaths.Length > 0)
                    return dropPaths;
            }
            catch
            {
                // Fall through to other formats.
            }
        }

        // 2. CF_VSREFPROJECTITEMS -- Visual Studio Solution Explorer.
        var vsPaths = TryReadVsRefProjectItems(data);
        if (vsPaths.Count > 0) return vsPaths;

        // 3. CF_FILENAMEW -- single Unicode file path (shell IDataObjects).
        string? singleFile = TryReadSingleFileNameW(data);
        if (!string.IsNullOrEmpty(singleFile))
            return new[] { singleFile };

        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> TryReadVsRefProjectItems(IDataObject data)
    {
        if (!data.GetDataPresent(CfVsRefProjectItems, false)) return Array.Empty<string>();

        byte[] bytes;
        try
        {
            object? raw = data.GetData(CfVsRefProjectItems, false);
            bytes = raw switch
            {
                MemoryStream ms => ms.ToArray(),
                byte[] b        => b,
                _               => Array.Empty<byte>(),
            };
        }
        catch
        {
            return Array.Empty<string>();
        }

        if (bytes.Length == 0) return Array.Empty<string>();

        // VS publishes this format as Unicode-encoded text. Each item is
        // "{ProjectGuid}|{ProjectFile}|{ItemPath}" terminated by a NUL. The
        // third pipe-delimited field is the path we want; if it is relative
        // it resolves against the project file in the second field.
        string text = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var paths = new List<string>();
        foreach (string entry in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = entry.Split('|');
            if (parts.Length < 3) continue;

            string itemPath    = parts[2].Trim();
            string projectFile = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(itemPath)) continue;

            if (!Path.IsPathRooted(itemPath) && !string.IsNullOrEmpty(projectFile))
            {
                string? projectDir = Path.GetDirectoryName(projectFile);
                if (!string.IsNullOrEmpty(projectDir))
                {
                    try { itemPath = Path.GetFullPath(Path.Combine(projectDir, itemPath)); }
                    catch { /* keep itemPath as-is */ }
                }
            }

            // Filter to entries that actually resolve on disk; the format can
            // include solution folders and other virtual items with no path.
            if (File.Exists(itemPath) || Directory.Exists(itemPath))
                paths.Add(itemPath);
        }
        return paths;
    }

    private static string? TryReadSingleFileNameW(IDataObject data)
    {
        if (!data.GetDataPresent(CfFileNameW, false)) return null;

        try
        {
            object? raw = data.GetData(CfFileNameW, false);
            string? path = raw switch
            {
                string s        => s,
                MemoryStream ms => DecodeNullTerminatedUnicode(ms.ToArray()),
                byte[] b        => DecodeNullTerminatedUnicode(b),
                _               => null,
            };
            return string.IsNullOrWhiteSpace(path) ? null : path!.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? DecodeNullTerminatedUnicode(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        string s = Encoding.Unicode.GetString(bytes);
        int nul = s.IndexOf('\0');
        if (nul >= 0) s = s.Substring(0, nul);
        return s;
    }

    private static string[] SafeGetFormats(IDataObject data)
    {
        try { return data.GetFormats(false) ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_PASTE || m.Msg == EM_PASTESPECIAL)
        {
            // Images on the clipboard are allowed through to the native RichEdit
            // handler so they embed as a visible preview. MainForm extracts them
            // from the RTF at send-time and forwards them as blob attachments.
            // Text-only pastes still route through our plain-text path so
            // formatting (fonts, colors, styles) is stripped as before.
            if (ClipboardHasImage())
            {
                base.WndProc(ref m);
                return;
            }
            PastePlainText();
            return;
        }
        base.WndProc(ref m);
    }

    private void PastePlainText()
    {
        string text =
            Clipboard.ContainsText(TextDataFormat.UnicodeText) ? Clipboard.GetText(TextDataFormat.UnicodeText) :
            Clipboard.ContainsText(TextDataFormat.Text)        ? Clipboard.GetText(TextDataFormat.Text) :
            Clipboard.ContainsText()                           ? Clipboard.GetText() :
            string.Empty;

        if (string.IsNullOrEmpty(text)) return;

        SelectionFont       = Font;
        SelectionColor      = ForeColor;
        SelectionBackColor  = BackColor;
        SelectedText        = text;
    }

    /// <summary>
    /// Returns true when the clipboard contains picture data in any of the
    /// formats commonly produced by screen capture tools, browsers, and Office
    /// apps (CF_BITMAP, CF_DIB, DIBv5, and the "PNG" synthetic format).
    /// </summary>
    private static bool ClipboardHasImage()
    {
        try
        {
            if (Clipboard.ContainsImage())              return true;
            if (Clipboard.ContainsData("PNG"))          return true;
            if (Clipboard.ContainsData(DataFormats.Dib))return true;
            if (Clipboard.ContainsData(DataFormats.Bitmap)) return true;
            if (Clipboard.ContainsData("DeviceIndependentBitmap")) return true;
        }
        catch
        {
            // Clipboard access can fail transiently; treat as "no image".
        }
        return false;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        NormalizeFormatting();
    }

    /// <summary>
    /// Walks every character and resets its formatting to the control's
    /// own font and colors, preventing any styling from persisting.
    /// Uses WM_SETREDRAW to suppress flicker during the operation.
    /// </summary>
    private void NormalizeFormatting()
    {
        if (_normalizing || TextLength == 0) return;
        _normalizing = true;
        try
        {
            int savedStart  = SelectionStart;
            int savedLength = SelectionLength;

            SendMessage(Handle, WM_SETREDRAW, 0, 0);
            try
            {
                SelectAll();
                SelectionFont      = Font;
                SelectionColor     = ForeColor;
                SelectionBackColor = BackColor;
            }
            finally
            {
                SendMessage(Handle, WM_SETREDRAW, 1, 0);
                Invalidate();
            }

            Select(savedStart, savedLength);
        }
        finally
        {
            _normalizing = false;
        }
    }
}
