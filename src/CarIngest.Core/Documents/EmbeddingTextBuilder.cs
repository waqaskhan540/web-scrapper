using System.Globalization;
using System.Text;
using CarIngest.Core.Abstractions;

namespace CarIngest.Core.Documents;

/// <summary>
/// Templates the allowlisted projection into natural language for embedding.
/// </summary>
/// <remarks>
/// <para>
/// One text per document — no chunking. A listing is already a single coherent semantic unit of
/// a few hundred words, well inside every candidate model's context window; splitting it would
/// add bookkeeping and can hurt retrieval by fragmenting a unit meant to be matched whole
/// (research D6).
/// </para>
/// <para>
/// The structured fields are templated rather than omitted because a bare seller blurb is
/// semantically blind to year, make, mileage, price, and fuel type — exactly what a query like
/// "cheap diesel wagon under 50k miles" turns on.
/// </para>
/// <para>
/// Input is <see cref="VehicleFields"/>, which is already the screened projection. That ordering
/// is deliberate: build the text from raw page content and a phone number in a description ends
/// up baked into the vector, where it cannot be redacted afterwards (research D8).
/// </para>
/// </remarks>
public sealed class EmbeddingTextBuilder : IEmbeddingTextBuilder
{
    public string Build(VehicleFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{fields.Year} {fields.Make} {fields.Model}");

        if (!string.IsNullOrWhiteSpace(fields.Trim))
        {
            builder.Append(' ').Append(fields.Trim);
        }

        var parts = new List<string>();

        if (fields.MileageValue is { } mileage)
        {
            var unit = fields.MileageUnit switch
            {
                "km" => "kilometres",
                _ => "miles",
            };
            parts.Add($"{mileage.ToString("N0", CultureInfo.InvariantCulture)} {unit}");
        }

        if (fields.PriceAmount is { } price && !string.IsNullOrWhiteSpace(fields.PriceCurrency))
        {
            parts.Add($"{fields.PriceCurrency} {price.ToString("N0", CultureInfo.InvariantCulture)}");
        }

        AddIfPresent(parts, fields.FuelType);
        AddIfPresent(parts, fields.Transmission);
        AddIfPresent(parts, fields.BodyStyle);
        AddIfPresent(parts, fields.Colour);

        if (parts.Count > 0)
        {
            builder.Append(", ").Append(string.Join(", ", parts));
        }

        builder.Append('.');
        return builder.ToString();
    }

    private static void AddIfPresent(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(value.Trim().ToLowerInvariant());
        }
    }
}
