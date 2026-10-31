using System.Globalization;

namespace Termyn.TestSupport;

/// <summary>
/// Sets the machine's culture for as long as it's held, and puts back what was there before.
/// </summary>
/// <remarks>
/// Held with <c>using var</c> at the top of a test, so it covers everything the test does after,
/// not only the setup. A presenter writes its rows again whenever something changes, and a scope
/// that ended after setup leaves those rows in whatever culture the machine happens to be in: en-GB
/// here, en-US on the CI runner.
///
/// The culture comes from <see cref="CultureInfo.GetCultureInfo(string)"/>, which leaves out
/// anything the machine has changed in its region settings.
/// </remarks>
public sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _was = CultureInfo.CurrentCulture;

    /// <summary>Sets the culture.</summary>
    /// <param name="name">The culture's name, such as "en-GB"</param>
    public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

    /// <summary>Sets the culture to one made here, such as one of <see cref="Regions"/>.</summary>
    /// <param name="culture">The culture</param>
    public CultureScope(CultureInfo culture) => CultureInfo.CurrentCulture = culture;

    public void Dispose() => CultureInfo.CurrentCulture = _was;
}
