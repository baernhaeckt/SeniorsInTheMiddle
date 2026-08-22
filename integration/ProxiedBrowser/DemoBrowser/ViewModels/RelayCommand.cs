using System.Windows.Input;

namespace DemoBrowser.ViewModels;

/// <summary>
/// Simple ICommand. Avalonia has no <c>CommandManager</c>, so CanExecute is re-queried through the static
/// <see cref="RaiseCanExecuteChanged"/>, which notifies every live command (same semantics as WPF's
/// <c>CommandManager.InvalidateRequerySuggested</c>).
/// </summary>
public sealed class RelayCommand : ICommand
{
    private static readonly List<WeakReference<RelayCommand>> Instances = [];
    private static readonly Lock InstancesLock = new();

    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        lock (InstancesLock)
        {
            Instances.Add(new WeakReference<RelayCommand>(this));
        }
    }

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>Asks every command to re-evaluate CanExecute (always dispatched on the UI thread by callers).</summary>
    public static void RaiseCanExecuteChanged()
    {
        List<RelayCommand> alive;
        lock (InstancesLock)
        {
            alive = new List<RelayCommand>(Instances.Count);
            Instances.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var weak in Instances)
            {
                if (weak.TryGetTarget(out var cmd))
                {
                    alive.Add(cmd);
                }
            }
        }

        foreach (var cmd in alive)
        {
            cmd.CanExecuteChanged?.Invoke(cmd, EventArgs.Empty);
        }
    }
}
