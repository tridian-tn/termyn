namespace Termyn.Core.Settings;

/// <summary>
/// The colours Todoist gives projects, labels and filters.
/// </summary>
/// <remarks>
/// Todoist names a colour rather than giving it: a project carries <c>"berry_red"</c>, and what
/// that looks like is the client's business. These are the twenty from Todoist's own reference,
/// which also numbers them 30–49 — payloads have used both, so both are read.
///
/// Anything unrecognised gets charcoal, which is what Todoist itself uses for something with no
/// colour of its own. A colour Todoist adds later therefore shows as grey rather than as nothing,
/// and the account's own value survives the round trip regardless (§6.4).
/// </remarks>
public static class TodoistPalette
{
    /// <summary>What a colour we don't recognise is drawn as, and Todoist's own default.</summary>
    public static readonly Rgb Charcoal = Rgb.Parse("#808080");

    private static readonly Dictionary<string, Rgb> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["berry_red"] = Rgb.Parse("#B8255F"),
        ["red"] = Rgb.Parse("#DC4C3E"),
        ["orange"] = Rgb.Parse("#C77100"),
        ["yellow"] = Rgb.Parse("#B29104"),
        ["olive_green"] = Rgb.Parse("#949C31"),
        ["lime_green"] = Rgb.Parse("#65A33A"),
        ["green"] = Rgb.Parse("#369307"),
        ["mint_green"] = Rgb.Parse("#42A393"),
        ["teal"] = Rgb.Parse("#148FAD"),
        ["sky_blue"] = Rgb.Parse("#319DC0"),
        ["light_blue"] = Rgb.Parse("#6988A4"),
        ["blue"] = Rgb.Parse("#4180FF"),
        ["grape"] = Rgb.Parse("#692EC2"),
        ["violet"] = Rgb.Parse("#CA3FEE"),
        ["lavender"] = Rgb.Parse("#A4698C"),
        ["magenta"] = Rgb.Parse("#E05095"),
        ["salmon"] = Rgb.Parse("#C9766F"),
        ["charcoal"] = Charcoal,
        ["grey"] = Rgb.Parse("#999999"),
        ["taupe"] = Rgb.Parse("#8F7A69"),
    };

    /// <summary>The numbering Todoist gives the same twenty, in the order its reference lists them.</summary>
    private static readonly string[] ByNumber =
    [
        "berry_red", "red", "orange", "yellow", "olive_green",
        "lime_green", "green", "mint_green", "teal", "sky_blue",
        "light_blue", "blue", "grape", "violet", "lavender",
        "magenta", "salmon", "charcoal", "grey", "taupe",
    ];

    /// <summary>The lowest id Todoist's numbering starts at.</summary>
    private const int FirstId = 30;

    /// <summary>Every colour Todoist names, for anything that wants to show them all.</summary>
    /// <remarks>Wrapped, so a caller casting it back to an array can't rewrite the palette.</remarks>
    public static IReadOnlyCollection<string> Names { get; } = Array.AsReadOnly(ByNumber);

    /// <summary>
    /// What a Todoist colour looks like.
    /// </summary>
    /// <param name="colour">A name (<c>berry_red</c>) or one of Todoist's numbers (<c>30</c>)</param>
    /// <returns>The colour, or charcoal when it's missing or unrecognised</returns>
    public static Rgb Of(string? colour)
    {
        if (string.IsNullOrWhiteSpace(colour))
            return Charcoal;

        if (ByName.TryGetValue(colour.Trim(), out var named))
            return named;

        if (int.TryParse(colour, out var id) && id - FirstId is >= 0 and var index && index < ByNumber.Length)
            return ByName[ByNumber[index]];

        return Charcoal;
    }
}
