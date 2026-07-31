using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

class Program
{
    // Opens Notifier.SettingWindow and exercises Play/Pause button.
    static int Main()
    {
        string assemblyPath = System.IO.Path.GetFullPath(".\\bin\\Release\\net10.0-windows10.0.22621.0\\Notifier.dll");
        if (!System.IO.File.Exists(assemblyPath))
        {
            Console.WriteLine($"Notifier.dll not found at {assemblyPath}");
            return 2;
        }

        Exception? threadEx = null;
        var t = new Thread(() =>
        {
            try
            {
                var asm = Assembly.LoadFrom(assemblyPath);
                var type = asm.GetType("Notifier.SettingWindow");
                if (type == null) throw new Exception("Type Notifier.SettingWindow not found.");

                var app = new Application();
                // Create instance
                var winObj = Activator.CreateInstance(type);
                if (winObj == null) throw new Exception("Failed to create SettingWindow instance.");

                var win = winObj as Window ?? throw new Exception("Instance is not Window.");

                win.Loaded += (_, __) => Console.WriteLine("SettingWindow loaded.");

                win.Show();

                // Allow UI to settle
                win.Dispatcher.InvokeAsync(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(500);

                    // Find PlayPause button and icons via reflection
                    var btnField = type.GetField("BtnPlayPause", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var playPathField = type.GetField("PlayPausePlay", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var pausePathField = type.GetField("PlayPausePause", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (btnField == null) Console.WriteLine("BtnPlayPause field not found.");
                    if (playPathField == null) Console.WriteLine("PlayPausePlay field not found.");
                    if (pausePathField == null) Console.WriteLine("PlayPausePause field not found.");

                    var btn = btnField?.GetValue(win) as Button;
                    var playPath = playPathField?.GetValue(win) as System.Windows.Shapes.Path;
                    var pausePath = pausePathField?.GetValue(win) as System.Windows.Shapes.Path;

                    Console.WriteLine($"Initial Opacity - Play: {playPath?.Opacity}, Pause: {pausePath?.Opacity}");

                    if (btn == null)
                    {
                        Console.WriteLine("PlayPause button not found; cannot click.");
                    }
                    else
                    {
                        // Click once, then manually toggle PlayPauseIcon.Text to simulate SMTC updates
                        Console.WriteLine("Clicking PlayPause (simulate)...");
                        btn.Dispatcher.Invoke(() => btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                        await System.Threading.Tasks.Task.Delay(300);
                        double? pO1 = playPath != null ? (double?)playPath.Dispatcher.Invoke(() => playPath.Opacity) : null;
                                                double? paO1 = pausePath != null ? (double?)pausePath.Dispatcher.Invoke(() => pausePath.Opacity) : null;
                        Console.WriteLine($"After click - Play Opacity={pO1}, Pause Opacity={paO1}");

                        // Now simulate SMTC updating PlayPauseIcon.Text to Play
                        var playPauseField = type.GetField("PlayPauseIcon", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (playPauseField != null)
                        {
                            var textBlock = playPauseField.GetValue(win) as TextBlock;
                            if (textBlock != null)
                            {
                                                                // Try converter resource
                                                                object convObj = null;
                                                                if (win.Resources.Contains("PlayPauseConverter")) convObj = win.Resources["PlayPauseConverter"];
                                                                else if (Application.Current != null && Application.Current.Resources.Contains("PlayPauseConverter")) convObj = Application.Current.Resources["PlayPauseConverter"];
                                                                Console.WriteLine($"Converter instance found: {convObj != null}");
                                                                if (convObj is System.Windows.Data.IValueConverter c)
                                                                {
                                                                    var resPlay = c.Convert("▶️", typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);
                                                                    var resPause = c.Convert("⏸", typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);
                                                                    Console.WriteLine($"Converter('▶️')->{resPlay}, Converter('⏸')->{resPause}");
                                                                }

                                                                Console.WriteLine("Simulating Play state (setting PlayPauseIcon.Text = '▶️')");
                                                                textBlock.Dispatcher.Invoke(() => textBlock.Text = "▶️");
                                                                await System.Threading.Tasks.Task.Delay(200);
                                                                var pO2 = playPath != null ? (double?)playPath.Dispatcher.Invoke(() => playPath.Opacity) : null;
                                                                var paO2 = pausePath != null ? (double?)pausePath.Dispatcher.Invoke(() => pausePath.Opacity) : null;
                                                                Console.WriteLine($"After sim Play - Play Opacity={pO2}, Pause Opacity={paO2}");

                                                                Console.WriteLine("Simulating Pause state (setting PlayPauseIcon.Text = '⏸')");
                                                                textBlock.Dispatcher.Invoke(() => textBlock.Text = "⏸");
                                                                await System.Threading.Tasks.Task.Delay(300);
                                                                var pO3 = playPath != null ? (double?)playPath.Dispatcher.Invoke(() => playPath.Opacity) : null;
                                                                var paO3 = pausePath != null ? (double?)pausePath.Dispatcher.Invoke(() => pausePath.Opacity) : null;
                                                                Console.WriteLine($"After sim Pause - Play Opacity={pO3}, Pause Opacity={paO3}");
                            }
                            else Console.WriteLine("PlayPauseIcon TextBlock not found or null.");
                        }
                        else Console.WriteLine("PlayPauseIcon field not found.");
                    }

                    // Wait briefly so user can see
                    await System.Threading.Tasks.Task.Delay(800);

                    // Close window and shutdown app
                    win.Dispatcher.Invoke(() => { win.Close(); Application.Current?.Shutdown(); });
                }, DispatcherPriority.Background);

                // Start WPF message loop
                app.Run();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();

        if (threadEx != null)
        {
            Console.WriteLine("Test runner thread error: " + threadEx);
            return 1;
        }

        Console.WriteLine("Test run complete.");
        return 0;
    }
}
