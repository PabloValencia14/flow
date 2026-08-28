using System.Windows;

namespace Flow.Windows;

public partial class App : System.Windows.Application
{
    public App()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("FlowStyles.xaml", UriKind.RelativeOrAbsolute)
        });
        ThemeManager.ApplyTheme(AppTheme.Dark);
    }
}

public static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new App();
        var window = new MainWindow();
        app.Run(window);
    }
}
