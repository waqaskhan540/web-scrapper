namespace CarIngest.Core.Documents;

/// <summary>
/// The allowlisted, non-personal projection of one car listing.
/// </summary>
/// <remarks>
/// This type is the PII boundary (constitution Principle I-4, spec FR-010). Two properties of
/// its shape are load-bearing and must not be relaxed:
/// <list type="number">
/// <item>
/// There is <b>no free-text seller description</b>. Sellers routinely embed phone numbers and
/// names inline ("call John at ..."), and regex scrubbing of free text has false negatives that
/// cannot be proven zero. Omitting the field makes the guarantee structural rather than
/// probabilistic: there is no field for personal data to occupy.
/// </item>
/// <item>
/// There is <b>no catch-all member</b> — no dictionary, no JsonElement, no ExtraFields. The CLR
/// type itself makes unmapped source data unrepresentable, so a future change cannot quietly
/// start carrying whatever a page happened to contain.
/// </item>
/// </list>
/// Adding either would re-open a path that the design closed deliberately.
/// </remarks>
public sealed record VehicleFields
{
    /// <summary>Mandatory. Absence is schema drift, not a nullable field.</summary>
    public required string Make { get; init; }

    /// <summary>Mandatory. Absence is schema drift, not a nullable field.</summary>
    public required string Model { get; init; }

    /// <summary>Mandatory. Valid range 1900..(current year + 2).</summary>
    public required int Year { get; init; }

    public string? Trim { get; init; }

    /// <summary>Range 0..1,000,000 when present.</summary>
    public int? MileageValue { get; init; }

    /// <summary>"mi" or "km". Required when <see cref="MileageValue"/> is present.</summary>
    public string? MileageUnit { get; init; }

    /// <summary>Range 0..10,000,000 when present.</summary>
    public decimal? PriceAmount { get; init; }

    /// <summary>ISO 4217. Required when <see cref="PriceAmount"/> is present.</summary>
    public string? PriceCurrency { get; init; }

    public string? FuelType { get; init; }

    public string? Transmission { get; init; }

    public string? BodyStyle { get; init; }

    public string? Colour { get; init; }

    /// <summary>
    /// Exactly 17 characters when present. A VIN identifies a vehicle, not a person, so it is
    /// not personal data on its own — but it stays allowlist-gated per host, because a VIN plus
    /// a location could narrow to an individual in a small enough market.
    /// </summary>
    public string? Vin { get; init; }

    public string? DetectedLanguage { get; init; }
}
