#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public event EventHandler? CanExecuteChanged;

        public RelayCommand(Action execute) : this(_ => execute(), null) { }
        public RelayCommand(Action<object?> execute) : this(execute, null) { }

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute)
        {
            ArgumentNullException.ThrowIfNull(execute);
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute == null || _canExecute(parameter);

        public void Execute(object? parameter)
        {
            try
            {
                _execute(parameter);
            }
            catch (Exception ex)
            {
                AppServices.Logger.Error($"[RelayCommand] Execution error: {ex.Message}", ex);
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _executeAsync;
        private readonly Predicate<object?>? _canExecute;
        private int _isExecuting;

        public event EventHandler? CanExecuteChanged;

        public AsyncRelayCommand(Func<Task> executeAsync) : this(_ => executeAsync(), null) { }

        public AsyncRelayCommand(Func<object?, Task> executeAsync, Predicate<object?>? canExecute = null)
        {
            ArgumentNullException.ThrowIfNull(executeAsync);
            _executeAsync = executeAsync;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => Volatile.Read(ref _isExecuting) == 0 && (_canExecute == null || _canExecute(parameter));

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0) return;

            try
            {
                RaiseCanExecuteChanged();
                await _executeAsync(parameter);
            }
            catch (OperationCanceledException)
            {
                // Task was canceled, expected on user cancellation
            }
            catch (Exception ex)
            {
                AppServices.Logger.Error($"[AsyncRelayCommand] Execution error: {ex.Message}", ex);
            }
            finally
            {
                Volatile.Write(ref _isExecuting, 0);
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Strongly-typed generic RelayCommand for modern C# MVVM pattern.
    /// Eliminates unsafe runtime casting of command parameters.
    /// </summary>
    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        private readonly Predicate<T>? _canExecute;

        public event EventHandler? CanExecuteChanged;

        public RelayCommand(Action<T> execute, Predicate<T>? canExecute = null)
        {
            ArgumentNullException.ThrowIfNull(execute);
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
        {
            if (_canExecute == null) return true;
            if (parameter is T typed) return _canExecute(typed);
            if (parameter == null && default(T) == null) return _canExecute(default!);
            return false;
        }

        public void Execute(object? parameter)
        {
            try
            {
                if (parameter is T typed)
                    _execute(typed);
                else if (parameter == null && default(T) == null)
                    _execute(default!);
            }
            catch (Exception ex)
            {
                AppServices.Logger.Error($"[RelayCommand<{typeof(T).Name}>] Execution error: {ex.Message}", ex);
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Strongly-typed generic asynchronous RelayCommand for modern C# MVVM pattern.
    /// </summary>
    public class AsyncRelayCommand<T> : ICommand
    {
        private readonly Func<T, Task> _executeAsync;
        private readonly Predicate<T>? _canExecute;
        private int _isExecuting;

        public event EventHandler? CanExecuteChanged;

        public AsyncRelayCommand(Func<T, Task> executeAsync, Predicate<T>? canExecute = null)
        {
            ArgumentNullException.ThrowIfNull(executeAsync);
            _executeAsync = executeAsync;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
        {
            if (Volatile.Read(ref _isExecuting) != 0) return false;
            if (_canExecute == null) return true;
            if (parameter is T typed) return _canExecute(typed);
            if (parameter == null && default(T) == null) return _canExecute(default!);
            return false;
        }

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            if (Interlocked.CompareExchange(ref _isExecuting, 1, 0) != 0) return;

            try
            {
                RaiseCanExecuteChanged();
                if (parameter is T typed)
                    await _executeAsync(typed);
                else if (parameter == null && default(T) == null)
                    await _executeAsync(default!);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
            catch (Exception ex)
            {
                AppServices.Logger.Error($"[AsyncRelayCommand<{typeof(T).Name}>] Execution error: {ex.Message}", ex);
            }
            finally
            {
                Volatile.Write(ref _isExecuting, 0);
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
