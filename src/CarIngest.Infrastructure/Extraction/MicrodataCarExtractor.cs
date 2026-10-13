using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using CarIngest.Core.Abstractions;
using CarIngest.Core.Documents;
using CarIngest.Core.Policies;
using CarIngest.Core.Runs;

namespace CarIngest.Infrastructure.Extraction;

/// <summary>
/// Extracts cars from schema.org microdata plus labelled text fields.
/// </summary>
/// <remarks>
/// <para>
/// Built for hosts that publish structured data as microdata rather than JSON-LD — confirmed to
/// be the case for webscraper.io, which emits <c>itemtype="https://schema.org/Product"</c> and
/// no <c>application/ld+json</c> at all (see r1-findings.md).
/// </para>
/// <para>
/// Same allowlist discipline as <see cref="JsonLdCarExtractor"/>: named properties are read one
/// at a time into <see cref="VehicleFields"/>, which has no catch-all member, so unmapped page
/// content is unrepresentable rather than filtered out afterwards. Fields outside the host's
/// permitted set are discarded at projection time, and a missing mandatory field is loud schema
/// drift rather than a null.
/// </para>
/// <para>
/// This extractor is selected only by explicit host configuration. It is never an automatic
/// fallback from absent JSON-LD, because a selector matching the wrong element produces
/// plausible wrong data — the failure FR-011 exists to prevent.
/// </para>
/// </remarks>
public sealed partial class MicrodataCarExtractor : ICarExtractor
{
    public const string Version = "microdata-car/1.0.0";

    private readonly IBrowsingContext browsingContext =
        BrowsingContext.New(AngleSharp.Configuration.Default);

    public ExtractorKind Kind => ExtractorKind.Css;

    /// <summary>
    /// Extracts every car card on the page.
    /// </summary>
    /// <remarks>
    /// All cards come from one fetch. On this host the per-car detail pages are
    /// robots-disallowed, so the listing page is the only lawful source — and even if it were
    /// not, one request for six cars is politer than six requests.
    /// </remarks>
    public IReadOnlyList<ExtractedCar> Extract(string content, Uri sourceUrl, HostAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(sourceUrl);
        ArgumentNullException.ThrowIfNull(policy);

        var cards = this.ParseCards(content);

        if (cards.Count == 0)
        {
            return
            [
                new ExtractedCar(
                    CardKey: "page",
                    DetailUrl: null,
                    ExtractionResult.Fail(
                        ReasonCode.JsonLdAbsent,
                        "No schema.org microdata item of type Product was found in the served HTML.")),
            ];
        }

        return [.. cards.Select(card =>
            new ExtractedCar(CardKeyFor(card), card.DetailUrl, Project(card, policy)))];
    }

    /// <summary>
    /// Stable identity for a card within its page.
    /// </summary>
    /// <remarks>
    /// Prefers the detail-page slug ("mercedes-benz-w123-280e-1955-c001"), which the host
    /// already guarantees unique. Falls back to the name, which is weaker — two identical names
    /// on one page would collide — but is better than positional indexing, since a card
    /// reordering would otherwise re-key every document on the page and look like six changes.
    /// </remarks>
    private static string CardKeyFor(CardData card)
    {
        if (!string.IsNullOrWhiteSpace(card.DetailUrl) && card.DetailUrl != "#")
        {
            var slug = card.DetailUrl.TrimEnd('/');
            var lastSlash = slug.LastIndexOf('/');
            if (lastSlash >= 0 && lastSlash < slug.Length - 1)
            {
                return slug[(lastSlash + 1)..];
            }
        }

        return CollapseWhitespace(card.Name).ToLowerInvariant().Replace(' ', '-');
    }

