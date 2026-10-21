using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace ResourceManager.App;

public partial class MainWindow
{
    // Isolated checks replace only this sink; the user's clipboard is touched only by a copy action.
    private Action<string>? chatTextClipboardWriter = null;

    private void ChatCopyText_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        var menu = ItemsControl.ItemsControlFromItemContainer(item) as ContextMenu ?? item.Parent as ContextMenu;
        if (menu?.PlacementTarget is TextBox text) CopyChatMessageText(text);
    }

    internal bool CopyChatMessageText(TextBox text)
    {
        var value = text.SelectionLength > 0 ? text.SelectedText : text.Text;
        if (string.IsNullOrEmpty(value)) return false;
        try
        {
            if (chatTextClipboardWriter is { } write) write(value);
            else System.Windows.Clipboard.SetText(value);
            SetStatus("已复制聊天文字。");
            return true;
        }
        catch (Exception error) when (error is ExternalException or ArgumentException)
        {
            SetStatus("暂时无法复制，请稍后重试。");
            return false;
        }
    }
}
