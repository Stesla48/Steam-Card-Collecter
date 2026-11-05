using System.Windows;
using SteamKit2.Authentication;

namespace SteamCardCollector.App;

internal sealed class WindowAuthenticator(Window owner, CancellationToken cancellationToken) : IAuthenticator
{
    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) => AskAsync(previousCodeWasIncorrect
        ? "Kod kabul edilmedi. Steam Guard'daki güncel kodu gir."
        : "Steam uygulamasındaki güncel kodu gir. Telefonundan onay verdiysen bu pencere kapanır.");

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) => AskAsync(previousCodeWasIncorrect
        ? "Kod kabul edilmedi. E-postandaki güncel kodu gir."
        : $"{email} adresine gönderilen Steam Guard kodunu gir.");

    public Task<bool> AcceptDeviceConfirmationAsync() => Task.FromResult(true);

    private async Task<string> AskAsync(string message)
    {
        return await owner.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var host = owner as MainWindow;
            host?.SetGuardOverlay(true);
            var window = new GuardWindow(message) { Owner = owner };
            using var registration = cancellationToken.Register(() => owner.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (window.IsVisible) window.Close();
            })));
            try
            {
                owner.UpdateLayout();
                var accepted = window.ShowDialog() == true;
                var code = window.TakeCode();
                cancellationToken.ThrowIfCancellationRequested();
                if (!accepted || string.IsNullOrEmpty(code)) throw new OperationCanceledException("Steam Guard girişi iptal edildi.");
                return code;
            }
            finally
            {
                host?.SetGuardOverlay(false);
            }
        });
    }
}
