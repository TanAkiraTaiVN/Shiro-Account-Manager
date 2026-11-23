#nullable enable
using System;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Shiro.Services
{
    public enum ToastType
    {
        Info,
        Success,
        Warning,
        Error
    }

    public class ToastItem : INotifyPropertyChanged
    {
        private static readonly IBrush SuccessAccentBrush = new ImmutableSolidColorBrush(Color.FromRgb(74, 222, 128));
        private static readonly IBrush WarningAccentBrush = new ImmutableSolidColorBrush(Color.FromRgb(251, 191, 36));
        private static readonly IBrush ErrorAccentBrush = new ImmutableSolidColorBrush(Color.FromRgb(251, 113, 133));
        private static readonly IBrush InfoAccentBrush = new ImmutableSolidColorBrush(Color.FromRgb(56, 189, 248));

        private static readonly IBrush SurfaceBgBrush = CreateSurfaceGradient();
        private static readonly IBrush s_topSpecularBrush = CreateTopSpecularBrush();

        private static readonly IBrush SuccessBorderBrush = new ImmutableSolidColorBrush(Color.FromArgb(64, 74, 222, 128));
        private static readonly IBrush WarningBorderBrush = new ImmutableSolidColorBrush(Color.FromArgb(64, 251, 191, 36));
        private static readonly IBrush ErrorBorderBrush = new ImmutableSolidColorBrush(Color.FromArgb(64, 251, 113, 133));
        private static readonly IBrush InfoBorderBrush = new ImmutableSolidColorBrush(Color.FromArgb(92, 56, 60, 82));

        private static readonly IBrush SuccessBadgeBgBrush = new ImmutableSolidColorBrush(Color.FromArgb(34, 74, 222, 128));
        private static readonly IBrush WarningBadgeBgBrush = new ImmutableSolidColorBrush(Color.FromArgb(34, 251, 191, 36));
        private static readonly IBrush ErrorBadgeBgBrush = new ImmutableSolidColorBrush(Color.FromArgb(34, 251, 113, 133));
        private static readonly IBrush InfoBadgeBgBrush = new ImmutableSolidColorBrush(Color.FromArgb(34, 56, 189, 248));

        private static readonly IBrush SuccessBadgeBorderBrush = new ImmutableSolidColorBrush(Color.FromArgb(95, 74, 222, 128));
        private static readonly IBrush WarningBadgeBorderBrush = new ImmutableSolidColorBrush(Color.FromArgb(95, 251, 191, 36));
        private static readonly IBrush ErrorBadgeBorderBrush = new ImmutableSolidColorBrush(Color.FromArgb(95, 251, 113, 133));
        private static readonly IBrush InfoBadgeBorderBrush = new ImmutableSolidColorBrush(Color.FromArgb(95, 56, 189, 248));

        private static readonly IBrush SuccessAuraBrush = CreateAuraBrush(74, 222, 128);
        private static readonly IBrush WarningAuraBrush = CreateAuraBrush(251, 191, 36);
        private static readonly IBrush ErrorAuraBrush = CreateAuraBrush(251, 113, 133);
        private static readonly IBrush InfoAuraBrush = CreateAuraBrush(56, 189, 248);

        private static readonly IBrush SuccessProgressBrush = CreateProgressBrush(74, 222, 128);
        private static readonly IBrush WarningProgressBrush = CreateProgressBrush(251, 191, 36);
        private static readonly IBrush ErrorProgressBrush = CreateProgressBrush(251, 113, 133);
        private static readonly IBrush InfoProgressBrush = CreateProgressBrush(56, 189, 248);

        private static Geometry? _successIconGeometry;
        private static Geometry? _warningIconGeometry;
        private static Geometry? _errorIconGeometry;
        private static Geometry? _infoIconGeometry;

        private ICommand? _actionCommand;
        private double _durationSeconds;
        private long _timerGeneration;
        private bool _isRead;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public ToastType Type { get; set; }
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public string TimestampText => CreatedAt.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        public ICommand DismissCommand { get; }
        public string ActionText { get; set; } = string.Empty;

        public bool IsRead
        {
            get => _isRead;
            set
            {
                if (_isRead != value)
                {
                    _isRead = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRead)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUnread)));
                }
            }
        }

        public bool IsUnread => !_isRead;

        public ICommand? ActionCommand
        {
            get => _actionCommand;
            set
            {
                if (!ReferenceEquals(_actionCommand, value))
                {
                    _actionCommand = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActionCommand)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAction)));
                }
            }
        }

        public double DurationSeconds
        {
            get => _durationSeconds;
            set
            {
                if (Math.Abs(_durationSeconds - value) > double.Epsilon)
                {
                    _durationSeconds = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationSeconds)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAutoDismiss)));
                }
            }
        }

        public long TimerGeneration
        {
            get => _timerGeneration;
            set
            {
                if (_timerGeneration != value)
                {
                    _timerGeneration = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimerGeneration)));
                }
            }
        }

        internal CancellationTokenSource? AutoDismissCts { get; set; }

        internal sealed class ToastActionCommand : ICommand
        {
            private readonly Action _execute;

            public ToastActionCommand(Action execute)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            }

            public event EventHandler? CanExecuteChanged
            {
                add { }
                remove { }
            }

            public bool CanExecute(object? parameter) => true;

            public void Execute(object? parameter) => _execute();
        }

        public ToastItem()
        {
            DismissCommand = new ToastActionCommand(() => ToastService.Dismiss(this));
        }

        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
        public bool HasAction => ActionCommand != null && !string.IsNullOrWhiteSpace(ActionText);
        public bool HasAutoDismiss => double.IsFinite(_durationSeconds) && _durationSeconds > 0.0;

        public Geometry? IconGeometry => Type switch
        {
            ToastType.Success => _successIconGeometry ??= TryParseGeometry("M 20,6 L 9,17 L 4,12"),
            ToastType.Warning => _warningIconGeometry ??= TryParseGeometry("M 12,22 C 12,22 20,18 20,12 L 20,5 L 12,2 L 4,5 L 4,12 C 4,18 12,22 12,22 Z M 12,8 L 12,13 M 12,16.5 L 12.02,16.5"),
            ToastType.Error => _errorIconGeometry ??= TryParseGeometry("M 18,6 L 6,18 M 6,6 L 18,18"),
            _ => _infoIconGeometry ??= TryParseGeometry("M 12,2 A 10,10 0 1 0 12,22 A 10,10 0 1 0 12,2 Z M 12,16 L 12,12 M 12,8 L 12.01,8")
        };

        private static Geometry? TryParseGeometry(string pathData)
        {
            try
            {
                return StreamGeometry.Parse(pathData);
            }
            catch (Exception ex)
            {
                AppServices.Logger.Debug("[ToastItem] TryParseGeometry failed", ex);
                return null;
            }
        }

        private static IBrush CreateSurfaceGradient()
        {
            var stops = new ImmutableGradientStop[]
            {
                new(0.0, Color.FromArgb(245, 24, 24, 36)),
                new(1.0, Color.FromArgb(245, 16, 16, 24))
            };
            return new ImmutableLinearGradientBrush(
                stops,
                startPoint: new RelativePoint(0.0, 0.0, RelativeUnit.Relative),
                endPoint: new RelativePoint(0.0, 1.0, RelativeUnit.Relative));
        }

        private static IBrush CreateTopSpecularBrush()
        {
            var stops = new ImmutableGradientStop[]
            {
                new(0.0, Color.FromArgb(0, 255, 255, 255)),
                new(0.25, Color.FromArgb(40, 255, 255, 255)),
                new(0.50, Color.FromArgb(56, 255, 255, 255)),
                new(0.75, Color.FromArgb(40, 255, 255, 255)),
                new(1.0, Color.FromArgb(0, 255, 255, 255))
            };
            return new ImmutableLinearGradientBrush(
                stops,
                startPoint: new RelativePoint(0.0, 0.5, RelativeUnit.Relative),
                endPoint: new RelativePoint(1.0, 0.5, RelativeUnit.Relative));
        }

        private static IBrush CreateAuraBrush(byte r, byte g, byte b)
        {
            var stops = new ImmutableGradientStop[]
            {
                new(0.0, Color.FromArgb(38, r, g, b)),
                new(0.48, Color.FromArgb(11, r, g, b)),
                new(1.0, Color.FromArgb(0, r, g, b))
            };
            var origin = new RelativePoint(0.08, 0.26, RelativeUnit.Relative);
            return new ImmutableRadialGradientBrush(
                stops,
                center: origin,
                gradientOrigin: origin,
                radiusX: new RelativeScalar(0.46, RelativeUnit.Relative),
                radiusY: new RelativeScalar(1.05, RelativeUnit.Relative));
        }

        private static IBrush CreateProgressBrush(byte r, byte g, byte b)
        {
            var stops = new ImmutableGradientStop[]
            {
                new(0.0, Color.FromArgb(75, r, g, b)),
                new(1.0, Color.FromArgb(225, r, g, b))
            };
            return new ImmutableLinearGradientBrush(
                stops,
                startPoint: new RelativePoint(0.0, 0.5, RelativeUnit.Relative),
                endPoint: new RelativePoint(1.0, 0.5, RelativeUnit.Relative));
        }

        public IBrush AccentBrush => Type switch
        {
            ToastType.Success => SuccessAccentBrush,
            ToastType.Warning => WarningAccentBrush,
            ToastType.Error => ErrorAccentBrush,
            _ => InfoAccentBrush
        };

        public IBrush BackgroundBrush => SurfaceBgBrush;

        public IBrush BorderBrush => Type switch
        {
            ToastType.Success => SuccessBorderBrush,
            ToastType.Warning => WarningBorderBrush,
            ToastType.Error => ErrorBorderBrush,
            _ => InfoBorderBrush
        };

        public IBrush BadgeBackground => Type switch
        {
            ToastType.Success => SuccessBadgeBgBrush,
            ToastType.Warning => WarningBadgeBgBrush,
            ToastType.Error => ErrorBadgeBgBrush,
            _ => InfoBadgeBgBrush
        };

        public IBrush BadgeBorderBrush => Type switch
        {
            ToastType.Success => SuccessBadgeBorderBrush,
            ToastType.Warning => WarningBadgeBorderBrush,
            ToastType.Error => ErrorBadgeBorderBrush,
            _ => InfoBadgeBorderBrush
        };

        public IBrush AuraBrush => Type switch
        {
            ToastType.Success => SuccessAuraBrush,
            ToastType.Warning => WarningAuraBrush,
            ToastType.Error => ErrorAuraBrush,
            _ => InfoAuraBrush
        };

        public IBrush ProgressBrush => Type switch
        {
            ToastType.Success => SuccessProgressBrush,
            ToastType.Warning => WarningProgressBrush,
            ToastType.Error => ErrorProgressBrush,
            _ => InfoProgressBrush
        };

        public IBrush TopSpecularBrush => s_topSpecularBrush;
    }
}
