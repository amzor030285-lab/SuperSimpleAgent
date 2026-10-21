using System.Windows;
using System.Windows.Controls;

namespace SuperSimpleAgentGUI
{
    /// <summary>
    /// Read-only formatted text host that swallows the
    /// <see cref="FrameworkElement.RequestBringIntoViewEvent"/> so placing the
    /// mouse focus/caret inside it does not scroll the outer chat list (WPF
    /// auto-scrolls a newly focused element into view).
    /// </summary>
    public sealed class NoScrollRichTextBox : RichTextBox
    {
        public NoScrollRichTextBox()
        {
            this.AddHandler(FrameworkElement.RequestBringIntoViewEvent,
                new RequestBringIntoViewEventHandler(NoScrollTextBox.SuppressBringIntoView));
        }
    }
}
