using System.Globalization;
using System.Text.Json;
using AngleSharp;
using AngleSharp.Dom;
using CarIngest.Core.Abstractions;
using CarIngest.Core.Documents;
using CarIngest.Core.Policies;
using CarIngest.Core.Runs;

namespace CarIngest.Infrastructure.Extraction;

/// <summary>
/// Extracts a car from schema.org JSON-LD.
/// </summary>
/// <remarks>
/// <para>
/// JSON-LD first because it is a machine-facing contract generated from the site's backend
/// model: it survives rebrands and component refactors that break CSS selectors, and it yields
/// typed named fields rather than "the third span in .price-block" (research D9).
/// </para>
/// <para>
/// Absence fails loudly. There is deliberately no automatic fallback to selector scraping — a
/// selector that matches the wrong element produces plausible wrong data, which is the failure
/// FR-011 exists to prevent.
/// </para>
/// <para>
/// Extraction reads named properties one at a time into <see cref="VehicleFields"/>, which has
/// no catch-all member. Seller name, phone, and address are never addressed in code, so there is
/// no path by which they could propagate (research D8, gate I-4).
/// </para>
/// </remarks>
public sealed class JsonLdCarExtractor : ICarExtractor
{
    public const string Version = "jsonld-car/1.0.0";

    private static readonly string[] CarTypes = ["Car", "Vehicle", "Product"];
    private readonly IBrowsingContext browsingContext =
        BrowsingContext.New(AngleSharp.Configuration.Default);

    public ExtractorKind Kind => ExtractorKind.JsonLd;

    public IReadOnlyList<ExtractedCar> Extract(string content, Uri sourceUrl, HostAccessPolicy policy)
    {
        // A JSON-LD listing describes one vehicle, so this always yields a single element. The
        // card key is the page itself, since there is nothing finer to key on.
        return [new ExtractedCar(CardKey: "page", DetailUrl: null, ExtractOne(content, sourceUrl, policy))];
    }

    private ExtractionResult ExtractOne(string content, Uri sourceUrl, HostAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(sourceUrl);
        ArgumentNullException.ThrowIfNull(policy);

        using var document = this.browsingContext
            .OpenAsync(req => req.Content(content))
            .GetAwaiter().GetResult();

        var blocks = document.QuerySelectorAll("script[type='application/ld+json']")
            .Select(node => node.TextContent)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        if (blocks.Length == 0)
        {
            return JsRenderDetector.LooksClientRendered(document)
                ? ExtractionResult.Fail(
                    ReasonCode.JsRenderRequired,
                    "No structured data in the served HTML; content appears to hydrate client-side.")
                : ExtractionResult.Fail(
                    ReasonCode.JsonLdAbsent,
                    "No application/ld+json block found in the served HTML.");
        }

        var anyParsed = false;
        foreach (var block in blocks)
        {
            JsonDocument parsed;
            try
            {
                parsed = JsonDocument.Parse(block, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                });
            }
            catch (JsonException)
            {
                // A malformed block is not fatal on its own: pages routinely carry several, and
                // one broken breadcrumb block should not mask a valid Car node. If nothing
                // parses at all, that is reported below.
                continue;
            }

            using (parsed)
            {
                anyParsed = true;

                // Select by @type rather than taking the first block: a listing page typically
                // carries breadcrumb, organization, and FAQ nodes alongside the real car, and
                // the organization node is where seller telephone numbers live (research D9).
                if (FindCarNode(parsed.RootElement) is not { } carNode)
                {
                    continue;
                }

                return Project(carNode, policy);
            }
        }

