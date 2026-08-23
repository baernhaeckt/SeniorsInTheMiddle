using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace DemoBrowser.Views;

/// <summary>Which glyph and accent colour a <see cref="MessageDialog"/> shows.</summary>
public enum MessageDialogIcon
{
    Information,
    Warning,
    Error,
}

/// <summary>
/// Modal OK message box in the application's own chrome (Avalonia has no built-in MessageBox).
/// Replaces WPF's <c>MessageBox.Show</c>.
/// </summary>
public partial class MessageDialog : Window
{
    private readonly TaskCompletionSource _closed = new();

    public MessageDialog()
    {
        InitializeComponent();
        Closed += (_, _) => _closed.TrySetResult();
    }

    public MessageDialog(string message, string title, MessageDialogIcon icon) : this()
    {
        Title = title;
        HeaderTitle.Text = title;
        MessageText.Text = message;

        var (glyph, color) = icon switch
        {
            MessageDialogIcon.Error => ("⛔", "#FF8A3D56"),
            MessageDialogIcon.Warning => ("⚠", "#FFB58A1E"),
            _ => ("ℹ", "#FF288879"),
        };
        HeaderGlyph.Text = glyph;
        BadgeGlyph.Text = glyph;
        Badge.Background = new SolidColorBrush(Color.Parse(color));
    }

    /// <summary>Shows the dialog modally over <paramref name="owner"/> (or as a free-standing window when there is none).</summary>
    public static async Task ShowAsync(Window? owner, string message, string title, MessageDialogIcon icon)
    {
        var dialog = new MessageDialog(message, title, icon);
        if (owner is not null && owner.IsVisible)
        {
            await dialog.ShowDialog(owner);
            return;
        }

        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.Show();
        dialog.OkButton.Focus();
        await dialog._closed.Task;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
