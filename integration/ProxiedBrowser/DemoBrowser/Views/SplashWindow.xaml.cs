using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Animation;

namespace DemoBrowser.Views;

/// <summary>
/// Animated startup screen. Shown while settings, the CA certificate and the WebView2 environment are
/// prepared; guaranteed to stay visible for at least <see cref="MinimumDisplayTime"/>.
/// </summary>
public partial class SplashWindow : Window
{
    public static readonly TimeSpan MinimumDisplayTime = TimeSpan.FromSeconds(1);

    private readonly Stopwatch _shownAt = Stopwatch.StartNew();

    public SplashWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ((Storyboard)FindResource("FadeIn")).Begin(this);
            ((Storyboard)FindResource("Intro")).Begin(this, isControllable: true);
        };
    }

    /// <summary>Updates the status line under the wordmark.</summary>
    public void SetStatus(string text) => StatusText.Text = text;

    /// <summary>Waits until the minimum display time has elapsed, plays the fade-out and closes.</summary>
    public async Task DismissAsync()
    {
        var remaining = MinimumDisplayTime - _shownAt.Elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining);
        }

        var tcs = new TaskCompletionSource();
        var fadeOut = (Storyboard)FindResource("FadeOut");
        fadeOut.Completed += (_, _) => tcs.TrySetResult();
        fadeOut.Begin(this);
        await tcs.Task;
        Close();
    }
}
