using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SimpleLLMChatGUI
{
    public static class CopyButtonFactory
    {
        private const string CopyGeometry = "M 4,9.5 V 1.5 H 11.5 M 5,4.5 H 12.5 V 12.5 H 5 Z";
        private const string CopiedGeometry = "M 2,7.5 L 5.5,11 L 12,3.5";

        public static Button Create(string automationId, Action onCopy)
        {
            var button = new Button
            {
                Content = BuildIcon(CopyGeometry, 0.6),
                MinWidth = 28,
                MinHeight = 22,
                Padding = new Thickness(5, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
                ToolTip = "Copy"
            };
            AutomationProperties.SetAutomationId(button, automationId);
            AutomationProperties.SetName(button, "Copy");

            var resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            resetTimer.Tick += (s, e) =>
            {
                button.Content = BuildIcon(CopyGeometry, 0.6);
                button.ToolTip = "Copy";
                resetTimer.Stop();
            };

            button.Click += (s, e) =>
            {
                onCopy();
                button.Content = BuildIcon(CopiedGeometry, 1.0);
                button.ToolTip = "Copied";
                resetTimer.Stop();
                resetTimer.Start();
            };

            return button;
        }

        private static Path BuildIcon(string geometry, double opacity)
        {
            var icon = new Path
            {
                Data = Geometry.Parse(geometry),
                Width = 15,
                Height = 15,
                Stretch = Stretch.Uniform,
                StrokeThickness = 1.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Opacity = opacity
            };
            icon.SetResourceReference(Path.StrokeProperty, "ChatTextColorBrush");
            return icon;
        }
    }
}
