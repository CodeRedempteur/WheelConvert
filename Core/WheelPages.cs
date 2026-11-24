using System;
using System.Collections.Generic;
using System.Linq;

namespace WheelConvert.Core;

/// <summary>
/// Liste ordonnée de toutes les roues d'actions : celles de WheelConvert, puis celles envoyées par
/// d'autres applications (Stream Deck). On passe de l'une à l'autre à la molette.
/// </summary>
public static class WheelPages
{
    public const int MaxSectors = 16;

    public sealed record Page(string Key, string Name, string Glyph, string Color, string? Source, Func<List<WheelEntry>> Build);

    /// Clé d'une roue de WheelConvert.
    public static string KeyFor(WheelPage w) => "own:" + w.Id;

    public static List<Page> All(AppSettings? settings = null)
    {
        var s = settings ?? AppSettings.Current;
        var pages = s.Wheels
            .Select(w => new Page(KeyFor(w), w.Name, w.Glyph, w.Color, null, () => EntriesFor(w)))
            .ToList();

        if (s.ShowExternalActions)
        {
            pages.AddRange(ExternalActions.Wheels.Select(w =>
                new Page(w.Key, w.Name, w.Glyph, w.Color, w.Provider, () => ExternalActions.EntriesFor(w))));
        }
        return pages;
    }

    public static List<WheelEntry> EntriesFor(WheelPage wheel) =>
        wheel.Actions.Select(ToEntry).Take(MaxSectors).ToList();

    public static WheelEntry ToEntry(QuickAction a) =>
        a.Type == QuickActionType.OpenWheel
            ? new WheelEntry { Label = a.Name, Sub = "roue ›", Glyph = a.Glyph, Color = a.Color, SwitchTo = a.Target }
            : QuickActionRunner.ToEntry(a);

    /// Choix proposés pour une action « Ouvrir une roue » (toutes les roues sauf celle en cours).
    public static List<KeyValuePair<string, string>> LinkChoices(AppSettings settings, string? exceptKey) =>
        All(settings)
            .Where(p => p.Key != exceptKey)
            .Select(p => new KeyValuePair<string, string>(p.Key, p.Source is null ? $"{p.Glyph}  {p.Name}" : $"{p.Glyph}  {p.Name}  ({p.Source})"))
            .ToList();
}
