using System.Windows;
using System.Globalization;
using SteamCardCollector.Core;
using System.IO;
using SteamCardCollector.Steam;
namespace SteamCardCollector.App;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == "--local-worker")
        {
            ApplyLanguage(Environment.GetEnvironmentVariable("SCC_LANGUAGE"));
            Shutdown(LocalSteamWorker.Run(e.Args));
            return;
        }
        if (e.Args.FirstOrDefault() == "--native-self-test" && e.Args.Length == 2)
        {
            try { SteamNativeRuntime.Validate(); File.WriteAllText(e.Args[1], "PASS: x64 native DLL and managed resolver; no game initialized."); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(e.Args[1], "FAIL: " + ex); Shutdown(1); }
            return;
        }
        if (e.Args.Length > 0 && !(e.Args.Length == 2 && e.Args[0] == "--ui-self-test")) { Shutdown(2); return; }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var testing = e.Args.Length == 2;
        var path = testing ? Path.GetFullPath(e.Args[1]) + ".preferences" : Preferences.DefaultPath;
        var prefs = testing ? new Preferences("en") : Preferences.Load(path);
        if (prefs.Language == null)
        {
            var picker = new WelcomeWindow(WelcomePage.Language);
            if (picker.ShowDialog() != true) { Shutdown(); return; }
            prefs = prefs with { Language = picker.SelectedLanguage };
            ApplyLanguage(prefs.Language);
            try { prefs.Save(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(Strings.SaveFailed);
                Shutdown(1);
                return;
            }
        }
        ApplyLanguage(prefs.Language);
        var window = new MainWindow(prefs, path);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        if (!testing) window.ShowInitialGuide();
        if (e.Args.Length == 2)
        {
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    window.VerifyInitialUi();
                    WelcomeChecks.Run(window, path);
                    File.WriteAllText(e.Args[1], "PASS: WPF window rendered; account controls gated; language selection, preferences, settings and both guides checked in every supported language.");
                }
                catch (Exception ex) { File.WriteAllText(e.Args[1], "FAIL: " + ex); Environment.ExitCode = 1; }
                finally { window.Close(); }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }
    internal static void ApplyLanguage(string? language)
    {
        var culture = CultureInfo.GetCultureInfo(AppLanguage.Find(language)?.Code ?? "en");
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
        foreach (var property in typeof(Strings).GetProperties())
            Current.Resources["Text." + property.Name] = (string)property.GetValue(null)!;
    }
}
