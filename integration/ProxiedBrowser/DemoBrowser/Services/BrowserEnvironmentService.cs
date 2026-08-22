using System.IO;
using DemoBrowser.Models;
using Microsoft.Web.WebView2.Core;

namespace DemoBrowser.Services;

/// <summary>
/// Owns the single <see cref="CoreWebView2Environment"/> shared by every tab.
///
/// WHY one environment: all tabs share the same UserDataFolder, and therefore the same cookies,
/// session storage and cache, across tabs and across restarts.
///
/// WHY the proxy is an environment-time argument: Chromium reads <c>--proxy-server</c> only from the
/// command line of the browser process, which WebView2 launches when the environment is created.
/// There is no API to change the proxy on a live environment, so proxy settings changed in the
/// settings dialog only take effect after restarting the application.
/// </summary>
public sealed class BrowserEnvironmentService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<CoreWebView2Environment>? _creation;

    public CoreWebView2Environment? Environment { get; private set; }

    /// <summary>Returns the installed Evergreen runtime version, or <c>null</c> if no runtime is available.</summary>
    public static string? GetInstalledRuntimeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the Chromium switches for the proxy. Values are intentionally NOT quoted: the string is
    /// split on whitespace and literal quotes would be passed through to Chromium verbatim.
    /// </summary>
    public static string BuildBrowserArguments(AppSettings settings)
    {
        var scheme = string.Equals(settings.ProxyScheme, "https", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
        var args = $"--proxy-server={scheme}://{settings.ProxyHost.Trim()}:{settings.ProxyPort}";
        if (!string.IsNullOrWhiteSpace(settings.ProxyBypassList))
        {
            args += $" --proxy-bypass-list={settings.ProxyBypassList.Trim()}";
        }

        return args;
    }

    /// <summary>Creates the environment exactly once; concurrent and repeated callers share the same cached task.</summary>
    public async Task<CoreWebView2Environment> GetOrCreateAsync(AppSettings settings)
    {
        if (_creation is null)
        {
            await _gate.WaitAsync().ConfigureAwait(true);
            try
            {
                _creation ??= CreateAsync(settings);
            }
            finally
            {
                _gate.Release();
            }
        }

        return await _creation.ConfigureAwait(true);
    }

    private async Task<CoreWebView2Environment> CreateAsync(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.UserDataFolder);

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = BuildBrowserArguments(settings),
        };

        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: AppPaths.UserDataFolder,
            options: options).ConfigureAwait(true);

        Environment = environment;
        return environment;
    }
}
