using System;
using Avalonia;

namespace Notifier
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                // Ensure any startup exception is written to console for diagnostics
                try { Console.Error.WriteLine(ex.ToString()); } catch { }
                throw;
            }
        }

        public static AppBuilder BuildAvaloniaApp()
        {
            // Limit Avalonia to Windows backend only and configure Skia renderer
            return AppBuilder.Configure<App>()
                .UseWin32()
                .UseSkia()
                .LogToTrace();
        }
    }
}
