using System.Windows;
using SteamCardCollector.Core;

namespace SteamCardCollector.App;

internal enum WelcomePage { Language, Settings, LoginGuide, DashboardGuide }

public partial class WelcomeWindow : Window
{
    private readonly WelcomePage page;
    private readonly (string Title, string Body)[] steps;
    private int step;
    internal string? SelectedLanguage => English.IsChecked == true ? "en" : Turkish.IsChecked == true ? "tr" : null;

    internal WelcomeWindow(WelcomePage page, string? language = null)
    {
        InitializeComponent();
        this.page = page;
        steps = page == WelcomePage.LoginGuide
            ? [(Strings.LoginSteamTitle, Strings.LoginSteamBody), (Strings.LoginWebTitle, Strings.LoginWebBody), (Strings.LoginGuardTitle, Strings.LoginGuardBody)]
            : [(Strings.DashGamesTitle, Strings.DashGamesBody), (Strings.DashStartTitle, Strings.DashStartBody), (Strings.DashLogTitle, Strings.DashLogBody)];

        if (page is WelcomePage.LoginGuide or WelcomePage.DashboardGuide)
        {
            Title = page == WelcomePage.LoginGuide ? Strings.LoginGuide : Strings.DashboardGuide;
            SkipButton.Visibility = Visibility.Visible;
            Previous.Visibility = Visibility.Visible;
            RenderStep();
            return;
        }

        bool first = page == WelcomePage.Language;
        Title = first ? "Steam Card Collector" : Strings.Settings;
        Heading.Text = first ? "Choose your language / Dil seçin" : Strings.Settings + " · " + Strings.Language;
        Details.Text = first ? "" : Strings.LanguageHint;
        Languages.Visibility = Visibility.Visible;
        ReplayPanel.Visibility = first ? Visibility.Collapsed : Visibility.Visible;
        Primary.Content = first ? "Continue / Devam" : Strings.Save;
        English.IsChecked = language == "en";
        Turkish.IsChecked = language == "tr";
        Primary.IsEnabled = SelectedLanguage != null;
    }

    private void RenderStep()
    {
        Heading.Text = steps[step].Title;
        Details.Text = steps[step].Body;
        Title = (page == WelcomePage.LoginGuide ? Strings.LoginGuide : Strings.DashboardGuide) + $" · {step + 1}/{steps.Length}";
        Previous.IsEnabled = step > 0;
        Primary.Content = step == steps.Length - 1 ? Strings.Done : Strings.Next;
    }

    private void SelectionChanged(object sender, RoutedEventArgs e) => Primary.IsEnabled = SelectedLanguage != null;

    private void Advance(object sender, RoutedEventArgs e)
    {
        if (page is WelcomePage.Language or WelcomePage.Settings)
        {
            if (SelectedLanguage != null) DialogResult = true;
            return;
        }
        if (step == steps.Length - 1) DialogResult = true;
        else { step++; RenderStep(); }
    }

    private void GoBack(object sender, RoutedEventArgs e) { if (step > 0) { step--; RenderStep(); } }
    private void SkipGuide(object sender, RoutedEventArgs e) => DialogResult = true;
    private void ReplayLogin(object sender, RoutedEventArgs e) => ShowGuide(this, WelcomePage.LoginGuide);
    private void ReplayDashboard(object sender, RoutedEventArgs e) => ShowGuide(this, WelcomePage.DashboardGuide);

    internal static bool ShowGuide(Window owner, WelcomePage guide) =>
        new WelcomeWindow(guide) { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false }.ShowDialog() == true;
}
