using System;
using Velopack;

namespace HelmetDesk
{
    /// <summary>
    /// Own entry point (App.xaml is built as a Page, so WPF does not generate Main).
    /// Velopack must run before anything else: during install / update / uninstall the installer starts the exe
    /// with special arguments, Run() handles them and exits the process before any window exists.
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            VelopackApp.Build()
                // fast hooks: must finish in a few seconds and never show UI
                .OnBeforeUninstallFastCallback(_ => App.RemoveStartupRegistration())
                // all-users install (Program Files): applying needs UAC, so a downloaded update is only applied when the
                // user clicks "Reiniciar e atualizar" — never by surprise at startup
                .SetAutoApplyOnStartup(!InstallMode.NeedsAdmin)
                .Run();

            var app = new App();
            app.InitializeComponent(); // App.xaml resources (converters); the theme and fonts are merged in OnStartup
            app.Run();
        }
    }
}
