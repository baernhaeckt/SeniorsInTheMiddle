using DemoBrowser.Models;
using Xunit;

namespace DemoBrowser.Tests;

/// <summary>
/// The in-flight restart hands the open tabs to the successor process on the command line. If that round trip
/// loses a URL or the geometry, the "reload" the user sees turns into a fresh start with a single tab.
/// </summary>
public class RestartStateTests
{
    [Fact]
    public void RoundTrips_tabs_active_index_and_geometry()
    {
        var state = new RestartState
        {
            PreviousProcessId = 4242,
            TabUrls = ["https://example.com/", "https://example.org/path?q=a b&x=1#frag"],
            ActiveTabIndex = 1,
            X = -8,
            Y = 31,
            Width = 1280.5,
            Height = 800,
            Maximized = true,
        };

        var parsed = RestartState.TryParse([.. state.ToArguments()]);

        Assert.NotNull(parsed);
        Assert.Equal(4242, parsed.PreviousProcessId);
        Assert.Equal(state.TabUrls, parsed.TabUrls);
        Assert.Equal(1, parsed.ActiveTabIndex);
        Assert.True(parsed.HasGeometry);
        Assert.Equal(-8, parsed.X);
        Assert.Equal(31, parsed.Y);
        Assert.Equal(1280.5, parsed.Width);
        Assert.Equal(800, parsed.Height);
        Assert.True(parsed.Maximized);
    }

    [Fact]
    public void Normal_launch_has_no_restart_state()
    {
        Assert.Null(RestartState.TryParse([]));
        Assert.Null(RestartState.TryParse(null));
        Assert.Null(RestartState.TryParse(["--tab", "https://example.com/"]));
    }

    [Fact]
    public void Active_index_is_clamped_and_geometry_is_optional()
    {
        var parsed = RestartState.TryParse(["--restart-of", "1", "--tab", "https://a/", "--active", "7"]);

        Assert.NotNull(parsed);
        Assert.Equal(0, parsed.ActiveTabIndex);
        Assert.False(parsed.HasGeometry);
        Assert.Single(parsed.TabUrls);
    }

    [Fact]
    public void Malformed_values_fall_back_instead_of_throwing()
    {
        var parsed = RestartState.TryParse(["--restart-of", "abc", "--active", "x", "--window", "1,2,3", "--tab", ""]);

        Assert.NotNull(parsed);
        Assert.Equal(0, parsed.PreviousProcessId);
        Assert.Empty(parsed.TabUrls);
        Assert.False(parsed.HasGeometry);
    }
}
