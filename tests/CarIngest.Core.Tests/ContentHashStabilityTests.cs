using CarIngest.Core.Documents;
using Xunit;

namespace CarIngest.Core.Tests;

/// <summary>
/// FR-018 / SC-008: the hash must track meaningful change and ignore cosmetic variation.
/// </summary>
/// <remarks>
/// Both directions matter and fail differently. Over-sensitivity means every run rewrites every
/// document and re-embeds the whole corpus for nothing. Under-sensitivity means a price change
/// is never noticed and the store quietly goes stale — the worse of the two, because it is
/// invisible.
/// </remarks>
public sealed class ContentHashStabilityTests
{
    private static VehicleFields Base => new()
    {
        Make = "Volkswagen",
        Model = "Golf",
        Year = 2018,
        Trim = "GT",
        MileageValue = 45000,
        MileageUnit = "mi",
        PriceAmount = 12000.00m,
        PriceCurrency = "GBP",
        FuelType = "Diesel",
        Transmission = "Manual",
        BodyStyle = "Hatchback",
        Colour = "Blue",
    };

    [Fact]
    public void IdenticalFields_HashIdentically()
    {
        Assert.Equal(CanonicalHash.Compute(Base), CanonicalHash.Compute(Base));
    }

    [Theory]
    [InlineData("  Volkswagen  ")]
    [InlineData("volkswagen")]
    [InlineData("VOLKSWAGEN")]
    [InlineData("\tVolkswagen\t")]
    [InlineData("\n Volkswagen \n")]
    public void WhitespaceAndCasingDifferences_DoNotChangeTheHash(string make)
    {
        // A site re-rendering "VOLKSWAGEN" in a different case, or with stray surrounding
        // whitespace, is not a content change. Note that interior whitespace IS significant:
        // "Volks wagen" is a different make from "Volkswagen", so it is not tested here.
        var variant = Base with { Make = make };

        Assert.Equal(CanonicalHash.Compute(Base), CanonicalHash.Compute(variant));
    }

    [Fact]
    public void PriceWrittenWithDifferentPrecision_DoesNotChangeTheHash()
    {
        // "12000" and "12000.00" are the same price.
        Assert.Equal(
            CanonicalHash.Compute(Base),
            CanonicalHash.Compute(Base with { PriceAmount = 12000m }));
    }

    [Fact]
    public void DetectedLanguage_DoesNotAffectTheHash()
    {
        // Language is metadata about the page, not part of the car's described content.
        Assert.Equal(
            CanonicalHash.Compute(Base),
            CanonicalHash.Compute(Base with { DetectedLanguage = "en-GB" }));
    }

    [Theory]
    [InlineData("price")]
    [InlineData("mileage")]
    [InlineData("year")]
    [InlineData("colour")]
    [InlineData("trim")]
    public void AnyMeaningfulFieldChange_ChangesTheHash(string field)
    {
        var changed = field switch
        {
            "price" => Base with { PriceAmount = 10500.00m },
            "mileage" => Base with { MileageValue = 46000 },
            "year" => Base with { Year = 2019 },
            "colour" => Base with { Colour = "Red" },
            _ => Base with { Trim = "R-Line" },
        };

        Assert.NotEqual(CanonicalHash.Compute(Base), CanonicalHash.Compute(changed));
    }

    [Fact]
    public void MileageUnitChange_ChangesTheHash()
    {
        // 45,000 miles and 45,000 km are very different cars.
        Assert.NotEqual(
            CanonicalHash.Compute(Base),
            CanonicalHash.Compute(Base with { MileageUnit = "km" }));
    }

    [Fact]
    public void NullVersusEmptyOptionalField_HashesTheSame()
    {
        Assert.Equal(
            CanonicalHash.Compute(Base with { Trim = null }),
            CanonicalHash.Compute(Base with { Trim = "   " }));
    }

    [Fact]
    public void FieldValuesCannotBeConfusedAcrossFieldBoundaries()
    {
        // The canonical form is field-keyed, so moving a value between fields must change the
        // hash. A naive concatenation would collide here.
        var swapped = Base with { Make = "Golf", Model = "Volkswagen" };

        Assert.NotEqual(CanonicalHash.Compute(Base), CanonicalHash.Compute(swapped));
    }

    [Fact]
    public void CanonicalFormIsDeterministicAcrossCalls()
    {
        var first = CanonicalHash.Canonicalize(Base);
        var second = CanonicalHash.Canonicalize(Base);

        Assert.Equal(first, second);
        // Keys keep their declared casing; only values are normalized.
        Assert.Contains("Make=volkswagen", first, StringComparison.Ordinal);
        Assert.Contains("PriceAmount=12000.00", first, StringComparison.Ordinal);
    }

    [Fact]
    public void TextHash_NormalizesWhitespaceOnly()
    {
        Assert.Equal(
            CanonicalHash.ComputeForText("2018  Volkswagen\nGolf"),
            CanonicalHash.ComputeForText("2018 Volkswagen Golf"));

        Assert.NotEqual(
            CanonicalHash.ComputeForText("2018 Volkswagen Golf"),
            CanonicalHash.ComputeForText("2019 Volkswagen Golf"));
    }
}
