using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace RevitPlanningPlugin.Infrastructure
{
    /// <summary>
    /// Базовый класс с реализацией INotifyPropertyChanged.
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    /// <summary>
    /// Реализация ICommand для MVVM-привязок.
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute != null ? _ => canExecute() : null) { }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);

        public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Асинхронная команда.
    /// </summary>
    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, System.Threading.Tasks.Task> _execute;
        private readonly Func<object?, bool>? _canExecute;
        private bool _isExecuting;

        public AsyncRelayCommand(Func<object?, System.Threading.Tasks.Task> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public AsyncRelayCommand(Func<System.Threading.Tasks.Task> execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute != null ? (Func<object?, bool>)(_ => canExecute()) : null) { }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);

        public async void Execute(object? parameter)
        {
            if (_isExecuting) return;
            _isExecuting = true;
            CommandManager.InvalidateRequerySuggested();
            try
            {
                await _execute(parameter);
            }
            catch (Exception ex)
            {
                // Prevent unhandled exception from crashing Revit.
                // Individual command handlers should handle their own errors,
                // but this is a safety net to avoid process termination.
                System.Diagnostics.Debug.WriteLine($"AsyncRelayCommand unhandled exception: {ex}");
            }
            finally
            {
                _isExecuting = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
}
