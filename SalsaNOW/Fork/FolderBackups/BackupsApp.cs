using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SalsaNOW
{
    // Fork-only: the "Backups" app. It runs as its own process, "SalsaNOW.exe --backups <DevTools folder>",
    // started by DevToolsInstaller when SalsaNOW starts and by the Backups desktop shortcut. It keeps running in
    // the background, so automatic backups keep happening even if the window is closed (closing only minimizes it).
    //
    // Hook into the original code: one line in Program.cs hands that command line to HandleCommandLineAsync.
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
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            // Already running: Start just brings that window to the front.
            // Otherwise this process becomes the Backups app until it is exited.
            if (!Start(devRoot))
                return true;

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

        // The app's icon, built into the exe from Fork\FolderBackups\Backups.ico
        private const string IconResource = "SalsaNOW.Fork.FolderBackups.Backups.ico";

        public static Icon LoadIcon()
        {
            using (Stream resource = typeof(BackupsApp).Assembly.GetManifestResourceStream(IconResource))
                return resource == null ? null : new Icon(resource);
        }

        // Shortcuts need the icon as a file on disk
        public static string WriteIconFile(string devRoot)
        {
            string path = Path.Combine(devRoot, "Backups.ico");
            using (Stream resource = typeof(BackupsApp).Assembly.GetManifestResourceStream(IconResource))
            {
                if (resource == null)
                    return null;
                using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
                    resource.CopyTo(output);
            }
            return path;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
