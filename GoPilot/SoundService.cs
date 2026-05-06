namespace GoPilot;

using System.Runtime.InteropServices;

/// <summary>
/// Fire-and-forget playback of stock Windows WAV chimes from %SystemRoot%\Media.
/// All operations are best-effort: missing files, locked devices, or any other
/// failure are swallowed silently so audio cues never disrupt the UI.
/// </summary>
internal static class SoundService
{
	// File names live in %SystemRoot%\Media on every supported Windows build.
	private const string PromptSentFile   = "notify.wav";
	private const string WorkCompleteFile = "chimes.wav";
	private const string DialogFile       = "Windows Exclamation.wav";

	[DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = false)]
	private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

	private const uint SND_ASYNC     = 0x0001;
	private const uint SND_NODEFAULT = 0x0002;
	private const uint SND_FILENAME  = 0x00020000;
	private const uint SND_PURGE     = 0x0040;

	/// <summary>Plays notify.wav when the user submits a prompt.</summary>
	public static void PlayPromptSent() => TryPlay(PromptSentFile);

	/// <summary>Plays chimes.wav when the agent finishes all pending work.</summary>
	public static void PlayWorkComplete() => TryPlay(WorkCompleteFile);

	/// <summary>Plays Windows Exclamation.wav when a modal dialog is presented.</summary>
	public static void PlayDialog() => TryPlay(DialogFile);

	private static void TryPlay(string fileName)
	{
		try
		{
			var path = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.Windows),
				"Media",
				fileName);

			if (!File.Exists(path)) return;

			// SND_ASYNC returns immediately; SND_NODEFAULT suppresses the default
			// beep if the file cannot be opened. Any false return is ignored.
			PlaySound(path, IntPtr.Zero, SND_ASYNC | SND_FILENAME | SND_NODEFAULT);
		}
		catch
		{
			// Silent failure by design.
		}
	}
}
