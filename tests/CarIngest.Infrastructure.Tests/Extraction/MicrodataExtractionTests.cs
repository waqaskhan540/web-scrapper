using CarIngest.Core.Abstractions;
using CarIngest.Core.Documents;
using CarIngest.Core.Policies;
using CarIngest.Core.Runs;
using CarIngest.Infrastructure.Extraction;
using CarIngest.Infrastructure.Tests.Harness;
using Xunit;

namespace CarIngest.Infrastructure.Tests.Extraction;

/// <summary>
/// Microdata extraction for webscraper.io (see specs/.../r1-findings.md).
/// </summary>
/// <remarks>
/// That host publishes <c>schema.org/Product</c> microdata and no JSON-LD, with year and
/// mileage outside microdata entirely as labelled text. Its per-car detail pages are
/// robots-disallowed, so the listing page is the only lawful source and must yield every card
/// from one fetch.
/// </remarks>
public sealed class MicrodataExtractionTests
{
    private static readonly string Html =
        FixtureFiles.Read(Path.Combine("webscraper.io", "load-more-cars.html"));

    /// <summary>The six fields this host actually provides.</summary>
    private static HostAccessPolicy Policy => new()
    {
        Host = "webscraper.io",
        ExtractorKind = ExtractorKind.Css,
        CssProfile = "webscraper-load-more",
        PermittedFields =
        [
            VehicleFieldNames.Make, VehicleFieldNames.Model, VehicleFieldNames.Year,
            VehicleFieldNames.MileageValue, VehicleFieldNames.MileageUnit,
            VehicleFieldNames.PriceAmount, VehicleFieldNames.PriceCurrency,
        ],
    };

    private static IReadOnlyList<ExtractedCar> ExtractAll() =>
        new MicrodataCarExtractor().Extract(Html, new Uri("https://webscraper.io/test-sites/load-more"), Policy);

    [Fact]
    public void EveryProductCardOnTheListingPage_IsExtracted()
    {
        var results = ExtractAll();

        // Five Product cards; the SiteNavigationElement is not one of them.
        Assert.Equal(5, results.Count);
    }

    [Fact]
    public void NavigationMicrodata_IsNotMistakenForAListingCard()
    {
        // The page also carries a SiteNavigationElement with itemprop="name". Treating any
        // itemscope as a card would produce a document for the site menu.
        var names = ExtractAll()
            .Where(r => r.Extraction.Success)
            .Select(r => r.Extraction.Fields!.Make)
            .ToList();

        Assert.DoesNotContain("Test", names);
    }

    [Fact]
    public void MakeModelAndYear_AreSplitOutOfTheCombinedNameField()
    {
        var first = ExtractAll()[0].Extraction;

        Assert.True(first.Success, first.Message);
        var fields = first.Fields!;

        // "Mercedes-Benz W123 280E 1955" -> make / model / year.
        Assert.Equal("Mercedes-Benz", fields.Make);
        Assert.Equal("W123 280E", fields.Model);
        Assert.Equal(1955, fields.Year);
    }

    [Fact]
    public void HyphenatedMake_SurvivesTokenization()
    {
        var makes = ExtractAll()
            .Where(r => r.Extraction.Success)
            .Select(r => r.Extraction.Fields!.Make)
            .ToList();

        // Splitting on punctuation rather than whitespace would yield "Mercedes".
        Assert.Contains("Mercedes-Benz", makes);
        Assert.Contains("BMW", makes);
        Assert.Contains("Jaguar", makes);
    }

    [Fact]
    public void MileageWithSpaceThousandsSeparator_IsParsed()
    {
        var first = ExtractAll()[0].Extraction;

        // "104 338 km" — space separators, unit suffix.
        Assert.Equal(104338, first.Fields!.MileageValue);
        Assert.Equal("km", first.Fields.MileageUnit);
    }

    [Fact]
    public void MilesUnit_IsDistinguishedFromKilometres()
    {
        var jaguar = ExtractAll()
            .Select(r => r.Extraction)
            .Single(r => r.Success && r.Fields!.Make == "Jaguar");

        // "132 500 mi" must not be recorded as kilometres — a 1.6x error in the stored value.
        Assert.Equal(132500, jaguar.Fields!.MileageValue);
        Assert.Equal("mi", jaguar.Fields.MileageUnit);
    }

    [Fact]
    public void PriceWithCurrencyInsideTheText_IsParsed()
    {
        var first = ExtractAll()[0].Extraction;

        // "USD 228 511" plus a <meta itemprop="priceCurrency" content="USD">.
        Assert.Equal(228511m, first.Fields!.PriceAmount);
        Assert.Equal("USD", first.Fields.PriceCurrency);
    }

