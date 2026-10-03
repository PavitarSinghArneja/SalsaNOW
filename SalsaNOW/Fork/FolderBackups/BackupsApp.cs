using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SalsaNOW
{
    // Fork-only: the "Backups" app. It opens with SalsaNOW and keeps running in the background, so
    // automatic backups keep happening even if the window is closed (closing only minimizes it).
    //
    // Hooks into the original code: DevToolsInstaller starts it, and one line in Program.cs handles
    // "SalsaNOW.exe --backups <DevTools folder>", which the Backups desktop shortcut runs.
    internal static class BackupsApp
    {
        public const string ShortcutArgument = "--backups";
        private const string ShowEventName = @"Local\SalsaNOW_Backups_Show";

        private static EventWaitHandle _showEvent;
        private static RegisteredWaitHandle _showWait;
        private static BackupsForm _form;
        private static readonly TaskCompletionSource<bool> Closed = new TaskCompletionSource<bool>();

        // Program.cs hook. Returns true when this launch came from the Backups shortcut and was handled.
        public static async Task<bool> HandleCommandLineAsync(string[] args)
        {
            int index = Array.IndexOf(args, ShortcutArgument);
            if (index < 0)
                return false;

            string devRoot = index + 1 < args.Length
                ? args[index + 1]
                : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DevTools");

            ShowWindow(GetConsoleWindow(), 0);

            // Already running inside SalsaNOW: Start just brings that window to the front.
            // Otherwise (SalsaNOW was closed) this process becomes the Backups app until it is exited.
            if (!Start(devRoot))
                return true;

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            await Closed.Task;
            return true;
        }

        // Opens the Backups window on its own UI thread. Returns false if it is already open in another
        // process, after asking that one to show itself.
        public static bool Start(string devRoot)
        {
            bool createdNew;
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out createdNew);
            if (!createdNew)
            {
                showEvent.Set();
                showEvent.Dispose();
                return false;
            }
            _showEvent = showEvent;
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (state, timedOut) =>
            {
                BackupsForm form = _form;
                if (form != null)
                    LoginForm.SafeInvoke(form, form.ShowAndActivate);
            }, null, Timeout.Infinite, false);

            var ready = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    _form = new BackupsForm(devRoot);
                    ready.Set();
                    Application.Run(_form);
                }
                catch (Exception ex)
                {
                    ready.Set();
                    try { File.AppendAllText(Path.Combine(devRoot, "backups.log"), $"[{DateTime.Now:HH:mm:ss}] Backups app crashed: {ex}{Environment.NewLine}"); } catch { }
                }
                finally
                {
                    // Exited: let the shortcut start a fresh copy next time instead of signalling this one
                    _showWait.Unregister(null);
                    _showEvent.Dispose();
                    Closed.TrySetResult(true);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "Backups UI";
            thread.Start();
            ready.Wait();

            return true;
        }

        public static string ExePath
        {
            get { return Process.GetCurrentProcess().MainModule.FileName; }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
