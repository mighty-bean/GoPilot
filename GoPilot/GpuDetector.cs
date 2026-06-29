using System;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace GoPilot;

/// <summary>
/// Best-effort detection of dedicated GPU memory so the local-LLM filter can
/// pick a codellama model that fits the card. Detection order:
///   1. nvidia-smi total memory (most accurate for NVIDIA cards).
///   2. Windows registry qwMemorySize for each display adapter.
/// If nothing can be determined the conservative 7B tier is used.
/// </summary>
internal static class GpuDetector
{
	/// <summary>
	/// Detected dedicated VRAM in gigabytes, or 0 when it could not be read.
	/// </summary>
	public static double DetectVramGb()
	{
		var nv = QueryNvidiaSmiGb();
		if (nv > 0) return nv;

		var reg = QueryRegistryGb();
		if (reg > 0) return reg;

		return 0;
	}

	/// <summary>
	/// Maps detected VRAM to a codellama model that fits comfortably alongside
	/// the OS and other apps. Quantized footprints: 7B ~5GB, 13B ~9GB, 34B ~20GB.
	/// Unknown VRAM (0) falls back to the smallest, safest 7B tier.
	/// </summary>
	public static string RecommendModel(double vramGb)
	{
		if (vramGb >= 22) return "codellama:34b-instruct";
		if (vramGb >= 12) return "codellama:13b-instruct";
		return "codellama:7b-instruct";
	}

	private static double QueryNvidiaSmiGb()
	{
		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = "nvidia-smi",
				Arguments = "--query-gpu=memory.total --format=csv,noheader,nounits",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};
			using var p = Process.Start(psi);
			if (p == null) return 0;
			var outp = p.StandardOutput.ReadToEnd();
			p.WaitForExit(4000);

			double maxMb = 0;
			foreach (var line in outp.Split('\n'))
			{
				var t = line.Trim();
				if (t.Length == 0) continue;
				if (double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out var mb))
					maxMb = Math.Max(maxMb, mb);
			}
			return maxMb > 0 ? maxMb / 1024.0 : 0;
		}
		catch { return 0; }
	}

	private static double QueryRegistryGb()
	{
		try
		{
			const string baseKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
			using var cls = Registry.LocalMachine.OpenSubKey(baseKey);
			if (cls == null) return 0;

			long maxBytes = 0;
			foreach (var sub in cls.GetSubKeyNames())
			{
				if (!int.TryParse(sub, out _)) continue;
				using var k = cls.OpenSubKey(sub);
				var v = k?.GetValue("HardwareInformation.qwMemorySize");
				if (v is long bytes && bytes > maxBytes) maxBytes = bytes;
			}
			return maxBytes > 0 ? maxBytes / 1_073_741_824.0 : 0;
		}
		catch { return 0; }
	}
}
