using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using WheelConvert.Core;
using WheelConvert.UI;

namespace WheelConvert;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // --designer : ouvre le designer (utilisé par Stream Deck Script Builder, onglet Roues → « Modifier dans WheelConvert »)
        bool openDesigner = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--designer", StringComparison.OrdinalIgnoreCase));

        using var designerSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WheelConvert_OpenDesigner");
        using var mutex = new Mutex(true, @"Local\WheelConvert_SingleInstance", out bool created);
        if (!created)
        {
            // Déjà lancé : on demande à l'instance en cours d'ouvrir son designer, puis on s'arrête
            if (openDesigner)
            {
                AllowSetForegroundWindow(-1); // autorise l'instance en cours à passer au premier plan
                designerSignal.Set();
            }
            return;
        }

        var app = new System.Windows.Application
        {
            ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
        };

        bool firstRun = !AppSettings.Exists;
        if (firstRun) AppSettings.Current.Save();
        WriteAppInfo();

        ExternalActions.Start(); // roues Stream Deck (providers\*.json)

        var wheel = new UI.WheelWindow();
        using var tray = new TrayService(app, wheel);
        wheel.Notify += tray.Notify;

        using var hook = new Core.MouseHook();
        hook.DragStarted += wheel.BeginDragSession;
        hook.ButtonUp += wheel.OnHookButtonUp;
        hook.Moved += wheel.OnHookMove;
        hook.Scroll += wheel.OnHookScroll;
        hook.QuickDown += wheel.BeginQuickSession;
        hook.QuickUp += wheel.OnQuickUp;
        tray.PausedChanged += paused =>
        {
            hook.Enabled = !paused;
            wheel.Paused = paused;
        };

        app.DispatcherUnhandledException += (_, e) =>
        {
            tray.Notify("WheelConvert — erreur", e.Exception.Message, true);
            e.Handled = true;
        };

        // Demandes d'ouverture du designer venant d'une autre instance (--designer)
        var registration = ThreadPool.RegisterWaitForSingleObject(designerSignal,
            (_, _) => app.Dispatcher.InvokeAsync(tray.OpenDesigner), null, Timeout.Infinite, executeOnlyOnce: false);

        wheel.Prewarm();
        wheel.UpdateHotkey();

        if (openDesigner) tray.OpenDesigner();
        else if (firstRun)
            tray.Notify("WheelConvert est prêt",
                "Clic gauche + glisser puis Shift pour convertir un fichier. Shift + clic droit maintenu pour les roues d'actions (molette pour changer). Clic sur l'icône pour le designer.",
                false);

        app.Run();
        registration.Unregister(null);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// Indique aux autres applications (Stream Deck) où se trouve WheelConvert.exe.
    private static void WriteAppInfo()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(Path.Combine(AppSettings.Folder, "app.json"),
                JsonSerializer.Serialize(new { exePath = Environment.ProcessPath, version = "3.0" }));
        }
        catch { }
    }
}
