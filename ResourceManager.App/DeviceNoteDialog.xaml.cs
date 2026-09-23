using System.Windows;

namespace ResourceManager.App;

public partial class DeviceNoteDialog : Window
{
    public string Note => NoteBox.Text;

    public DeviceNoteDialog(string deviceName, string note)
    {
        InitializeComponent();
        DeviceText.Text = deviceName;
        NoteBox.Text = note;
        Loaded += (_, _) => { NoteBox.Focus(); NoteBox.CaretIndex = NoteBox.Text.Length; };
    }

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
