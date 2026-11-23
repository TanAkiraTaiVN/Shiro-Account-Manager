#nullable enable
using System;
using System.ComponentModel;
using System.Numerics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Shiro.Services;
using Shiro.ViewModels;

namespace Shiro.AvaloniaUI.Views.Panels
{
    public partial class MainWindowOverlaysPanel : UserControl
    {
        public const double InspectorToastOffsetX = -356.0;
        private static readonly Easing s_toastSlideEasing = new SpringEasing(mass: 1.0, stiffness: 280.0, damping: 30.0, initialVelocity: 0.0);

        private MainViewModel? _subscribedMainVm;
        private AccountInspectorViewModel? _subscribedInspector;
        private TranslateTransform? _toastTransform;

        public MainWindowOverlaysPanel()
        {
            InitializeComponent();
            EnsureToastTransform();
        }

        public double ToastHorizontalOffset => _toastTransform?.X ?? 0.0;

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            SyncInspectorSubscription();
            UpdateToastHorizontalOffset(animate: false);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (ToastItemsControl != null && ToastItemsControl.ItemsSource == null)
                ToastItemsControl.ItemsSource = ToastService.ActiveToasts;
            if (ViewNotificationCenter != null)
            {
                ViewNotificationCenter.DataContext ??= NotificationHistoryService.Instance;
                ViewNotificationCenter.AttachStaticBindings();
            }
            SyncInspectorSubscription();
            UpdateToastHorizontalOffset(animate: false);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            DetachInspectorSubscription();
            DetachStaticBindings();
            base.OnDetachedFromVisualTree(e);
        }

        internal void DetachStaticBindings()
        {
            if (ToastItemsControl != null)
                ToastItemsControl.ItemsSource = null;
            if (ViewNotificationCenter != null)
            {
                ViewNotificationCenter.DetachStaticBindings();
                if (ReferenceEquals(ViewNotificationCenter.DataContext, NotificationHistoryService.Instance))
                    ViewNotificationCenter.DataContext = null;
            }
        }

        private void SyncInspectorSubscription()
        {
            var nextVm = DataContext as MainViewModel;
            if (ReferenceEquals(_subscribedMainVm, nextVm))
                return;

            DetachInspectorSubscription();
            _subscribedMainVm = nextVm;
            if (_subscribedMainVm?.Inspector is { } inspector)
            {
                _subscribedInspector = inspector;
                _subscribedInspector.PropertyChanged += OnInspectorPropertyChanged;
            }
        }

        private void DetachInspectorSubscription()
        {
            if (_subscribedInspector != null)
            {
                _subscribedInspector.PropertyChanged -= OnInspectorPropertyChanged;
                _subscribedInspector = null;
            }
            _subscribedMainVm = null;
        }

        private void OnInspectorPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AccountInspectorViewModel.IsOpen))
                UpdateToastHorizontalOffset(animate: true);
        }

        private TranslateTransform? EnsureToastTransform()
        {
            if (ToastItemsControl == null)
                return null;

            _toastTransform ??= ToastItemsControl.RenderTransform as TranslateTransform ?? new TranslateTransform();
            if (!ReferenceEquals(ToastItemsControl.RenderTransform, _toastTransform))
                ToastItemsControl.RenderTransform = _toastTransform;
            return _toastTransform;
        }

        private void UpdateToastHorizontalOffset(bool animate)
        {
            if (EnsureToastTransform() is not { } transform)
                return;

            double prevX = transform.X;
            double targetX = _subscribedInspector?.IsOpen == true ? InspectorToastOffsetX : 0.0;
            transform.X = targetX;

            if (animate && ToastItemsControl != null && Math.Abs(prevX - targetX) > 0.5)
                RunToastHorizontalSlide(ToastItemsControl, prevX - targetX);
        }

        private static void RunToastHorizontalSlide(Visual target, double startDeltaX)
        {
            CompositionVisual? visual = ElementComposition.GetElementVisual(target);
            if (visual == null)
                return;

            visual.StopAnimation("Translation");
            if (VisualPreferencesService.Instance.ReducedMotion ||
                Math.Abs(startDeltaX) <= 0.5 ||
                visual.Compositor is not { } compositor)
            {
                visual.Translation = default;
                return;
            }

            Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
            slide.Target = "Translation";
            slide.Duration = TimeSpan.FromMilliseconds(220);
            slide.StopBehavior = AnimationStopBehavior.SetToFinalValue;
            slide.InsertKeyFrame(0.0f, new Vector3((float)startDeltaX, 0.0f, 0.0f));
            slide.InsertKeyFrame(1.0f, Vector3.Zero, s_toastSlideEasing);
            visual.StartAnimation("Translation", slide);
        }

        private void OnNotificationBackdropPressed(object? sender, PointerPressedEventArgs e)
        {
            if (ReferenceEquals(e.Source, sender))
            {
                if (DataContext is MainViewModel vm)
                    vm.IsNotificationCenterOpen = false;
                else
                    NotificationHistoryService.Instance.IsCenterOpen = false;
                e.Handled = true;
            }
        }
    }
}
