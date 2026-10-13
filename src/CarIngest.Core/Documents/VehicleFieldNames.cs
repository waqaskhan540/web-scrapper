using System.Collections.Frozen;

namespace CarIngest.Core.Documents;

/// <summary>
/// The complete set of field names a host policy may permit. Startup validation rejects any
/// policy naming something outside this set, so a configuration mistake surfaces at boot rather
/// than as a mid-run extraction failure.
/// </summary>
public static class VehicleFieldNames
{
    public const string Make = nameof(VehicleFields.Make);
    public const string Model = nameof(VehicleFields.Model);
    public const string Year = nameof(VehicleFields.Year);
    public const string Trim = nameof(VehicleFields.Trim);
    public const string MileageValue = nameof(VehicleFields.MileageValue);
    public const string MileageUnit = nameof(VehicleFields.MileageUnit);
    public const string PriceAmount = nameof(VehicleFields.PriceAmount);
    public const string PriceCurrency = nameof(VehicleFields.PriceCurrency);
    public const string FuelType = nameof(VehicleFields.FuelType);
    public const string Transmission = nameof(VehicleFields.Transmission);
    public const string BodyStyle = nameof(VehicleFields.BodyStyle);
    public const string Colour = nameof(VehicleFields.Colour);
    public const string Vin = nameof(VehicleFields.Vin);

    /// <summary>Fields without which a document is not stored at all (FR-011).</summary>
    public static FrozenSet<string> Mandatory { get; } =
        new[] { Make, Model, Year }.ToFrozenSet(StringComparer.Ordinal);

    public static FrozenSet<string> All { get; } = new[]
    {
        Make, Model, Year, Trim, MileageValue, MileageUnit, PriceAmount, PriceCurrency,
        FuelType, Transmission, BodyStyle, Colour, Vin,
    }.ToFrozenSet(StringComparer.Ordinal);
}
