using System.Windows;
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
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Length == 2)
        {
            window.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    window.VerifyInitialUi();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using (var file = File.Create(e.Args[1] + ".png")) encoder.Save(file);
                    File.WriteAllText(e.Args[1], "PASS: WPF window rendered; initial start/stop/refresh disabled; no account login or game initialized.");
                }
                catch (Exception ex) { File.WriteAllText(e.Args[1], "FAIL: " + ex); Environment.ExitCode = 1; }
                finally { window.Close(); }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }
}