        return anyParsed
            ? ExtractionResult.Fail(
                ReasonCode.NotACarPage,
                $"Structured data present but no node of @type {string.Join('/', CarTypes)} was found.")
            : ExtractionResult.Fail(
                ReasonCode.JsonLdMalformed,
                "Every application/ld+json block failed to parse as JSON.");
    }

    private static JsonElement? FindCarNode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (HasCarType(element))
                {
                    return element;
                }

                // @graph is the common wrapper when a page declares several entities at once.
                if (element.TryGetProperty("@graph", out var graph))
                {
                    return FindCarNode(graph);
                }

                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindCarNode(item) is { } found)
                    {
                        return found;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    private static bool HasCarType(JsonElement node)
    {
        if (!node.TryGetProperty("@type", out var type))
        {
            return false;
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => CarTypes.Contains(type.GetString(), StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => type.EnumerateArray().Any(t =>
                t.ValueKind == JsonValueKind.String &&
                CarTypes.Contains(t.GetString(), StringComparer.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    /// <summary>
    /// Maps named source properties into the allowlisted record, field by field.
    /// </summary>
    /// <remarks>
    /// The return type has no catch-all member, so the compiler itself prevents unmapped source
    /// data from propagating. Note what is *not* read: "seller", "description", "telephone",
    /// "email", "address". Those properties are never addressed, so they cannot leak — the
    /// guarantee is that the code has no expression referring to them, not that a filter removes
    /// them afterwards (FR-010).
    /// </remarks>
    private static ExtractionResult Project(JsonElement node, HostAccessPolicy policy)
    {
        bool Permitted(string field) => policy.PermittedFields.Contains(field, StringComparer.Ordinal);

        var make = Permitted(VehicleFieldNames.Make) ? ReadBrand(node) : null;
        var model = Permitted(VehicleFieldNames.Model) ? ReadString(node, "model") : null;
        var year = Permitted(VehicleFieldNames.Year) ? ReadYear(node) : null;

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(make))
        {
            missing.Add(VehicleFieldNames.Make);
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            missing.Add(VehicleFieldNames.Model);
        }

        if (year is null)
        {
            missing.Add(VehicleFieldNames.Year);
        }

        if (missing.Count > 0)
        {
            // Names the field, never quotes the page.
            return ExtractionResult.Fail(
                ReasonCode.SchemaDrift,
                $"Mandatory field(s) absent from the schema.org node: {string.Join(", ", missing)}.");
        }

        var (mileageValue, mileageUnit) = Permitted(VehicleFieldNames.MileageValue)
            ? ReadMileage(node)
            : (null, null);

        var (priceAmount, priceCurrency) = Permitted(VehicleFieldNames.PriceAmount)
            ? ReadPrice(node)
            : (null, null);

        // Sanity ranges guard against schema abuse — SEO spam stuffs exaggerated values into
        // JSON-LD. A violation is a reported failure, never a silent clamp (research D9).
        if (year!.Value is < 1900 or > 2100)
        {
            return ExtractionResult.Fail(ReasonCode.ValidationRange, "Year outside the plausible range 1900-2100.");
        }

        if (mileageValue is < 0 or > 1_000_000)
        {
            return ExtractionResult.Fail(ReasonCode.ValidationRange, "Mileage outside the plausible range 0-1,000,000.");
        }

        if (priceAmount is < 0 or > 10_000_000)
        {
            return ExtractionResult.Fail(ReasonCode.ValidationRange, "Price outside the plausible range 0-10,000,000.");
        }

        return ExtractionResult.Ok(new VehicleFields
        {
            Make = make!,
            Model = model!,
            Year = year!.Value,
            Trim = Permitted(VehicleFieldNames.Trim) ? ReadString(node, "vehicleConfiguration") : null,
            MileageValue = mileageValue,
            MileageUnit = Permitted(VehicleFieldNames.MileageUnit) ? mileageUnit : null,
            PriceAmount = priceAmount,
            PriceCurrency = Permitted(VehicleFieldNames.PriceCurrency) ? priceCurrency : null,
            FuelType = Permitted(VehicleFieldNames.FuelType) ? ReadString(node, "fuelType") : null,
            Transmission = Permitted(VehicleFieldNames.Transmission) ? ReadString(node, "vehicleTransmission") : null,
            BodyStyle = Permitted(VehicleFieldNames.BodyStyle) ? ReadString(node, "bodyType") : null,
            Colour = Permitted(VehicleFieldNames.Colour) ? ReadString(node, "color") : null,
            Vin = Permitted(VehicleFieldNames.Vin) ? ReadVin(node) : null,
        });
    }

    private static string? ReadString(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static string? ReadBrand(JsonElement node)
    {
        if (!node.TryGetProperty("brand", out var brand))
        {
            return ReadString(node, "manufacturer");
        }

        return brand.ValueKind switch
        {
            JsonValueKind.String => brand.GetString()?.Trim(),
            JsonValueKind.Object => ReadString(brand, "name"),
            _ => null,
        };
    }

    private static int? ReadYear(JsonElement node)
    {
        foreach (var property in (string[])["vehicleModelDate", "productionDate", "modelDate"])
        {
            if (!node.TryGetProperty(property, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
            {
                return numeric;
            }

            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (text is null)
            {
                continue;
            }

            // Dates arrive as "2018", "2018-06", or "2018-06-01".
            if (int.TryParse(
                    text.AsSpan(0, Math.Min(4, text.Length)),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static (int? Value, string? Unit) ReadMileage(JsonElement node)
    {
        if (!node.TryGetProperty("mileageFromOdometer", out var odometer))
        {
            return (null, null);
        }

        if (odometer.ValueKind == JsonValueKind.Number && odometer.TryGetInt32(out var bare))
        {
            return (bare, "mi");
        }

        if (odometer.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        int? value = null;
        if (odometer.TryGetProperty("value", out var raw))
        {
            if (raw.ValueKind == JsonValueKind.Number && raw.TryGetInt32(out var numeric))
            {
                value = numeric;
            }
            else if (raw.ValueKind == JsonValueKind.String &&
                     int.TryParse(raw.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                value = parsed;
            }
        }

        // UN/CEFACT codes: SMI = statute mile, KMT = kilometre.
        var unit = ReadString(odometer, "unitCode") switch
        {
            "KMT" => "km",
            "SMI" => "mi",
            _ => ReadString(odometer, "unitText")?.StartsWith("km", StringComparison.OrdinalIgnoreCase) == true
                ? "km"
                : "mi",
        };

        return (value, value is null ? null : unit);
    }

    private static (decimal? Amount, string? Currency) ReadPrice(JsonElement node)
    {
        if (!node.TryGetProperty("offers", out var offers))
        {
            return (null, null);
        }

        if (offers.ValueKind == JsonValueKind.Array)
        {
            offers = offers.EnumerateArray().FirstOrDefault();
            if (offers.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }
        }

        if (offers.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        decimal? amount = null;
        if (offers.TryGetProperty("price", out var price))
        {
            if (price.ValueKind == JsonValueKind.Number && price.TryGetDecimal(out var numeric))
            {
                amount = numeric;
            }
            else if (price.ValueKind == JsonValueKind.String &&
                     decimal.TryParse(price.GetString(), NumberStyles.Currency, CultureInfo.InvariantCulture, out var parsed))
            {
                amount = parsed;
            }
        }

        var currency = ReadString(offers, "priceCurrency");
        return (amount, amount is null ? null : currency?.ToUpperInvariant());
    }

    private static string? ReadVin(JsonElement node)
    {
        var vin = ReadString(node, "vehicleIdentificationNumber");
        return vin is { Length: 17 } ? vin : null;
    }
}
