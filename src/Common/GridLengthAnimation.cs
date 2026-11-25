using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace LlmodelBox.Common
{
    /// <summary>
    /// Animates a <see cref="GridLength"/> (e.g. a ColumnDefinition.Width) between two absolute
    /// pixel values. WPF has no built-in GridLength animation, so this bridges DoubleAnimation-style
    /// easing onto the GridLength property.
    /// </summary>
    public class GridLengthAnimation : AnimationTimeline
    {
        public override Type TargetPropertyType => typeof(GridLength);

        public static readonly DependencyProperty FromProperty =
            DependencyProperty.Register(nameof(From), typeof(double), typeof(GridLengthAnimation));

        public static readonly DependencyProperty ToProperty =
            DependencyProperty.Register(nameof(To), typeof(double), typeof(GridLengthAnimation));

        public static readonly DependencyProperty EasingFunctionProperty =
            DependencyProperty.Register(nameof(EasingFunction), typeof(IEasingFunction), typeof(GridLengthAnimation));

        public double From
        {
            get => (double)GetValue(FromProperty);
            set => SetValue(FromProperty, value);
        }

        public double To
        {
            get => (double)GetValue(ToProperty);
            set => SetValue(ToProperty, value);
        }

        public IEasingFunction? EasingFunction
        {
            get => (IEasingFunction?)GetValue(EasingFunctionProperty);
            set => SetValue(EasingFunctionProperty, value);
        }

        protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

        public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
        {
            double from = From;
            double to = To;
            double progress = animationClock.CurrentProgress ?? 0.0;
            if (EasingFunction != null)
                progress = EasingFunction.Ease(progress);

            double current = from + (to - from) * progress;
            return new GridLength(current, GridUnitType.Pixel);
        }
    }
}
