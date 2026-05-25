using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;

namespace GoPilot.SkillCatalog;

/// <summary>
/// Singleton-style factory that owns the shared <see cref="HttpClient"/> used
/// by every provider, configures it once with a <c>User-Agent</c> and a
/// 30-second default timeout, and dispatches <see cref="CatalogSource"/>
/// instances to the right <see cref="ICatalogProvider"/> implementation.
/// </summary>
internal static class CatalogProviderRegistry
{
	private static readonly Lazy<HttpClient> _http = new(() =>
	{
		var c = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(30),
		};
		var version = typeof(CatalogProviderRegistry).Assembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
			?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
			?? "0.0.0";
		c.DefaultRequestHeaders.UserAgent.ParseAdd($"GoPilot/{version} (+https://github.com/mighty-bean/GoPilot)");
		c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
		return c;
	});

	private static Dictionary<ProviderKind, ICatalogProvider>? _providers;

	/// <summary>Returns the shared HTTP client. Lazy-initialised on first access.</summary>
	public static HttpClient Http => _http.Value;

	/// <summary>
	/// Resolves the right provider for <paramref name="source"/>. Throws
	/// <see cref="CatalogProviderException"/> with kind <see
	/// cref="CatalogProviderErrorKind.UnsupportedProvider"/> when the source
	/// host isn't one we support.
	/// </summary>
	public static ICatalogProvider For(CatalogSource source)
	{
		_providers ??= BuildProviders();
		if (_providers.TryGetValue(source.Kind, out var p))
			return p;

		throw new CatalogProviderException(
			CatalogProviderErrorKind.UnsupportedProvider,
			$"Unsupported source host: {source.Url}");
	}

	private static Dictionary<ProviderKind, ICatalogProvider> BuildProviders()
	{
		var http = _http.Value;
		return new Dictionary<ProviderKind, ICatalogProvider>
		{
			{ ProviderKind.GitHub,      new GitHubCatalogProvider(http) },
			{ ProviderKind.AzureDevOps, new AzureDevOpsCatalogProvider(http) },
		};
	}
}
