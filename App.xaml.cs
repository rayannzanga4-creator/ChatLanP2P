using System.Windows;
using ChatLan.Services;

namespace ChatLan;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeManager.Apply(Prefs.Current.Dark, false);
        base.OnStartup(e);
    }
}
