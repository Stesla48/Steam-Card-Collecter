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

    public string TakeCode()
    {
        var code = CodeBox.Password.Trim();
        CodeBox.Clear();
        return code;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(CodeBox.Password)) DialogResult = true;
    }
}
