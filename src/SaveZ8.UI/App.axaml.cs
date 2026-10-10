using Avalonia;
using Avalonia.Markup.Xaml;

namespace SaveZ8.UI;

public class App : Application
{
    /// <summary>Raised once when the main window is opened (used by --ui-smoke).</summary>
    public static event Action<Views.MainWindow>? OnMainWindowOpened;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = new Views.MainWindow
            {
                DataContext = new ViewModels.MainViewModel()
            };
            main.Opened += (_, _) => OnMainWindowOpened?.Invoke(main);
            desktop.MainWindow = main;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
