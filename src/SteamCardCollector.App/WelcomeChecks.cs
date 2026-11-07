using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SteamCardCollector.Core;

namespace SteamCardCollector.App;

internal static class WelcomeChecks
{
    internal static void Run(MainWindow owner, string testPath)
    {
        var original = CultureInfo.CurrentUICulture.Name;
        try
        {
            var picker = new WelcomeWindow(WelcomePage.Language);
            Check(!picker.Primary.IsEnabled && picker.SelectedLanguage == null);
            picker.Languages.SelectedValue = "tr";
            Check(picker.Primary.IsEnabled && picker.SelectedLanguage == "tr");
            picker.Close();
            foreach (var option in AppLanguage.All)
            {
                var language = option.Code;
                owner.ChangeLanguage(language);
                Check(Equals(owner.SettingsButton.Content, Strings.Settings));
                var prefs = new Preferences(language, LoginGuideSeen: true, DashboardGuideSeen: true);
                prefs.Save(testPath);
                Check(Preferences.Load(testPath) == prefs);
                var settings = new WelcomeWindow(WelcomePage.Settings, language);
                Check(settings.SelectedLanguage == language && settings.ReplayPanel.Visibility == Visibility.Visible);
                Check(language == "en" || Strings.Settings != "Settings");
                settings.Close();
                foreach (var guide in new[] { WelcomePage.LoginGuide, WelcomePage.DashboardGuide })
                {
                    var dialog = new WelcomeWindow(guide) { Owner = owner };
                    Check(!dialog.Previous.IsEnabled);
                    var first = dialog.Heading.Text;
                    Click(dialog.Primary);
                    Check(dialog.Previous.IsEnabled && first != dialog.Heading.Text);
                    Click(dialog.Previous);
                    Check(first == dialog.Heading.Text);
                    Click(dialog.Primary);
                    Click(dialog.Primary);
                    Check(Equals(dialog.Primary.Content, Strings.Done));
                    dialog.Close();
                }
            }
            File.WriteAllText(testPath, "{");
            Check(Preferences.Load(testPath).Language == null);
        }
        finally { App.ApplyLanguage(original); File.Delete(testPath); }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Check(bool valid)
    {
        if (!valid) throw new InvalidOperationException("Language, tutorial or settings check failed.");
    }
}
