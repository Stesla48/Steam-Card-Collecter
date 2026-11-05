using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using SteamCardCollector.Core;
using SteamCardCollector.Steam;

namespace SteamCardCollector.App;

public partial class MainWindow : Window
{
    public ObservableCollection<CardGame> Games { get; } = new();
    public ObservableCollection<OwnedGame> Library { get; } = new();
    private readonly CancellationTokenSource lifetime = new();
    private SteamSession? session;
    private readonly LocalSteamPlayer localPlayer = new();
    private readonly DispatcherTimer identityTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool accountsMatch;
    private string? interruption;
    private CancellationTokenSource? farmCancellation;
    private Task? farmTask;
    private Task? operation;
    private bool busy;
    private bool closing;
    private bool closeReady;

    public MainWindow()
    {
        InitializeComponent(); DataContext = this;
        localPlayer.Interrupted += message => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (closing || farmTask is not { IsCompleted: false }) return;
            interruption = message;
            farmCancellation?.Cancel();
            StatusLabel.Text = message;
            UpdateButtons();
        }));
        identityTimer.Tick += (_, _) => UpdateButtons();
        identityTimer.Start();
        UpdateButtons();
    }

    internal void VerifyInitialUi()
    {
        if (!IsVisible || !IsLoaded || ActualWidth < 740 || ActualHeight < 650 ||
            StartButton.IsEnabled || StopButton.IsEnabled || RefreshButton.IsEnabled || !ConnectButton.IsEnabled ||
            Games.Count != 0 || Library.Count != 0 || string.IsNullOrWhiteSpace(LocalAccountLabel.Text))
            throw new InvalidOperationException("Initial WPF state is invalid.");
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Username.Text) || string.IsNullOrEmpty(Password.Password))
        { StatusLabel.Text = "Steam kullanıcı adı ve şifreni gir."; return; }
        var password = Password.Password;
        Password.Clear();
        busy = true; UpdateButtons();
        operation = ConnectAsync(Username.Text.Trim(), password);
        await operation;
        busy = false; UpdateButtons();
        if (!closing && session?.IsConnected == true && AutoStart.IsChecked == true && Games.Count > 0) StartFarming();
    }
    private async Task ConnectAsync(string username, string password)
    {
        using var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        loginTimeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await localPlayer.StopAsync(CancellationToken.None);
            if (session is not null) await session.DisposeAsync();
            Games.Clear(); Library.Clear(); CountLabel.Text = "";
            session = new SteamSession();
            var current = session;
            current.Interrupted += message => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (session != current || closing) return;
                interruption = message;
                farmCancellation?.Cancel();
                StatusLabel.Text = message;
                if (!current.IsConnected) AccountLabel.Text = "Steam bağlantısı kesildi";
                UpdateButtons();
            }));
            StatusLabel.Text = "Steam'e bağlanılıyor. Gerekirse telefonundan giriş onayını ver.";
            await current.LoginAsync(username, password, new WindowAuthenticator(this, loginTimeout.Token), loginTimeout.Token);
            AccountLabel.Text = $"Web hesabı: {current.AccountName} · Steam ID: {current.SteamId}";
            foreach (var game in current.Library) Library.Add(game);
        }
        catch (OperationCanceledException) { StatusLabel.Text = "Bağlantı iptal edildi veya giriş süresi doldu."; await DiscardSessionAsync(); }
        catch (Exception ex) { ShowError(ex); await DiscardSessionAsync(); }
        // Card-page errors must not destroy a successful Steam login or its library.
        if (session?.IsConnected == true && !lifetime.IsCancellationRequested)
            await RefreshWithErrorsAsync();
    }
    private async Task DiscardSessionAsync()
    {
        if (session is not null) await session.DisposeAsync();
        session = null;
        Games.Clear(); Library.Clear(); CountLabel.Text = "";
        AccountLabel.Text = "Web hesabı: bağlı değil";
    }
    private async Task RefreshAsync()
    {
        if (session is null) return;
        StatusLabel.Text = "Oyunların kalan kart hakları kontrol ediliyor…";
        var snapshot = await session.GetCardGamesAsync(lifetime.Token);
        SetGames(snapshot);
        UpdateButtons();
        StatusLabel.Text = Games.Count > 0 ? accountsMatch ? "Hazır. Başlat ile yerel Steam üzerinden topla." : "Kart hakları okundu. Toplama için yerel ve web hesaplarını eşleştir."
            : session.UnresolvedAppIds.Count > 0 ? "Kart hakkı doğrulanabilen oyun bulunamadı. Okunamayan oyunlar var; kart hakları bitmiş sayılmadı."
            : "Kart düşürme hakkı kalan oyun yok.";
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        busy = true; UpdateButtons();
        operation = RefreshWithErrorsAsync();
        await operation;
        busy = false; UpdateButtons();
    }
    private async Task RefreshWithErrorsAsync()
    {
        try { await RefreshAsync(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) { StatusLabel.Text = "Steam kart hakları sorgusu zaman aşımına uğradı. Oyun listen korunuyor; Yenile ile tekrar dene."; Games.Clear(); CountLabel.Text = "Kart hakları henüz doğrulanmadı."; }
        catch (Exception ex) { ShowError(ex); Games.Clear(); CountLabel.Text = "Kart bilgisi güncel değil; yeniden kontrol edin."; }
    }
    private void SetGames(IReadOnlyList<CardGame> games)
    {
        Games.Clear();
        foreach (var game in games) Games.Add(game);
        CountLabel.Text = $"{Library.Count} oyun · {Games.Count} kart hakkı olan oyun · {Games.Sum(g => g.RemainingDrops)} kalan kart hakkı";
        if (session?.UnresolvedAppIds.Count > 0)
            CountLabel.Text += $" · Okunamadı: {session.UnresolvedAppIds.Count} oyun (App ID: {string.Join(", ", session.UnresolvedAppIds.Take(5))}{(session.UnresolvedAppIds.Count > 5 ? ", …" : "")})";
    }
    private void Start_Click(object sender, RoutedEventArgs e) => StartFarming();
    private void StartFarming()
    {
        UpdateButtons();
        if (busy || closing || session?.IsConnected != true || farmTask is { IsCompleted: false }) return;
        if (!accountsMatch) { StatusLabel.Text = "Toplama başlatılmadı: yerel Steam oturumu ile web Steam ID eşleşmeli."; return; }
        farmCancellation?.Dispose();
        farmCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        interruption = null;
        farmTask = FarmAsync(session, farmCancellation.Token);
        UpdateButtons();
    }
    private async Task FarmAsync(SteamSession current, CancellationToken token)
    {
        try
        {
            var progress = new Progress<FarmingStatus>(status =>
            {
                if (closing || token.IsCancellationRequested) return;
                StatusLabel.Text = status.CurrentGame is null ? status.Message : $"{status.CurrentGame.Name} · {status.Message}";
                if (status.Games is not null) SetGames(status.Games);
            });
            var localSession = new LocalFarmingSession(current, localPlayer);
            localSession.EnsureSameAccount();
            await new CardFarmer(localSession).RunAsync(progress, token);
        }
        catch (OperationCanceledException) { StatusLabel.Text = interruption ?? "Kart toplama durduruldu."; }
        catch (Exception ex) { ShowError(ex); }
        finally { _ = Dispatcher.BeginInvoke(new Action(UpdateButtons)); }
    }
    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        farmCancellation?.Cancel();
        if (farmTask is not null) await farmTask;
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        accountsMatch = false;
        try
        {
            var local = localPlayer.ReadIdentity();
            LocalAccountLabel.Text = $"Yerel Steam · Steam ID: {local.SteamId}";
            accountsMatch = session?.IsConnected == true && session.SteamId == local.SteamId;
            MatchLabel.Text = session?.IsConnected != true ? "Kart haklarını okumak için aynı hesabın web girişini yap."
                : accountsMatch ? "Hesaplar eşleşiyor. Oynanıyor durumu yerel Steam üzerinden gönderilecek."
                : "Hesaplar farklı. Toplama engellendi; aynı hesapla yeniden bağlan.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LocalAccountLabel.Text = "Yerel Steam: oturum bulunamadı";
            MatchLabel.Text = "Steam'i bu Windows kullanıcısında açıp çevrimiçi giriş yap. Toplama engellendi.";
        }
        var farming = farmTask is { IsCompleted: false };
        var connected = session?.IsConnected == true;
        ConnectButton.IsEnabled = !closing && !busy && !farming;
        Username.IsEnabled = Password.IsEnabled = ConnectButton.IsEnabled;
        RefreshButton.IsEnabled = !closing && !busy && !farming && connected;
        StartButton.IsEnabled = !closing && !busy && !farming && connected && accountsMatch && Games.Count > 0;
        StopButton.IsEnabled = !closing && farming && farmCancellation?.IsCancellationRequested == false;
    }
    private void ShowError(Exception ex)
    {
        StatusLabel.Text = ex switch
        {
            SteamKit2.Authentication.AuthenticationException => "Steam girişi doğrulanamadı. Bilgilerini ve Steam Guard onayını kontrol et.",
            System.Net.Http.HttpRequestException => "Steam web bağlantısı başarısız. İnternet bağlantını kontrol edip tekrar dene.",
            InvalidDataException => "Kart hakları okunamadı: " + ex.Message + " Oyun listen korunuyor; Yenile ile tekrar deneyebilirsin.",
            IOException or InvalidOperationException => ex.Message,
            _ => $"İşlem tamamlanamadı ({ex.GetType().Name}). Ekrandaki hata türünü paylaşabilirsin."
        };
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closeReady) return;
        e.Cancel = true;
        if (closing) return;
        closing = true; identityTimer.Stop(); UpdateButtons();
        lifetime.Cancel(); farmCancellation?.Cancel();
        try
        {
            if (operation is not null) await operation;
            if (farmTask is not null) await farmTask;
            await localPlayer.DisposeAsync();
            if (session is not null) await session.DisposeAsync();
        }
        finally
        {
            farmCancellation?.Dispose(); lifetime.Dispose(); closeReady = true;
            // Cleanup can finish synchronously. Re-enter Close only after this Closing event returns.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}
