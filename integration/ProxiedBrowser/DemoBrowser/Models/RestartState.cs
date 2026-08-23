using System.Globalization;

namespace DemoBrowser.Models;

/// <summary>
/// What one instance hands to its successor when the browser engine has to be re-initialised in flight
/// (proxy or CA settings changed, the proxy's CA was re-issued). Travels on the command line of the new process
/// only — it is never written to disk, so nothing about open tabs survives a normal exit.
///
/// WHY a new process at all: Chromium reads the proxy and the SPKI pins from the browser process command line,
/// and CEF can initialise the runtime exactly once per process. The only way to "reload the browser control with
/// new settings" is therefore to start a fresh process and let it open the same pages again.
/// </summary>
public sealed class RestartState
{
    private const string PreviousPidSwitch = "--restart-of";
    private const string TabSwitch = "--tab";
    private const string ActiveSwitch = "--active";
    private const string WindowSwitch = "--window";

    /// <summary>PID of the instance being replaced; the successor waits for it to release the profile.</summary>
    public int PreviousProcessId { get; init; }

    public IReadOnlyList<string> TabUrls { get; init; } = [];

    public int ActiveTabIndex { get; init; }

    /// <summary>Window geometry in screen pixels, or <c>null</c> to use the defaults.</summary>
    public int? X { get; init; }

    public int? Y { get; init; }

    public double? Width { get; init; }

    public double? Height { get; init; }

    public bool Maximized { get; init; }

    public bool HasGeometry => X is not null && Y is not null && Width is not null && Height is not null;

    /// <summary>Serialises to process arguments (each value its own argument, so URLs need no quoting rules).</summary>
    public IReadOnlyList<string> ToArguments()
    {
        var args = new List<string> { PreviousPidSwitch, PreviousProcessId.ToString(CultureInfo.InvariantCulture) };
        foreach (var url in TabUrls)
        {
            args.Add(TabSwitch);
            args.Add(url);
        }

        args.Add(ActiveSwitch);
        args.Add(ActiveTabIndex.ToString(CultureInfo.InvariantCulture));

        if (HasGeometry)
        {
            args.Add(WindowSwitch);
            args.Add(string.Join(',',
                X!.Value.ToString(CultureInfo.InvariantCulture),
                Y!.Value.ToString(CultureInfo.InvariantCulture),
                Width!.Value.ToString(CultureInfo.InvariantCulture),
                Height!.Value.ToString(CultureInfo.InvariantCulture),
                Maximized ? "max" : "normal"));
        }

        return args;
    }

    /// <summary>Returns the state encoded in <paramref name="args"/>, or <c>null</c> for a normal launch.</summary>
    public static RestartState? TryParse(string[]? args)
    {
        if (args is null || Array.IndexOf(args, PreviousPidSwitch) < 0)
        {
            return null;
        }

        var pid = 0;
        var tabs = new List<string>();
        var active = 0;
        int? x = null, y = null;
        double? width = null, height = null;
        var maximized = false;

        for (var i = 0; i < args.Length - 1; i++)
        {
            var value = args[i + 1];
            switch (args[i])
            {
                case PreviousPidSwitch:
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
                    i++;
                    break;
                case TabSwitch:
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        tabs.Add(value);
                    }

                    i++;
                    break;
                case ActiveSwitch:
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out active);
                    i++;
                    break;
                case WindowSwitch:
                    var parts = value.Split(',');
                    if (parts.Length == 5
                        && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var px)
                        && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var py)
                        && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pw)
                        && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var ph))
                    {
                        x = px;
                        y = py;
                        width = pw;
                        height = ph;
                        maximized = parts[4] == "max";
                    }

                    i++;
                    break;
            }
        }

        return new RestartState
        {
            PreviousProcessId = pid,
            TabUrls = tabs,
            ActiveTabIndex = Math.Clamp(active, 0, Math.Max(0, tabs.Count - 1)),
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Maximized = maximized,
        };
    }
}
