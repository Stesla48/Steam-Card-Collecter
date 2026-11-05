using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SteamCardCollector.Core;
using SteamCardCollector.Steam;

namespace SteamCardCollector.App;

public partial class MainWindow : Window
{
    public ObservableCollection<CardGame> Games { get; } = new();
    public ObservableCollection<OwnedGame> Library { get; } = new();
    public ObservableCollection<string> StatusLog { get; } = new();
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
    private bool preferLogin;
    private bool navSync;
    private static readonly CultureInfo Turkish = new("tr-TR");
    private static readonly SolidColorBrush OnlineDot = Freeze(Color.FromRgb(0x7D, 0xCB, 0x3B));
    private static readonly SolidColorBrush OfflineDot = Freeze(Color.FromRgb(0x6A, 0x73, 0x7C));
    private static readonly SolidColorBrush MatchGreen = Freeze(Color.FromRgb(0xA4, 0xD0, 0x07));
    private static readonly SolidColorBrush MatchAmber = Freeze(Color.FromRgb(0xE0, 0xA1, 0x4A));

    public static readonly DependencyProperty IsFarmingProperty =
        DependencyProperty.Register(nameof(IsFarming), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public bool IsFarming
    {
        get => (bool)GetValue(IsFarmingProperty);
        set => SetValue(IsFarmingProperty, value);
    }

    public static readonly DependencyProperty CurrentFarmAppIdProperty =
        DependencyProperty.Register(nameof(CurrentFarmAppId), typeof(uint?), typeof(MainWindow), new PropertyMetadata(null));
    public uint? CurrentFarmAppId
    {
        get => (uint?)GetValue(CurrentFarmAppIdProperty);
        set => SetValue(CurrentFarmAppIdProperty, value);
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .AddValueChanged(StatusLabel, (_, _) => MirrorStatus(log: true));
        MirrorStatus(log: false);
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

    internal void SetGuardOverlay(bool visible)
    {
        GuardOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
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
        if (string.IsNullOrWhiteSpace(Username.Text) || Password.Password.Length == 0)
        { StatusLabel.Text = "Steam kullanıcı adı ve şifreni gir."; return; }
        var username = Username.Text.Trim();
        busy = true; UpdateButtons();
        operation = ConnectAsync(username);
        await operation;
        if (session?.IsConnected == true) preferLogin = false;
        busy = false; UpdateButtons();
        if (!closing && session?.IsConnected == true && AutoStart.IsChecked == true && Games.Count > 0) StartFarming();
    }
    private async Task ConnectAsync(string username)
    {
        using var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        loginTimeout.CancelAfter(TimeSpan.FromMinutes(5));
        string? password = Password.Password;
        Password.Clear();
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
            StatusLabel.Text = "Steam'e bağlanılıyor…";
            var login = current.LoginAsync(username, password, new WindowAuthenticator(this, loginTimeout.Token), loginTimeout.Token);
            password = null;
            await login;
            AccountLabel.Text = $"Web Steam ID: {current.SteamId}";
            foreach (var game in current.Library) Library.Add(game);
        }
        catch (OperationCanceledException) { password = null; StatusLabel.Text = "Bağlantı iptal edildi veya giriş süresi doldu."; await DiscardSessionAsync(); }
        catch (Exception ex) { password = null; ShowError(ex); await DiscardSessionAsync(); }
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
        UpdateStats();
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
                if (status.CurrentGame is { } game) CurrentFarmAppId = game.AppId;
                else ClearValue(CurrentFarmAppIdProperty);
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
            LocalAccountLabel.Text = $"Steam ID: {local.SteamId}";
            DashboardLocalId.Text = $"Yerel Steam ID: {local.SteamId}";
            LocalChipTitle.Text = "Yerel Steam bağlı";
            LocalChipDot.Fill = OnlineDot;
            accountsMatch = session?.IsConnected == true && session.SteamId == local.SteamId;
            MatchLabel.Text = session?.IsConnected != true ? "Kart haklarını okumak için aynı hesabın web girişini yap."
                : accountsMatch ? "Hesaplar eşleşiyor. Oynanıyor durumu yerel Steam üzerinden gönderilecek."
                : "Hesaplar farklı. Toplama engellendi; aynı hesapla yeniden bağlan.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LocalAccountLabel.Text = "Oturum bulunamadı";
            DashboardLocalId.Text = "Yerel Steam ID: oturum yok";
            LocalChipTitle.Text = "Yerel Steam kapalı";
            LocalChipDot.Fill = OfflineDot;
            MatchLabel.Text = "Steam'i bu Windows kullanıcısında açıp çevrimiçi giriş yap. Toplama engellendi.";
        }
        var farming = farmTask is { IsCompleted: false };
        var connected = session?.IsConnected == true;
        ConnectButton.IsEnabled = !closing && !busy && !farming;
        Username.IsEnabled = Password.IsEnabled = ConnectButton.IsEnabled;
        RefreshButton.IsEnabled = !closing && !busy && !farming && connected;
        StartButton.IsEnabled = !closing && !busy && !farming && connected && accountsMatch && Games.Count > 0;
        StopButton.IsEnabled = !closing && farming && farmCancellation?.IsCancellationRequested == false;
        IsFarming = farming;
        if (!farming) ClearValue(CurrentFarmAppIdProperty);
        if (!connected) preferLogin = false;
        var showDashboard = connected && !preferLogin;
        LoginView.Visibility = showDashboard ? Visibility.Collapsed : Visibility.Visible;
        DashboardView.Visibility = showDashboard ? Visibility.Visible : Visibility.Collapsed;
        ReturnButton.Visibility = connected && preferLogin ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.IsDefault = !showDashboard;
        ReturnButton.IsEnabled = !busy && !farming;
        SwitchAccountButton.IsEnabled = connected && !closing && !busy && !farming;
        SessionTitle.Text = accountsMatch ? "Kart toplama hazır" : connected ? "Hesaplar eşleşmiyor" : "Oturum";
        if (connected && accountsMatch)
        {
            MatchBadge.Text = "Hesaplar eşleşiyor";
            MatchBadge.Foreground = MatchGreen;
            MatchDot.Fill = OnlineDot;
            MatchDot.Visibility = Visibility.Visible;
        }
        else if (connected)
        {
            MatchBadge.Text = "Hesaplar eşleşmiyor";
            MatchBadge.Foreground = MatchAmber;
            MatchDot.Fill = MatchAmber;
            MatchDot.Visibility = Visibility.Visible;
        }
        else
        {
            MatchBadge.Text = "";
            MatchDot.Visibility = Visibility.Collapsed;
        }
        var account = session?.AccountName;
        SidebarName.Text = string.IsNullOrWhiteSpace(account) ? "Steam" : account;
        SidebarPresence.Text = !connected ? "çevrimdışı" : accountsMatch ? "çevrimiçi" : "bağlı";
        var trimmed = SidebarName.Text.Trim();
        AvatarLetter.Text = trimmed.Length == 0 ? "?" : trimmed[..1].ToUpper(Turkish);
        UpdateStats();
    }
    private void UpdateStats()
    {
        LibraryStat.Text = Library.Count.ToString();
        CardStat.Text = Games.Count.ToString();
        DropStat.Text = Games.Sum(static g => g.RemainingDrops).ToString();
        GamesEmpty.Visibility = Games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmpty.Visibility = Library.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var text = CountLabel.Text ?? "";
        var warning = text.Contains("Okunamadı", StringComparison.Ordinal)
            || text.Contains("doğrulanmadı", StringComparison.Ordinal)
            || text.Contains("güncel değil", StringComparison.Ordinal);
        CountLabel.Visibility = warning ? Visibility.Visible : Visibility.Collapsed;
    }
    private void MirrorStatus(bool log)
    {
        var text = StatusLabel.Text ?? "";
        if (DashboardStatus is not null) DashboardStatus.Text = text;
        if (!log || string.IsNullOrWhiteSpace(text)) return;
        if (StatusLog.Count > 0)
        {
            var last = StatusLog[^1];
            var split = last.IndexOf("  ", StringComparison.Ordinal);
            var previous = split >= 0 ? last[(split + 2)..] : last;
            if (previous == text) return;
        }
        StatusLog.Add($"{DateTime.Now:HH:mm:ss}  {text}");
        while (StatusLog.Count > 300) StatusLog.RemoveAt(0);
        if (LogEmpty is not null) LogEmpty.Visibility = Visibility.Collapsed;
        if (LogList is { Items.Count: > 0 }) LogList.ScrollIntoView(LogList.Items[^1]);
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

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeGlyph is null) return;
        MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }
    private void SwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        preferLogin = true;
        UpdateButtons();
        Username.Focus();
    }
    private void ReturnToDashboard_Click(object sender, RoutedEventArgs e)
    {
        preferLogin = false;
        UpdateButtons();
    }
    private void DashboardNav_Checked(object sender, RoutedEventArgs e)
    {
        if (navSync || sender is not RadioButton { Tag: string page } || DropsView is null) return;
        ShowDashboardPage(page);
    }
    private void ShowDashboardPage(string page)
    {
        if (DropsView is null || LibraryView is null || LogView is null || ContentTabs is null ||
            NavDrops is null || NavLibrary is null || NavLog is null || TabDrops is null || TabLibrary is null)
            return;
        navSync = true;
        try
        {
            DropsView.Visibility = page == "drops" ? Visibility.Visible : Visibility.Collapsed;
            LibraryView.Visibility = page == "library" ? Visibility.Visible : Visibility.Collapsed;
            LogView.Visibility = page == "log" ? Visibility.Visible : Visibility.Collapsed;
            ContentTabs.Visibility = page == "log" ? Visibility.Collapsed : Visibility.Visible;
            NavDrops.IsChecked = page == "drops";
            NavLibrary.IsChecked = page == "library";
            NavLog.IsChecked = page == "log";
            TabDrops.IsChecked = page == "drops";
            TabLibrary.IsChecked = page == "library";
        }
        finally { navSync = false; }
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
