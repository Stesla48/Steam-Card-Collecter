using System.Windows;
using System.Windows.Controls;
using SteamKit2.Authentication;

namespace SteamCardCollector.App;

internal sealed class WindowAuthenticator(Window owner, CancellationToken cancellationToken) : IAuthenticator
{
    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) => AskAsync(previousCodeWasIncorrect ? "Kod kabul edilmedi. Steam Guard'daki güncel kodu gir." : "Steam Guard uygulamasındaki kodu gir.");
    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) => AskAsync(previousCodeWasIncorrect ? "Kod kabul edilmedi. E-postandaki güncel kodu gir." : $"{email} adresine gönderilen Steam Guard kodunu gir.");
    public Task<bool> AcceptDeviceConfirmationAsync() => Task.FromResult(true);
    private async Task<string> AskAsync(string message)
    {
        return await owner.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = new Window { Owner = owner, Title = "Steam Guard", Width = 430, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new StackPanel { Margin = new Thickness(20) };
            var input = new TextBox { Margin = new Thickness(0, 12, 0, 12), FontSize = 20, MaxLength = 12 };
            var confirm = new Button { Content = "Onayla", Padding = new Thickness(16, 8, 16, 8), IsDefault = true };
            confirm.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(input.Text)) window.DialogResult = true; };
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(input); panel.Children.Add(confirm); window.Content = panel;
            window.Loaded += (_, _) => input.Focus();
            using var registration = cancellationToken.Register(() => owner.Dispatcher.BeginInvoke(new Action(window.Close)));
            if (window.ShowDialog() != true) throw new OperationCanceledException("Steam Guard girişi iptal edildi.");
            cancellationToken.ThrowIfCancellationRequested();
            return input.Text.Trim();
        });
    }
}
