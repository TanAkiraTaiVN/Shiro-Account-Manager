#nullable enable
using System;
using System.Numerics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Shiro.Services;

namespace Shiro.AvaloniaUI.Controls
{
    /// <summary>
    /// 120Hz render-thread composition physics helpers for implicit layout offsets
    /// and spring entrance transitions with automatic ReducedMotion and headless safety.
    /// </summary>
    public static class SpringCompositionHelper
    {
        private static readonly Easing s_springEasing = new SpringEasing(mass: 1.0, stiffness: 260.0, damping: 28.0, initialVelocity: 0.0);
        private static readonly Easing s_opacityEasing = new CubicEaseOut();

        public static void AttachImplicitOffsetAndOpacity(Visual? target, double durationMs = 240)
        {
            if (target is null)
                return;

            CompositionVisual? visual = ElementComposition.GetElementVisual(target);
            if (visual?.Compositor is not { } compositor)
                return;

            if (double.IsNaN(durationMs) || double.IsInfinity(durationMs))
                durationMs = 240.0;

            if (VisualPreferencesService.Instance.ReducedMotion || durationMs <= 0.0)
            {
                visual.ImplicitAnimations = null;
                return;
            }

            TimeSpan duration = TimeSpan.FromMilliseconds(Math.Clamp(durationMs, 16.0, 2000.0));

            Vector3KeyFrameAnimation offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
            offsetAnimation.Target = "Offset";
            offsetAnimation.Duration = duration;
            offsetAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", s_springEasing);

            ScalarKeyFrameAnimation opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
            opacityAnimation.Target = "Opacity";
            opacityAnimation.Duration = duration;
            opacityAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", s_opacityEasing);

            ImplicitAnimationCollection implicitAnimations = compositor.CreateImplicitAnimationCollection();
            implicitAnimations["Offset"] = offsetAnimation;
            implicitAnimations["Opacity"] = opacityAnimation;
            visual.ImplicitAnimations = implicitAnimations;
        }

        public static void RunSpringEntrance(
            Visual? target,
            double offsetX,
            double offsetY,
            double fromScale = 0.96,
            double durationMs = 260)
        {
            if (target is null)
                return;

            target.Opacity = 1.0;

            CompositionVisual? visual = ElementComposition.GetElementVisual(target);
            if (visual is null)
                return;

            visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");
            visual.StopAnimation("Scale");

            if (double.IsNaN(offsetX) || double.IsInfinity(offsetX)) offsetX = 0.0;
            if (double.IsNaN(offsetY) || double.IsInfinity(offsetY)) offsetY = 0.0;
            if (double.IsNaN(fromScale) || double.IsInfinity(fromScale)) fromScale = 1.0;
            if (double.IsNaN(durationMs) || double.IsInfinity(durationMs)) durationMs = 260.0;

            if (VisualPreferencesService.Instance.ReducedMotion ||
                durationMs <= 0.0 ||
                visual.Compositor is not { } compositor)
            {
                visual.Opacity = 1.0f;
                visual.Translation = default;
                visual.Scale = new Vector3D(1.0, 1.0, 1.0);
                return;
            }

            TimeSpan duration = TimeSpan.FromMilliseconds(Math.Clamp(durationMs, 16.0, 2000.0));

            ScalarKeyFrameAnimation opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
            opacityAnimation.Target = "Opacity";
            opacityAnimation.Duration = duration;
            opacityAnimation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
            opacityAnimation.InsertKeyFrame(0.0f, 0.0f);
            opacityAnimation.InsertKeyFrame(1.0f, 1.0f, s_opacityEasing);
            visual.StartAnimation("Opacity", opacityAnimation);

            if (Math.Abs(offsetX) > 0.01 || Math.Abs(offsetY) > 0.01)
            {
                Vector3KeyFrameAnimation translationAnimation = compositor.CreateVector3KeyFrameAnimation();
                translationAnimation.Target = "Translation";
                translationAnimation.Duration = duration;
                translationAnimation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
                translationAnimation.InsertKeyFrame(0.0f, new Vector3((float)offsetX, (float)offsetY, 0.0f));
                translationAnimation.InsertKeyFrame(1.0f, Vector3.Zero, s_springEasing);
                visual.StartAnimation("Translation", translationAnimation);
            }
            else
            {
                visual.Translation = default;
            }

            if (Math.Abs(fromScale - 1.0) > 0.001)
            {
                double clampedScale = Math.Clamp(fromScale, 0.5, 1.5);
                double w = target.Bounds.Width > 0.0 ? target.Bounds.Width : visual.Size.X;
                double h = target.Bounds.Height > 0.0 ? target.Bounds.Height : visual.Size.Y;
                if (w > 0.0 && h > 0.0)
                    visual.CenterPoint = new Vector3D(w * 0.5, h * 0.5, 0.0);

                Vector3KeyFrameAnimation scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
                scaleAnimation.Target = "Scale";
                scaleAnimation.Duration = duration;
                scaleAnimation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
                scaleAnimation.InsertKeyFrame(0.0f, new Vector3((float)clampedScale, (float)clampedScale, 1.0f));
                scaleAnimation.InsertKeyFrame(1.0f, Vector3.One, s_springEasing);
                visual.StartAnimation("Scale", scaleAnimation);
            }
            else
            {
                visual.Scale = new Vector3D(1.0, 1.0, 1.0);
            }
        }
    }
}