    [Fact]
    public void NonUsdCurrency_IsCarriedThrough()
    {
        var jaguar = ExtractAll()
            .Select(r => r.Extraction)
            .Single(r => r.Success && r.Fields!.Make == "Jaguar");

        Assert.Equal(18250m, jaguar.Fields!.PriceAmount);
        Assert.Equal("GBP", jaguar.Fields.PriceCurrency);
    }

    [Fact]
    public void CardWithoutAYear_FailsAsSchemaDrift_AndNamesTheField()
    {
        var ferrari = ExtractAll()
            .Select(r => r.Extraction)
            .Single(r => !r.Success && r.ReasonCode == ReasonCode.SchemaDrift);

        Assert.Contains(VehicleFieldNames.Year, ferrari.Message!, StringComparison.Ordinal);

        // Nothing partial is produced, even though make, model, mileage and price were all
        // readable (FR-011).
        Assert.Null(ferrari.Fields);
    }

    [Fact]
    public void ImplausibleMileage_FailsAsValidationRange_RatherThanBeingClamped()
    {
        var nissan = ExtractAll()
            .Select(r => r.Extraction)
            .Single(r => !r.Success && r.ReasonCode == ReasonCode.ValidationRange);

        Assert.Contains("Mileage", nissan.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditorialDescription_IsNeverCarriedIntoTheProjection()
    {
        // The cards carry itemprop="description" ("Uncompromising Stuttgart saloon"). It has no
        // personal data on this host, but D8 makes the absence of a free-text field structural
        // rather than conditional — a field that exists for one host exists for all of them.
        var json = System.Text.Json.JsonSerializer.Serialize(
            ExtractAll().Where(r => r.Extraction.Success).Select(r => r.Extraction.Fields).ToList());

        foreach (var copy in (string[])
            ["Uncompromising", "Stuttgart saloon", "Bavarian", "Coventry comfort", "Maranello"])
        {
            Assert.DoesNotContain(copy, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FieldsOutsideTheAllowlist_AreDiscardedAtProjectionTime()
    {
        var narrow = new HostAccessPolicy
        {
            Host = "webscraper.io",
            ExtractorKind = ExtractorKind.Css,
            CssProfile = "webscraper-load-more",
            PermittedFields = [VehicleFieldNames.Make, VehicleFieldNames.Model, VehicleFieldNames.Year],
        };

        var first = new MicrodataCarExtractor()
            .Extract(Html, new Uri("https://webscraper.io/test-sites/load-more"), narrow)[0].Extraction;

        Assert.True(first.Success, first.Message);
        Assert.Equal("Mercedes-Benz", first.Fields!.Make);

        // Present on the page, absent from the policy, therefore not stored.
        Assert.Null(first.Fields.MileageValue);
        Assert.Null(first.Fields.PriceAmount);
    }

    [Fact]
    public void UnmappedPageFields_AreNotStored()
    {
        // "Country of origin" and data-rating exist on the page and are not allowlisted vehicle
        // fields. The record has no member capable of holding them.
        var json = System.Text.Json.JsonSerializer.Serialize(ExtractAll()[0].Extraction.Fields);

        Assert.DoesNotContain("United Kingdom", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Country", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailUrls_AreSurfaced_SoTheyCanBeCheckedAgainstRobots()
    {
        // Each card links to /test-sites/product/..., which robots.txt disallows. Surfacing the
        // URL lets the caller verify that, rather than discovering it by requesting one.
        var detailUrls = ExtractAll()
            .Select(r => r.DetailUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u) && u != "#")
            .ToList();

        Assert.NotEmpty(detailUrls);

        // Every real detail link sits under the disallowed prefix, which is why the listing page
        // has to carry every field we store.
        Assert.All(detailUrls, u => Assert.Contains("/test-sites/product/", u!, StringComparison.Ordinal));
    }

    [Fact]
    public void JsonLdExtractor_RejectsThisHost_ConfirmingTheProfileIsNecessary()
    {
        // The reason this extractor exists: the host emits no JSON-LD, so the default path
        // fails loudly rather than silently guessing (research D9, risk R1).
        var viaJsonLd = new JsonLdCarExtractor()
            .Extract(Html, new Uri("https://webscraper.io/test-sites/load-more"), Policy)
            .Single().Extraction;

        Assert.False(viaJsonLd.Success);
        Assert.Equal(ReasonCode.JsonLdAbsent, viaJsonLd.ReasonCode);
    }
}
