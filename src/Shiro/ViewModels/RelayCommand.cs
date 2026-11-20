#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Shiro.Services;

namespace Shiro.ViewModels
{
    public interface IRaiseCanExecuteChanged
    {
        void RaiseCanExecuteChanged();
    }

    public interface IAsyncRelayCommand : ICommand, IRaiseCanExecuteChanged
    {
        Task ExecuteAsync(object? parameter = null);
    }

    public class RelayCommand : ICommand, IRaiseCanExecuteChanged
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public event EventHandler? CanExecuteChanged;

        public RelayCommand(Action execute) : this(_ => execute(), null) { }
        public RelayCommand(Action execute, Func<bool>? canExecute) : this(_ => execute(), canExecute == null ? null : _ => canExecute()) { }
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

        public void RaiseCanExecuteChanged()
            => AsyncRelayCommand.PostCanExecuteChanged(() => CanExecuteChanged, this);
    }

    public class AsyncRelayCommand : IAsyncRelayCommand
    {
        public static Action<Exception>? DefaultUnhandledErrorHandler { get; set; }

        private readonly Func<object?, Task> _executeAsync;
        private readonly Predicate<object?>? _canExecute;
        private readonly Action<Exception>? _onError;
        private int _isExecuting;

        public event EventHandler? CanExecuteChanged;

        public AsyncRelayCommand(Func<Task> executeAsync)
            : this(_ => executeAsync(), null, null) { }

        public AsyncRelayCommand(Func<Task> executeAsync, Func<bool>? canExecute)
            : this(_ => executeAsync(), canExecute == null ? null : _ => canExecute(), null) { }

        public AsyncRelayCommand(Func<Task> executeAsync, Func<bool>? canExecute, Action<Exception>? onError)
            : this(_ => executeAsync(), canExecute == null ? null : _ => canExecute(), onError) { }

        public AsyncRelayCommand(
            Func<object?, Task> executeAsync,
            Predicate<object?>? canExecute = null,
            Action<Exception>? onError = null)
        {
            ArgumentNullException.ThrowIfNull(executeAsync);
            _executeAsync = executeAsync;
            _canExecute = canExecute;
            _onError = onError;
        }

        public bool CanExecute(object? parameter) => Volatile.Read(ref _isExecuting) == 0 && (_canExecute == null || _canExecute(parameter));

        public async void Execute(object? parameter) => await ExecuteAsync(parameter);

        public async Task ExecuteAsync(object? parameter = null)
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
                HandleUnhandledCommandError(ex, _onError, nameof(AsyncRelayCommand));
            }
            finally
            {
                Volatile.Write(ref _isExecuting, 0);
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged()
            => PostCanExecuteChanged(() => CanExecuteChanged, this);

        internal static void PostCanExecuteChanged(Func<EventHandler?> handlerAccessor, object sender)
        {
            if (handlerAccessor() == null) return;
            AppDispatcher.Post(() => handlerAccessor()?.Invoke(sender, EventArgs.Empty));
        }

        internal static void HandleUnhandledCommandError(Exception ex, Action<Exception>? onError, string scope)
        {
            AppServices.Logger.Error($"[{scope}] Execution error: {ex.Message}", ex);
            if (onError != null)
            {
                try
                {
                    onError(ex);
                }
                catch (Exception handlerEx)
                {
                    AppServices.Logger.Error($"[{scope}] onError handler failed: {handlerEx.Message}", handlerEx);
                }
                return;
            }

            var defaultHandler = DefaultUnhandledErrorHandler;
            if (defaultHandler != null)
            {
                try
                {
                    defaultHandler(ex);
                }
                catch (Exception handlerEx)
                {
                    AppServices.Logger.Error($"[{scope}] DefaultUnhandledErrorHandler failed: {handlerEx.Message}", handlerEx);
                }
            }
        }
    }

    /// <summary>
    /// Strongly-typed generic RelayCommand for modern C# MVVM pattern.
    /// Eliminates unsafe runtime casting of command parameters.
    /// </summary>
    public class RelayCommand<T> : ICommand, IRaiseCanExecuteChanged
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

        public void RaiseCanExecuteChanged()
            => AsyncRelayCommand.PostCanExecuteChanged(() => CanExecuteChanged, this);
    }

    /// <summary>
    /// Strongly-typed generic asynchronous RelayCommand for modern C# MVVM pattern.
    /// </summary>
    public class AsyncRelayCommand<T> : IAsyncRelayCommand
    {
        private readonly Func<T, Task> _executeAsync;
        private readonly Predicate<T>? _canExecute;
        private readonly Action<Exception>? _onError;
        private int _isExecuting;

        public event EventHandler? CanExecuteChanged;

        public AsyncRelayCommand(
            Func<T, Task> executeAsync,
            Predicate<T>? canExecute = null,
            Action<Exception>? onError = null)
        {
            ArgumentNullException.ThrowIfNull(executeAsync);
            _executeAsync = executeAsync;
            _canExecute = canExecute;
            _onError = onError;
        }

        public bool CanExecute(object? parameter)
        {
            if (Volatile.Read(ref _isExecuting) != 0) return false;
            if (_canExecute == null) return true;
            if (parameter is T typed) return _canExecute(typed);
            if (parameter == null && default(T) == null) return _canExecute(default!);
            return false;
        }

        public async void Execute(object? parameter) => await ExecuteAsync(parameter);

        public async Task ExecuteAsync(object? parameter = null)
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
                AsyncRelayCommand.HandleUnhandledCommandError(ex, _onError, $"AsyncRelayCommand<{typeof(T).Name}>");
            }
            finally
            {
                Volatile.Write(ref _isExecuting, 0);
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged()
            => AsyncRelayCommand.PostCanExecuteChanged(() => CanExecuteChanged, this);
    }
}