    private List<CardData> ParseCards(string content)
    {
        using var document = this.browsingContext
            .OpenAsync(req => req.Content(content))
            .GetAwaiter().GetResult();

        var cards = new List<CardData>();

        foreach (var scope in document.QuerySelectorAll("[itemscope][itemtype]"))
        {
            var itemType = scope.GetAttribute("itemtype") ?? string.Empty;

            // Product rather than Car/Vehicle: this host describes cars with the generic
            // commerce type, which is why the JSON-LD extractor's @type check would reject it.
            if (!itemType.EndsWith("/Product", StringComparison.OrdinalIgnoreCase) &&
                !itemType.EndsWith("/Car", StringComparison.OrdinalIgnoreCase) &&
                !itemType.EndsWith("/Vehicle", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = ReadProp(scope, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            cards.Add(new CardData(
                Name: name,
                Price: ReadProp(scope, "price"),
                PriceCurrency: ReadProp(scope, "priceCurrency"),
                LabelledFields: ReadLabelledFields(scope),
                DetailUrl: scope.QuerySelector("a[href]")?.GetAttribute("href")));
        }

        return cards;
    }

    /// <summary>
    /// Reads an itemprop, preferring <c>content</c> so a &lt;meta&gt; carrier works, then the
    /// element text.
    /// </summary>
    private static string? ReadProp(IElement scope, string prop)
    {
        var element = scope.QuerySelector($"[itemprop='{prop}']");
        if (element is null)
        {
            return null;
        }

        var value = element.GetAttribute("content") ?? element.TextContent;
        return string.IsNullOrWhiteSpace(value) ? null : CollapseWhitespace(value);
    }

    /// <summary>
    /// Harvests "&lt;b&gt;Label: &lt;/b&gt; value" pairs, which is how this host publishes year
    /// and mileage — outside microdata entirely.
    /// </summary>
    private static Dictionary<string, string> ReadLabelledFields(IElement scope)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var paragraph in scope.QuerySelectorAll("p, li, div"))
        {
            var label = paragraph.QuerySelector("b, strong, dt, .label");
            if (label is null)
            {
                continue;
            }

            var key = CollapseWhitespace(label.TextContent).TrimEnd(':', ' ');
            if (key.Length == 0)
            {
                continue;
            }

            var whole = CollapseWhitespace(paragraph.TextContent);
            var labelText = CollapseWhitespace(label.TextContent);
            var value = whole.Length > labelText.Length
                ? whole[labelText.Length..].TrimStart(':', ' ')
                : string.Empty;

            if (value.Length > 0)
            {
                fields.TryAdd(key, value);
            }
        }

        return fields;
    }

    private static ExtractionResult Project(CardData card, HostAccessPolicy policy)
    {
        bool Permitted(string field) => policy.PermittedFields.Contains(field, StringComparer.Ordinal);

        // Year appears both as its own labelled field and as the trailing token of the name.
        // Prefer the labelled field and treat the name as corroboration.
        var labelledYear = card.LabelledFields.TryGetValue("Year", out var rawYear)
            ? ParseInt(rawYear)
            : null;

        var (makeFromName, modelFromName, yearFromName) = SplitName(card.Name);
        var year = labelledYear ?? yearFromName;

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(makeFromName))
        {
            missing.Add(VehicleFieldNames.Make);
        }

        if (string.IsNullOrWhiteSpace(modelFromName))
        {
            missing.Add(VehicleFieldNames.Model);
        }

        if (year is null)
        {
            missing.Add(VehicleFieldNames.Year);
        }

        if (missing.Count > 0)
        {
            return ExtractionResult.Fail(
                ReasonCode.SchemaDrift,
                $"Mandatory field(s) not derivable from the microdata card: {string.Join(", ", missing)}.");
        }

        if (year is < 1900 or > 2100)
        {
            return ExtractionResult.Fail(ReasonCode.ValidationRange, "Year outside the plausible range 1900-2100.");
        }

        var (mileageValue, mileageUnit) = card.LabelledFields.TryGetValue("Mileage", out var rawMileage)
            ? ParseMileage(rawMileage)
            : (null, null);

        if (mileageValue is < 0 or > 1_000_000)
        {
            return ExtractionResult.Fail(ReasonCode.ValidationRange, "Mileage outside the plausible range 0-1,000,000.");
        }

        var (priceAmount, priceCurrency) = ParsePrice(card.Price, card.PriceCurrency);

        if (priceAmount is < 0 or > 10_000_000)
        {
            return ExtractionResult.Fail(ReasonCode.ValidationRange, "Price outside the plausible range 0-10,000,000.");
        }

        return ExtractionResult.Ok(new VehicleFields
        {
            Make = Permitted(VehicleFieldNames.Make) ? makeFromName! : string.Empty,
            Model = Permitted(VehicleFieldNames.Model) ? modelFromName! : string.Empty,
            Year = year!.Value,

            // Every remaining field is absent on this host. They are nullable, so absence is
            // not drift — and nothing is invented to fill them.
            Trim = null,
            MileageValue = Permitted(VehicleFieldNames.MileageValue) ? mileageValue : null,
            MileageUnit = Permitted(VehicleFieldNames.MileageUnit) ? mileageUnit : null,
            PriceAmount = Permitted(VehicleFieldNames.PriceAmount) ? priceAmount : null,
            PriceCurrency = Permitted(VehicleFieldNames.PriceCurrency) ? priceCurrency : null,
            FuelType = null,
            Transmission = null,
            BodyStyle = null,
            Colour = null,
            Vin = null,
        });
    }

