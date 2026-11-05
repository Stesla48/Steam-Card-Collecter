using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private bool qrMode;
    private bool navSync;
    private bool avatarShown;
    private bool hasCountWarning;
    private CancellationTokenSource? qrCancel;
    private CancellationTokenSource? artCancellation;
    private DispatcherTimer? dropTimer;
    private Dictionary<uint, TrackedDrop>? dropBaseline;
    private readonly HashSet<uint> selectedGames = new();
    private readonly HashSet<uint> seenGames = new();
    private readonly Dictionary<uint, ImageSource> art = new();
    private Preferences preferences;
    private readonly string preferencesPath;
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

    public static readonly DependencyProperty ImageRevisionProperty =
        DependencyProperty.Register(nameof(ImageRevision), typeof(int), typeof(MainWindow), new PropertyMetadata(0));
    public int ImageRevision
    {
        get => (int)GetValue(ImageRevisionProperty);
        set => SetValue(ImageRevisionProperty, value);
    }

    public bool IsGameSelected(uint appId) => selectedGames.Contains(appId);
    public ImageSource? ArtFor(uint appId) => art.TryGetValue(appId, out var image) ? image : null;

    internal MainWindow(Preferences preferences, string preferencesPath)
    {
        this.preferences = preferences;
        this.preferencesPath = preferencesPath;
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

    internal void ShowInitialGuide()
    {
        if (!preferences.LoginGuideSeen && WelcomeWindow.ShowGuide(this, WelcomePage.LoginGuide))
            SavePreferences(preferences with { LoginGuideSeen = true });
    }

    private void SavePreferences(Preferences next)
    {
        try { next.Save(preferencesPath); preferences = next; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, Strings.SaveFailed, Strings.Settings); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settings = new WelcomeWindow(WelcomePage.Settings, preferences.Language)
        { Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        if (settings.ShowDialog() == true)
            SavePreferences(preferences with { Language = settings.SelectedLanguage });
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
        { StatusLabel.Text = Strings.EnterYourSteamAccountNameAndPassword; return; }
        var username = Username.Text.Trim();
        busy = true; UpdateButtons();
        operation = ConnectAsync(username);
        await operation;
        if (session?.IsConnected == true) preferLogin = false;
        busy = false; UpdateButtons();
        if (!closing && session?.IsConnected == true && !preferences.DashboardGuideSeen &&
            WelcomeWindow.ShowGuide(this, WelcomePage.DashboardGuide))
            SavePreferences(preferences with { DashboardGuideSeen = true });
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
            Games.Clear(); Library.Clear(); CountLabel.Text = ""; hasCountWarning = false;
            ResetAccountVisuals();
            session = new SteamSession();
            var current = session;
            current.Interrupted += message => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (session != current || closing) return;
                interruption = message;
                farmCancellation?.Cancel();
                StatusLabel.Text = message;
                if (!current.IsConnected) AccountLabel.Text = Strings.SteamDisconnected;
                UpdateButtons();
            }));
            StatusLabel.Text = Strings.ConnectingToSteam;
            var login = current.LoginAsync(username, password, new WindowAuthenticator(this, loginTimeout.Token), loginTimeout.Token);
            password = null;
            await login;
            AccountLabel.Text = string.Format(Strings.WebSteamId, current.SteamId);
            foreach (var game in current.Library) Library.Add(game);
            QueueArt();
        }
        catch (OperationCanceledException) { password = null; StatusLabel.Text = Strings.SigninCanceledOrTimedOut; await DiscardSessionAsync(); }
        catch (Exception ex) { password = null; ShowError(ex); await DiscardSessionAsync(); }
        // Card-page errors must not destroy a successful Steam login or its library.
        if (session?.IsConnected == true && !lifetime.IsCancellationRequested)
            await RefreshWithErrorsAsync();
    }
    private async Task DiscardSessionAsync()
    {
        if (session is not null) await session.DisposeAsync();
        session = null;
        Games.Clear(); Library.Clear(); CountLabel.Text = ""; hasCountWarning = false;
        ResetAccountVisuals();
        AccountLabel.Text = Strings.WebAccountDisconnected;
    }
    private async Task RefreshAsync()
    {
        if (session is null) return;
        StatusLabel.Text = Strings.CheckingRemainingCardDrops;
        var snapshot = await session.GetCardGamesAsync(lifetime.Token);
        SetGames(snapshot);
        UpdateButtons();
        StatusLabel.Text = Games.Count > 0 ? accountsMatch ? Strings.ReadyToStart : Strings.MatchAccountsToCollect
            : session.UnresolvedAppIds.Count > 0 ? Strings.NoVerifiedEligibleGames
            : Strings.NoGamesHaveCardDropsRemaining;
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
        catch (OperationCanceledException) { StatusLabel.Text = Strings.CardQueryTimedOut; Games.Clear(); hasCountWarning = true; CountLabel.Text = Strings.CardCountsAreUnverified; }
        catch (Exception ex) { ShowError(ex); Games.Clear(); hasCountWarning = true; CountLabel.Text = Strings.CardCountsOutOfDate; }
    }
    private void SetGames(IReadOnlyList<CardGame> games)
    {
        SyncSelection(games);
        Games.Clear();
        foreach (var game in games) Games.Add(game);
        QueueArt();
        CountLabel.Text = string.Format(Strings.GamesEligibleRemainingDrops, Library.Count, Games.Count, Games.Sum(g => g.RemainingDrops));
        hasCountWarning = session?.UnresolvedAppIds.Count > 0;
        if (session is { UnresolvedAppIds.Count: > 0 })
            CountLabel.Text += string.Format(Strings.UnknownGamesAppId, session.UnresolvedAppIds.Count,
                string.Join(", ", session.UnresolvedAppIds.Take(5)) + (session.UnresolvedAppIds.Count > 5 ? ", …" : ""));
        UpdateStats();
    }
    private void Start_Click(object sender, RoutedEventArgs e) => StartFarming();
    private void StartFarming()
    {
        UpdateButtons();
        if (busy || closing || session?.IsConnected != true || farmTask is { IsCompleted: false }) return;
        if (!accountsMatch) { StatusLabel.Text = Strings.MatchingIdsRequired; return; }
        var selected = selectedGames.ToHashSet();
        if (!Games.Any(game => selected.Contains(game.AppId))) { StatusLabel.Text = Strings.SelectAtLeastOneGame; return; }
        farmCancellation?.Dispose();
        farmCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        interruption = null;
        dropBaseline = Games.ToDictionary(game => game.AppId, game => new TrackedDrop(game.Name, game.RemainingDrops));
        farmTask = FarmAsync(session, selected, farmCancellation.Token);
        UpdateButtons();
    }
    private async Task FarmAsync(SteamSession current, HashSet<uint> selected, CancellationToken token)
    {
        try
        {
            var progress = new Progress<FarmingStatus>(status =>
            {
                if (closing || token.IsCancellationRequested) return;
                string? dropText = null;
                if (status.Games is not null)
                {
                    dropText = DescribeDrops(status.Games);
                    SetGames(status.Games);
                }
                StatusLabel.Text = dropText ?? (status.CurrentGame is null ? status.Message : $"{status.CurrentGame.Name} · {status.Message}");
                if (dropText is not null) ShowDropBanner(dropText);
                if (status.CurrentGame is { } game) CurrentFarmAppId = game.AppId;
                else ClearValue(CurrentFarmAppIdProperty);
            });
            var localSession = new LocalFarmingSession(current, localPlayer);
            localSession.EnsureSameAccount();
            ISteamSession farming = localSession;
            if (selected.Count != Games.Count) farming = new GameSelectionSession(localSession, selected);
            await new CardFarmer(farming).RunAsync(progress, token);
        }
        catch (OperationCanceledException) { StatusLabel.Text = interruption ?? Strings.CollectionStopped; }
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
            DashboardLocalId.Text = string.Format(Strings.LocalSteamIdLabel, local.SteamId);
            LocalChipTitle.Text = Strings.LocalSteamConnected;
            LocalChipDot.Fill = OnlineDot;
            accountsMatch = session?.IsConnected == true && session.SteamId == local.SteamId;
            MatchLabel.Text = session?.IsConnected != true ? Strings.SignInToTheSameWebAccount
                : accountsMatch ? Strings.PresenceAccountVerified
                : Strings.ReconnectWithMatchingAccount;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LocalAccountLabel.Text = Strings.NoSessionFound;
            DashboardLocalId.Text = Strings.LocalSteamIdNoSession;
            LocalChipTitle.Text = Strings.LocalSteamOffline;
            LocalChipDot.Fill = OfflineDot;
            MatchLabel.Text = Strings.LocalSteamRequired;
        }
        var farming = farmTask is { IsCompleted: false };
        var connected = session?.IsConnected == true;
        SettingsButton.IsEnabled = !closing && !busy;
        ConnectButton.IsEnabled = !closing && !busy && !farming;
        Username.IsEnabled = Password.IsEnabled = ConnectButton.IsEnabled;
        RefreshButton.IsEnabled = !closing && !busy && !farming && connected;
        var anySelected = Games.Any(game => selectedGames.Contains(game.AppId));
        StartButton.IsEnabled = !closing && !busy && !farming && connected && accountsMatch && anySelected;
        StopButton.IsEnabled = !closing && farming && farmCancellation?.IsCancellationRequested == false;
        IsFarming = farming;
        if (!farming) ClearValue(CurrentFarmAppIdProperty);
        if (!connected) preferLogin = false;
        var showDashboard = connected && !preferLogin;
        LoginView.Visibility = showDashboard ? Visibility.Collapsed : Visibility.Visible;
        DashboardView.Visibility = showDashboard ? Visibility.Visible : Visibility.Collapsed;
        ReturnButton.Visibility = connected && preferLogin ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.IsDefault = !showDashboard && !qrMode;
        if (QrButton is not null) QrButton.IsEnabled = !closing && !busy && !farming;
        if (QrCancelButton is not null) QrCancelButton.IsEnabled = busy;
        ReturnButton.IsEnabled = !busy && !farming;
        SwitchAccountButton.IsEnabled = connected && !closing && !busy && !farming;
        SessionTitle.Text = accountsMatch ? Strings.ReadyToCollect : connected ? Strings.AccountsDiffer : Strings.Session;
        if (connected && accountsMatch)
        {
            MatchBadge.Text = Strings.AccountsMatch;
            MatchBadge.Foreground = MatchGreen;
            MatchDot.Fill = OnlineDot;
            MatchDot.Visibility = Visibility.Visible;
        }
        else if (connected)
        {
            MatchBadge.Text = Strings.AccountsDiffer;
            MatchBadge.Foreground = MatchAmber;
            MatchDot.Fill = MatchAmber;
            MatchDot.Visibility = Visibility.Visible;
        }
        else
        {
            MatchBadge.Text = "";
            MatchDot.Visibility = Visibility.Collapsed;
        }
        var account = string.IsNullOrWhiteSpace(session?.PersonaName) ? session?.AccountName : session.PersonaName;
        SidebarName.Text = string.IsNullOrWhiteSpace(account) ? "Steam" : account;
        ShowAvatar(connected);
        SidebarPresence.Text = !connected ? Strings.Offline : accountsMatch ? Strings.Online : Strings.Connected;
        var trimmed = SidebarName.Text.Trim();
        AvatarLetter.Text = trimmed.Length == 0 ? "?" : trimmed[..1].ToUpper(CultureInfo.CurrentUICulture);
        UpdateStats();
    }
    private void UpdateStats()
    {
        LibraryStat.Text = Library.Count.ToString();
        CardStat.Text = Games.Count.ToString();
        DropStat.Text = Games.Sum(static g => g.RemainingDrops).ToString();
        GamesEmpty.Visibility = Games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmpty.Visibility = Library.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountLabel.Visibility = hasCountWarning ? Visibility.Visible : Visibility.Collapsed;
    }
    private void MirrorStatus(bool log)
    {
        var text = StatusLabel.Text ?? "";
        if (DashboardStatus is not null) DashboardStatus.Text = text;
        if (QrHint is not null && qrMode) QrHint.Text = text;
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
            SteamKit2.Authentication.AuthenticationException => Strings.CheckCredentialsAndGuard,
            System.Net.Http.HttpRequestException => Strings.CheckInternetConnection,
            InvalidDataException => Strings.CouldNotReadCardCounts + ex.Message + Strings.RefreshRetryHint,
            IOException or InvalidOperationException => ex.Message,
            _ => string.Format(Strings.OperationFailed, ex.GetType().Name)
        };
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closeReady) return;
        e.Cancel = true;
        if (closing) return;
        closing = true; identityTimer.Stop(); artCancellation?.Cancel(); DismissDrop(); UpdateButtons();
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

    private void SyncSelection(IReadOnlyList<CardGame> games)
    {
        var ids = games.Select(game => game.AppId).ToHashSet();
        foreach (var id in ids)
            if (seenGames.Add(id)) selectedGames.Add(id);
        selectedGames.RemoveWhere(id => !ids.Contains(id));
    }

    private string? DescribeDrops(IReadOnlyList<CardGame> games)
    {
        var previous = dropBaseline;
        dropBaseline = games.ToDictionary(game => game.AppId, game => new TrackedDrop(game.Name, game.RemainingDrops));
        if (previous is null) return null;
        var text = DropEvents.Format(DropEvents.Find(previous, games));
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private void ShowDropBanner(string message)
    {
        DropBannerText.Text = message;
        DropBanner.Visibility = Visibility.Visible;
        dropTimer?.Stop();
        dropTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        dropTimer.Tick += (_, _) => DismissDrop();
        dropTimer.Start();
        DropBalloon.Show(new WindowInteropHelper(this).Handle, message);
    }

    private void DismissDrop()
    {
        dropTimer?.Stop();
        DropBanner.Visibility = Visibility.Collapsed;
        DropBalloon.Hide(new WindowInteropHelper(this).Handle);
    }

    private void DismissDrop_Click(object sender, RoutedEventArgs e) => DismissDrop();

    private void ShowAvatar(bool connected)
    {
        if (!connected || session?.AvatarBytes is not { Length: > 0 } bytes)
        {
            if (!connected)
            {
                avatarShown = false;
                AvatarImage.Source = null;
                AvatarImage.Visibility = Visibility.Collapsed;
                AvatarLetter.Visibility = Visibility.Visible;
            }
            return;
        }
        if (avatarShown) return;
        var image = BitmapFrom(bytes);
        if (image is null) return;
        AvatarImage.Source = image;
        AvatarImage.Visibility = Visibility.Visible;
        AvatarLetter.Visibility = Visibility.Collapsed;
        avatarShown = true;
    }

    private void ResetAccountVisuals()
    {
        artCancellation?.Cancel();
        art.Clear();
        ImageRevision++;
        seenGames.Clear();
        selectedGames.Clear();
        avatarShown = false;
        AvatarImage.Source = null;
        AvatarImage.Visibility = Visibility.Collapsed;
        AvatarLetter.Visibility = Visibility.Visible;
        dropBaseline = null;
        DismissDrop();
    }

    private void QueueArt()
    {
        var ids = Library.Select(game => game.AppId).Concat(Games.Select(game => game.AppId)).Distinct().Where(id => !art.ContainsKey(id)).ToArray();
        if (ids.Length == 0) return;
        artCancellation?.Cancel();
        artCancellation = new CancellationTokenSource();
        _ = LoadArtAsync(ids, artCancellation.Token);
    }

    private async Task LoadArtAsync(uint[] ids, CancellationToken token)
    {
        var gate = new SemaphoreSlim(4);
        try
        {
            await Task.WhenAll(ids.Select(async id =>
            {
                await gate.WaitAsync(token);
                try
                {
                    var bytes = await SteamImages.TryHeaderAsync(id, token);
                    if (bytes is not { Length: > 0 } || token.IsCancellationRequested) return;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var image = BitmapFrom(bytes);
                        if (image is null) return;
                        art[id] = image;
                        ImageRevision++;
                    });
                }
                catch (OperationCanceledException) { }
                finally { gate.Release(); }
            }));
        }
        catch (OperationCanceledException) { }
        finally { gate.Dispose(); }
    }

    private static BitmapImage? BitmapFrom(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (NotSupportedException) { return null; }
        catch (IOException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private void ShowPassword_Click(object sender, RoutedEventArgs e)
    {
        qrCancel?.Cancel();
        qrMode = false;
        QrImage.Source = null;
        QrLogin.Visibility = Visibility.Collapsed;
        PasswordLogin.Visibility = Visibility.Visible;
        PasswordActions.Visibility = Visibility.Visible;
        UpdateButtons();
    }

    private async void ShowQr_Click(object sender, RoutedEventArgs e)
    {
        if (busy || closing) return;
        Password.Clear();
        qrMode = true;
        PasswordLogin.Visibility = Visibility.Collapsed;
        PasswordActions.Visibility = Visibility.Collapsed;
        QrLogin.Visibility = Visibility.Visible;
        QrImage.Source = null;
        qrCancel = new CancellationTokenSource();
        busy = true;
        UpdateButtons();
        try
        {
            operation = QrConnectAsync(qrCancel.Token);
            await operation;
            if (session?.IsConnected == true) preferLogin = false;
        }
        finally
        {
            qrCancel.Dispose();
            qrCancel = null;
            QrImage.Source = null;
            busy = false;
            UpdateButtons();
            if (!closing && session?.IsConnected == true && AutoStart.IsChecked == true && Games.Any(game => selectedGames.Contains(game.AppId))) StartFarming();
        }
    }

    private void QrCancel_Click(object sender, RoutedEventArgs e) => qrCancel?.Cancel();

    private async Task QrConnectAsync(CancellationToken cancellationToken)
    {
        await localPlayer.StopAsync(CancellationToken.None);
        if (session is not null) await session.DisposeAsync();
        Games.Clear(); Library.Clear(); CountLabel.Text = "";
        ResetAccountVisuals();
        session = new SteamSession();
        var current = session;
        current.Interrupted += message => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (session != current || closing) return;
            interruption = message;
            farmCancellation?.Cancel();
            StatusLabel.Text = message;
            if (!current.IsConnected) AccountLabel.Text = Strings.SteamDisconnected;
            UpdateButtons();
        }));
        StatusLabel.Text = Strings.QrPreparing;
        var progress = new Progress<string>(ShowQrCode);
        try
        {
            await current.LoginWithQrAsync(progress, cancellationToken);
            AccountLabel.Text = string.Format(Strings.WebSteamId, current.SteamId);
            foreach (var game in current.Library) Library.Add(game);
            QueueArt();
            if (session?.IsConnected == true && !lifetime.IsCancellationRequested)
                await RefreshWithErrorsAsync();
        }
        catch (OperationCanceledException) { StatusLabel.Text = Strings.QrCanceled; await DiscardSessionAsync(); }
        catch (Exception ex) { ShowError(ex); await DiscardSessionAsync(); }
    }

    private void ShowQrCode(string url)
    {
        if (url.Length is 0 or > 300 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            StatusLabel.Text = Strings.QrUnavailable;
            return;
        }
        try { QrImage.Source = BitmapFrom(QrCodes.Png(url)); }
        catch (Exception)
        {
            StatusLabel.Text = Strings.QrDrawFailed;
            return;
        }
        StatusLabel.Text = Strings.QrReady;
    }

    private void GameSelection_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || box.Tag is not uint id) return;
        if (box.IsChecked == true) selectedGames.Add(id);
        else selectedGames.Remove(id);
        if (IsLoaded) UpdateButtons();
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
        qrCancel?.Cancel();
        qrMode = false;
        QrLogin.Visibility = Visibility.Collapsed;
        PasswordLogin.Visibility = Visibility.Visible;
        PasswordActions.Visibility = Visibility.Visible;
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
