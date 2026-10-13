using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CarIngest.Core.Documents;

/// <summary>
/// Computes a stable content hash over the allowlisted projection.
/// </summary>
/// <remarks>
/// Hashing the projection rather than raw HTML is the whole point (research D13): it detects
/// meaningful change (price, mileage) while ignoring cosmetic churn — a rotated banner URL, an
/// ad slot, a "47 people viewed this" counter — because none of that enters
/// <see cref="VehicleFields"/>. It also means the hash covers exactly the fields that were
/// validated and committed to storage, rather than bytes we never agreed to care about.
/// </remarks>
public static class CanonicalHash
{
    /// <summary>
    /// Serializes the projection deterministically: fixed field order, invariant culture,
    /// whitespace collapsed, text lowercased. Two pages differing only in casing or spacing of
    /// an allowlisted value must hash identically.
    /// </summary>
    public static string Canonicalize(VehicleFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var builder = new StringBuilder();
        Append(builder, VehicleFieldNames.Make, fields.Make);
        Append(builder, VehicleFieldNames.Model, fields.Model);
        Append(builder, VehicleFieldNames.Year, fields.Year.ToString(CultureInfo.InvariantCulture));
        Append(builder, VehicleFieldNames.Trim, fields.Trim);
        Append(builder, VehicleFieldNames.MileageValue, fields.MileageValue?.ToString(CultureInfo.InvariantCulture));
        Append(builder, VehicleFieldNames.MileageUnit, fields.MileageUnit);

        // Fixed 2dp so "12000" and "12000.00" are the same price, not different content.
        Append(builder, VehicleFieldNames.PriceAmount, fields.PriceAmount?.ToString("F2", CultureInfo.InvariantCulture));
        Append(builder, VehicleFieldNames.PriceCurrency, fields.PriceCurrency);
        Append(builder, VehicleFieldNames.FuelType, fields.FuelType);
        Append(builder, VehicleFieldNames.Transmission, fields.Transmission);
        Append(builder, VehicleFieldNames.BodyStyle, fields.BodyStyle);
        Append(builder, VehicleFieldNames.Colour, fields.Colour);
        Append(builder, VehicleFieldNames.Vin, fields.Vin);
        return builder.ToString();
    }

    /// <summary>SHA-256 over the canonical form. Stability, not cryptographic strength, is the requirement.</summary>
    public static byte[] Compute(VehicleFields fields) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(fields)));

    public static byte[] ComputeForText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeWhitespace(text)));
    }

    private static void Append(StringBuilder builder, string name, string? value)
    {
        builder.Append(name).Append('=');
        if (!string.IsNullOrWhiteSpace(value))
        {
            builder.Append(NormalizeWhitespace(value).ToLowerInvariant());
        }

        builder.Append('\n');
    }

    private static string NormalizeWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }
}