    /// <summary>
    /// Splits "Mercedes-Benz W123 280E 1955" into make, model, and year.
    /// </summary>
    /// <remarks>
    /// The leading token is the make: hyphenated makes ("Mercedes-Benz") survive whitespace
    /// tokenization intact, which is why splitting on whitespace rather than on punctuation
    /// matters here.
    /// </remarks>
    private static (string? Make, string? Model, int? Year) SplitName(string name)
    {
        var tokens = CollapseWhitespace(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            return (tokens.Length == 1 ? tokens[0] : null, null, null);
        }

        int? year = null;
        var end = tokens.Length;

        if (YearPattern().IsMatch(tokens[^1]) &&
            int.TryParse(tokens[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedYear))
        {
            year = parsedYear;
            end--;
        }

        var make = tokens[0];
        var model = end > 1 ? string.Join(' ', tokens[1..end]) : null;
        return (make, model, year);
    }

    private static int? ParseInt(string text)
    {
        var digits = new string([.. text.Where(char.IsDigit)]);
        return digits.Length > 0 &&
               int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>Parses "104 338 km" — note the space thousands separator.</summary>
    private static (int? Value, string? Unit) ParseMileage(string text)
    {
        var unit = text.Contains("km", StringComparison.OrdinalIgnoreCase) ? "km" : "mi";
        var value = ParseInt(text);
        return (value, value is null ? null : unit);
    }

    /// <summary>
    /// Parses "USD 228 511" plus a separate priceCurrency carrier. The currency appears inside
    /// the price text as well as in its own meta tag, so either source will do.
    /// </summary>
    private static (decimal? Amount, string? Currency) ParsePrice(string? price, string? currency)
    {
        if (string.IsNullOrWhiteSpace(price))
        {
            return (null, null);
        }

        var digits = new string([.. price.Where(c => char.IsDigit(c) || c == '.')]);
        if (digits.Length == 0 ||
            !decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            return (null, null);
        }

        var resolved = currency?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(resolved))
        {
            var match = CurrencyPattern().Match(price);
            resolved = match.Success ? match.Value.ToUpperInvariant() : null;
        }

        return (amount, resolved is { Length: 3 } ? resolved : null);
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var pending = false;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                pending = builder.Length > 0;
                continue;
            }

            if (pending)
            {
                builder.Append(' ');
                pending = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"^(19|20)\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"\b[A-Z]{3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyPattern();

    private sealed record CardData(
        string Name,
        string? Price,
        string? PriceCurrency,
        Dictionary<string, string> LabelledFields,
        string? DetailUrl);
}
