using System.Windows;

namespace SteamCardCollector.App;

public partial class GuardWindow : Window
{
    public GuardWindow(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
        Loaded += (_, _) => CodeBox.Focus();
    }

    public string Code => CodeBox.Text.Trim();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(CodeBox.Text)) DialogResult = true;
    }
}
