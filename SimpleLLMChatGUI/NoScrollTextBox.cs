using System.Windows;
using System.Windows.Controls;

namespace SimpleLLMChatGUI
{
    /// <summary>
    /// Read-only selectable text host that swallows the
    /// <see cref="FrameworkElement.RequestBringIntoViewEvent"/> so placing the
    /// mouse focus/caret inside it does not scroll the outer chat list (WPF
    /// auto-scrolls a newly focused element into view).
    /// </summary>
    public sealed class NoScrollTextBox : TextBox
    {
        public NoScrollTextBox()
        {
            this.AddHandler(FrameworkElement.RequestBringIntoViewEvent,
                new RequestBringIntoViewEventHandler(SuppressBringIntoView));
        }

        private void SuppressBringIntoView(object sender, RequestBringIntoViewEventArgs e)
        {
            e.Handled = true;
        }
    }
}
