using System.Windows;
using ResourceManager.Core;

namespace ResourceManager.App;

public partial class PublishModeDialog : Window
{
    public PublishMode SelectedMode => CopyOption.IsChecked == true ? PublishMode.Copy : PublishMode.Reference;

    public PublishModeDialog(string resourceName)
    {
        InitializeComponent();
        ResourceNameText.Text = resourceName;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
