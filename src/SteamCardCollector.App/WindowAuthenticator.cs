using SteamCardCollector.Core;
using System.Windows;
using SteamKit2.Authentication;

namespace SteamCardCollector.App;

internal sealed class WindowAuthenticator(Window owner, CancellationToken cancellationToken) : IAuthenticator
{
    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) => AskAsync(previousCodeWasIncorrect
        ? Strings.DeviceCodeRejected
        : Strings.DeviceApprovalPrompt);

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) => AskAsync(previousCodeWasIncorrect
        ? Strings.EmailCodeRejected
        : string.Format(Strings.EmailCodePrompt, email));

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
                if (window.ShowDialog() != true) throw new OperationCanceledException(Strings.SteamGuardSigninCanceled);
                cancellationToken.ThrowIfCancellationRequested();
                return window.Code;
            }
            finally
            {
                host?.SetGuardOverlay(false);
            }
        });
    }
}
