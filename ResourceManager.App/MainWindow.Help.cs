using System.Windows;

namespace ResourceManager.App;

public partial class MainWindow
{
    private HelpWindow? helpWindow;

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        if (helpWindow is not null)
        {
            helpWindow.Activate();
            return;
        }

        var window = new HelpWindow { Owner = this, Icon = Icon };
        helpWindow = window;
        window.Closed += (_, _) => helpWindow = null;
        window.Show();
    }
}
