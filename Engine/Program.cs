using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ParrotCore
{
    public static class Program
    {
        public const string ShutdownEventName = @"Local\ParrotCoreHyacinthShutdownEvent";

        public static string GetStartupBanner()
        {
            return
@"================================================================================
                 _____                     _                     
                |  __ \                   | |                    
                | |__) |_ _ _ __ _ __ ___ | |_ ___ ___  _ __ ___ 
                |  ___/ _` | '__| '__/ _ \| __/ __/ _ \| '__/ _ \
                | |  | (_| | |  | | | (_) | || (_| (_) | | |  __/
                |_|   \__,_|_|  |_|  \___/ \__\___\___/|_|  \___|
                                                                                              
================================================================================
Engine: ParrotCore 2.3 Hyacinth
Copyright: 2026 Parrotcore ©
Made by: Hnato";
        }

        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Contains("--shutdown") || args.Contains("--stop") || args.Contains("--quit"))
            {
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ShutdownEventName, out var evt))
                    {
                        evt.Set();
                        evt.Dispose();
                    }
                }
                catch { }
                return;
            }

            AppConfig.Load();

            if (args.Contains("--server") || args.Contains("--nogui"))
            {
                RunHeadlessServer(args).GetAwaiter().GetResult();
                return;
            }

            using var mutex = new Mutex(true, "ParrotCoreHyacinthApp", out bool createdNew);
            if (!createdNew)
            {
                return;
            }

            using var shutdownEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ShutdownEventName);

            System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
            var app = new System.Windows.Application();
            var mainWindow = new MainWindow();

            ThreadPool.RegisterWaitForSingleObject(
                shutdownEvent,
                (_, _) =>
                {
                    app.Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        await mainWindow.ExternalInstallerShutdownAsync();
                    }));
                },
                null,
                -1,
                true);

            app.Run(mainWindow);
        }

        private static async Task RunHeadlessServer(string[] args)
        {
            Console.Title = "ParrotCore 2.3 Hyacinth";
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(@"
================================================================================
                 _____                     _                     
                |  __ \                   | |                    
                | |__) |_ _ _ __ _ __ ___ | |_ ___ ___  _ __ ___ 
                |  ___/ _` | '__| '__/ _ \| __/ __/ _ \| '__/ _ \
                | |  | (_| | |  | | | (_) | || (_| (_) | | |  __/
                |_|   \__,_|_|  |_|  \___/ \__\___\___/|_|  \___|
                                                                                              
================================================================================");

            Console.WriteLine("Engine: ParrotCore 2.3 Hyacinth");
            Console.WriteLine("Copyright: 2026 Parrotcore \u00A9");
            Console.WriteLine("Made by: Hnato");
            Console.WriteLine();

            var host = new ServerHost(msg => Console.WriteLine(msg));
            try
            {
                await host.StartAsync();
                Console.WriteLine("\nPress Ctrl+C to stop the engine.\n");
                await Task.Delay(-1);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"\n[ERROR]: {ex.Message}");
                Console.ResetColor();
            }
        }
    }
}
