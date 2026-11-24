using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WheelConvert.Core;
using WheelConvert.UI;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace WheelConvert;

/// <summary>Icône de la barre des tâches, menu et notifications.</summary>
internal sealed class TrayService : IDisposable
{
    private static readonly Uri IconUri = new("pack://application:,,,/Assets/WheelConvert.ico");

    private readonly System.Windows.Application _app;
    private readonly UI.WheelWindow _wheel;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private UI.SettingsWindow? _settings;

    public event Action<bool>? PausedChanged;

    public TrayService(System.Windows.Application app, UI.WheelWindow wheel)
    {
        _app = app;
        _wheel = wheel;

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "WheelConvert",
            Visible = true
        };

        var menu = new Forms.ContextMenuStrip();

        var designer = menu.Items.Add("Ouvrir le designer…", null, (_, _) => OpenDesigner());
        designer.Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold);
        menu.Items.Add("Roue d'actions   (Ctrl+Alt+Espace)", null, (_, _) => _wheel.ShowQuickWheelAtCursor());
        menu.Items.Add(new Forms.ToolStripSeparator());

        _pauseItem = new Forms.ToolStripMenuItem("Mettre en pause") { CheckOnClick = true };
        _pauseItem.CheckedChanged += (_, _) =>
        {
            _icon.Text = _pauseItem.Checked ? "WheelConvert (en pause)" : "WheelConvert";
            PausedChanged?.Invoke(_pauseItem.Checked);
        };
        menu.Items.Add(_pauseItem);

        _startupItem = new Forms.ToolStripMenuItem("Lancer au démarrage de Windows")
        {
            CheckOnClick = true,
            Checked = StartupManager.IsEnabled()
        };
        _startupItem.CheckedChanged += (_, _) => StartupManager.Set(_startupItem.Checked);
        menu.Items.Add(_startupItem);

        menu.Items.Add("Dossier des paramètres", null,
            (_, _) => Process.Start("explorer.exe", $"\"{AppSettings.Folder}\""));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => _app.Shutdown());

        menu.Opening += (_, _) => _startupItem.Checked = StartupManager.IsEnabled();

        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenDesigner();
        };
    }

    public void OpenDesigner()
    {
        if (_settings is not null)
        {
            // Ramène la fenêtre devant (même si elle était réduite ou derrière une autre application)
            if (_settings.WindowState == System.Windows.WindowState.Minimized)
                _settings.WindowState = System.Windows.WindowState.Normal;
            _settings.Topmost = true;
            _settings.Activate();
            _settings.Topmost = false;
            return;
        }

        _settings = new UI.SettingsWindow();
        _settings.Saved += () => _wheel.UpdateHotkey();
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    public void Notify(string title, string text, bool error)
    {
        if (!error && !AppSettings.Current.Notifications) return;
        if (text.Length > 250) text = text[..247] + "…";
        _icon.ShowBalloonTip(3000, title, text, error ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info);
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        var res = System.Windows.Application.GetResourceStream(IconUri);
        if (res is null) return Drawing.SystemIcons.Application;
        using var stream = res.Stream;
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    /// Icône haute définition pour les fenêtres WPF.
    public static ImageSource LoadWpfIcon()
    {
        var decoder = new IconBitmapDecoder(IconUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
