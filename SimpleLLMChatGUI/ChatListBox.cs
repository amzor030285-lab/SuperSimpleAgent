using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SimpleLLMChatGUI
{
    // ListBox variant used for the chat list: its item containers never
    // participate in click-selection, so clicking a button inside a turn
    // (e.g. a code-block Copy button) does not move the scroll offset.
    public class ChatListBox : ListBox
    {
        protected override DependencyObject GetContainerForItemOverride()
        {
            return new ChatListBoxItem();
        }

        protected override bool IsItemItsOwnContainerOverride(object item)
        {
            return item is ChatListBoxItem;
        }
    }

    // ListBoxItem variant that skips the base OnMouseLeftButtonDown: the base
    // focuses the item and notifies the ListBox, which changes selection and
    // makes the framework scroll the item into view. UIElement's own hook is
    // a no-op, so not calling base loses nothing.
    public class ChatListBoxItem : ListBoxItem
    {
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (!e.Handled)
                e.Handled = true;
        }
    }
}
