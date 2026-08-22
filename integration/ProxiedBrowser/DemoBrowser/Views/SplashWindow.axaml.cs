using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace DemoBrowser.Views;

/// <summary>
/// Animated startup screen. Shown while settings, the CA certificate and the Chromium runtime are
/// prepared; guaranteed to stay visible for at least <see cref="MinimumDisplayTime"/>.
/// </summary>
public partial class SplashWindow : Window
{
    public static readonly TimeSpan MinimumDisplayTime = TimeSpan.FromSeconds(1);

    private readonly Stopwatch _shownAt = Stopwatch.StartNew();

    public SplashWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => await FadeInAsync();
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

        await FadeOutAsync();
        Close();
    }

    private Task FadeInAsync()
    {
        var fade = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(450),
            Easing = new BackEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0.0),
                        new Setter(ScaleTransform.ScaleXProperty, 0.94),
                        new Setter(ScaleTransform.ScaleYProperty, 0.94),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1.0),
                        new Setter(ScaleTransform.ScaleXProperty, 1.0),
                        new Setter(ScaleTransform.ScaleYProperty, 1.0),
                    },
                },
            },
        };
        return fade.RunAsync(Root);
    }

    private Task FadeOutAsync()
    {
        var fade = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(300),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1.0),
                        new Setter(ScaleTransform.ScaleXProperty, 1.0),
                        new Setter(ScaleTransform.ScaleYProperty, 1.0),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0.0),
                        new Setter(ScaleTransform.ScaleXProperty, 1.05),
                        new Setter(ScaleTransform.ScaleYProperty, 1.05),
                    },
                },
            },
        };
        return fade.RunAsync(Root);
    }
}
