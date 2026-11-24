using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace WheelConvert.Core;

public enum BrowseKind { None, File, Folder }

public static class QuickActionRunner
{
    public static IReadOnlyList<KeyValuePair<QuickActionType, string>> TypeNames { get; } = new List<KeyValuePair<QuickActionType, string>>
    {
        new(QuickActionType.OpenApp, "Ouvrir une application ou un fichier"),
        new(QuickActionType.OpenFolder, "Ouvrir un dossier"),
        new(QuickActionType.OpenUrl, "Ouvrir une adresse (site, ms-settings:…)"),
        new(QuickActionType.RunCommand, "Commande (cmd)"),
        new(QuickActionType.PowerShell, "Script PowerShell"),
        new(QuickActionType.SendKeys, "Raccourci clavier"),
        new(QuickActionType.PasteText, "Coller un texte"),
        new(QuickActionType.OpenWheel, "Ouvrir une autre roue"),
        new(QuickActionType.ScanScreenQr, "Scanner un QR code à l'écran")
    };

    public static (string Label, string Hint, BrowseKind Browse, bool HasArguments) Describe(QuickActionType type) => type switch
    {
        QuickActionType.OpenApp => ("Programme ou fichier",
            @"Ex. notepad.exe, C:\Outils\app.exe ou un document. Les variables comme %USERPROFILE% sont acceptées.",
            BrowseKind.File, true),
        QuickActionType.OpenFolder => ("Dossier", @"Ex. %USERPROFILE%\Downloads", BrowseKind.Folder, false),
        QuickActionType.OpenUrl => ("Adresse", "Ex. https://www.google.com, ms-settings:display, ms-screenclip:", BrowseKind.None, false),
        QuickActionType.RunCommand => ("Commande", "Exécutée en arrière-plan avec cmd /c.", BrowseKind.None, false),
        QuickActionType.PowerShell => ("Script .ps1 ou commande",
            "Un fichier .ps1 est lancé avec -File ; sinon le texte est exécuté avec -Command.",
            BrowseKind.File, true),
        QuickActionType.SendKeys => ("Touches",
            "Syntaxe SendKeys : ^c = Ctrl+C, %{F4} = Alt+F4, +{TAB} = Shift+Tab. Envoyées à la fenêtre active.",
            BrowseKind.None, false),
        QuickActionType.PasteText => ("Texte à coller",
            "Copié dans le presse-papiers puis collé (Ctrl+V) dans la fenêtre active.",
            BrowseKind.None, false),
        QuickActionType.OpenWheel => ("Roue à ouvrir",
            "Survole ce secteur un instant (ou clique-le) pour passer à cette roue sans fermer.",
            BrowseKind.None, false),
        QuickActionType.ScanScreenQr => ("Aucun réglage",
            "Lit les QR codes visibles sur tous tes écrans (PDF, navigateur, image ouverte…) : un lien s'ouvre dans le navigateur, un autre contenu est copié.",
            BrowseKind.None, false),
        _ => ("Cible", "", BrowseKind.None, false)
    };

    public static WheelEntry ToEntry(QuickAction a) => a.Type == QuickActionType.ScanScreenQr
        ? new WheelEntry { Label = a.Name, Glyph = a.Glyph, Color = a.Color, Run = Converters.QrTools.ScanScreenAsync }
        : new WheelEntry
    {
        Label = a.Name,
        Glyph = a.Glyph,
        Color = a.Color,
        NeedsForeground = a.Type is QuickActionType.SendKeys or QuickActionType.PasteText,
        Run = () =>
        {
            Run(a);
            return Task.FromResult<string?>(null);
        }
    };

    public static void Run(QuickAction a)
    {
        string target = Environment.ExpandEnvironmentVariables(a.Target ?? "").Trim();
        string args = Environment.ExpandEnvironmentVariables(a.Arguments ?? "").Trim();
        if (a.Type == QuickActionType.ScanScreenQr)
        {
            _ = RunScanAsync(); // bouton « Tester » du designer
            return;
        }
        if (target.Length == 0) throw new InvalidOperationException($"« {a.Name} » : aucune cible définie.");

        switch (a.Type)
        {
            case QuickActionType.OpenApp:
                var psi = new ProcessStartInfo(target) { Arguments = args, UseShellExecute = true };
                if (Path.IsPathRooted(target)) psi.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
                Process.Start(psi);
                break;

            case QuickActionType.OpenFolder:
                Process.Start("explorer.exe", $"\"{target}\"");
                break;

            case QuickActionType.OpenUrl:
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                break;

            case QuickActionType.RunCommand:
                Process.Start(new ProcessStartInfo("cmd.exe", "/c " + target)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                break;

            case QuickActionType.PowerShell:
                string psArgs = target.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                    ? $"-NoProfile -ExecutionPolicy Bypass -File \"{target}\" {args}"
                    : $"-NoProfile -ExecutionPolicy Bypass -Command {target}";
                Process.Start(new ProcessStartInfo("powershell.exe", psArgs)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                break;

            case QuickActionType.SendKeys:
                System.Windows.Forms.SendKeys.SendWait(a.Target);
                break;

            case QuickActionType.PasteText:
                System.Windows.Clipboard.SetText(a.Target);
                System.Windows.Forms.SendKeys.SendWait("^v");
                break;
        }
    }

    private static async Task RunScanAsync()
    {
        try
        {
            string? message = await Converters.QrTools.ScanScreenAsync();
            System.Windows.MessageBox.Show(message ?? "OK", "QR code");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "QR code");
        }
    }
}
