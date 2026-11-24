using System;
using System.Threading.Tasks;

namespace WheelConvert.Core;

/// <summary>Un secteur de la roue : ce qui est affiché et ce qui est exécuté.</summary>
public sealed class WheelEntry
{
    public required string Label { get; init; }
    public string? Sub { get; init; }
    public string? Glyph { get; init; }
    public string? Color { get; init; }

    /// Exécute l'action ; renvoie un message de fin (ou null pour aucune notification).
    public Func<Task<string?>> Run { get; init; } = () => Task.FromResult<string?>(null);

    /// Message affiché au lancement (conversions longues).
    public string? StartMessage { get; init; }

    /// Remettre la fenêtre précédente au premier plan avant d'exécuter (touches, collage).
    public bool NeedsForeground { get; init; }

    /// Secteur « lien » : ouvre une autre roue (clé de WheelPages) au lieu d'exécuter une action.
    public string? SwitchTo { get; init; }
}
