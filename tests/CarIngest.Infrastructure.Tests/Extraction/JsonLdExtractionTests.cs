using CarIngest.Core.Documents;
using CarIngest.Core.Policies;
using CarIngest.Core.Runs;
using CarIngest.Infrastructure.Extraction;
using CarIngest.Infrastructure.Tests.Harness;
using Xunit;

namespace CarIngest.Infrastructure.Tests.Extraction;

public sealed class JsonLdExtractionTests
{
    private static readonly HostAccessPolicy FullPolicy = new()
    {
        Host = "dealer.example",
        PermittedFields = [.. VehicleFieldNames.All],
    };

    private static readonly Uri SourceUrl = new("https://dealer.example/listing/1");

    private static ExtractionResultWrapper Extract(string fixtureName, HostAccessPolicy? policy = null)
    {
        var html = FixtureFiles.Read(Path.Combine("dealer.example", fixtureName));
        // A JSON-LD page describes one car, so the list always has exactly one element.
        var result = new JsonLdCarExtractor().Extract(html, SourceUrl, policy ?? FullPolicy).Single().Extraction;
        return new ExtractionResultWrapper(result.Success, result.Fields, result.ReasonCode, result.Message);
    }

    private sealed record ExtractionResultWrapper(
        bool Success, VehicleFields? Fields, string? ReasonCode, string? Message);

    [Fact]
    public void ValidJsonLd_YieldsCompleteProjection()
    {
        var result = Extract("valid-jsonld.html");

        Assert.True(result.Success, result.Message);
        var f = result.Fields!;
        Assert.Equal("Volkswagen", f.Make);
        Assert.Equal("Golf", f.Model);
        Assert.Equal(2018, f.Year);
        Assert.Equal("GT", f.Trim);
        Assert.Equal(45000, f.MileageValue);
        Assert.Equal("mi", f.MileageUnit);
        Assert.Equal(12000.00m, f.PriceAmount);
        Assert.Equal("GBP", f.PriceCurrency);
        Assert.Equal("Diesel", f.FuelType);
        Assert.Equal("Manual", f.Transmission);
        Assert.Equal("Hatchback", f.BodyStyle);
        Assert.Equal("Blue", f.Colour);
    }

    [Fact]
    public void MultipleJsonLdBlocks_SelectsTheCarNodeByType_NotTheFirstBlock()
    {
        // The fixture leads with Organization, BreadcrumbList, and FAQPage blocks. Taking the
        // first script[type='application/ld+json'] would read the Organization node — which is
        // exactly where seller telephone numbers live.
        var result = Extract("multiple-jsonld-blocks.html");

        Assert.True(result.Success, result.Message);
        Assert.Equal("Toyota", result.Fields!.Make);
        Assert.Equal("Corolla", result.Fields.Model);
        Assert.Equal(2021, result.Fields.Year);
    }

    [Fact]
    public void MissingMandatoryField_FailsAsSchemaDrift_AndNamesTheField()
    {
        var result = Extract("missing-required-field.html");

        Assert.False(result.Success);
        Assert.Equal(ReasonCode.SchemaDrift, result.ReasonCode);

        // Naming the field is the point: SC-007 requires an operator to tell "extraction is
        // broken" from "nothing matched" without reading raw logs.
        Assert.Contains(VehicleFieldNames.Year, result.Message!, StringComparison.Ordinal);

        // Nothing partial is produced.
        Assert.Null(result.Fields);
    }

    [Fact]
    public void AbsentJsonLd_FailsLoudly_AndDoesNotSilentlyFallBackToSelectors()
    {
        // The fixture carries the data in CSS-reachable markup (data-price, data-mileage). An
        // automatic fallback would "succeed" here — and would be the FR-011 failure, because a
        // selector matching the wrong element yields plausible wrong data.
        var result = Extract("jsonld-absent.html");

        Assert.False(result.Success);
        Assert.Equal(ReasonCode.JsonLdAbsent, result.ReasonCode);
        Assert.Null(result.Fields);
    }

    [Fact]
    public void MalformedJsonLd_FailsAsMalformed_NotAsAbsent()
    {
        var result = Extract("jsonld-malformed.html");

        Assert.False(result.Success);
        Assert.Equal(ReasonCode.JsonLdMalformed, result.ReasonCode);
    }

    [Fact]
    public void ClientRenderedPage_IsReportedAsJsRenderRequired_NotAsAbsentJsonLd()
    {
        // Distinguishing these two saves an operator from hunting a selector bug on a host that
        // is unsupported by design (research D12).
        var result = Extract("js-render-required.html");

        Assert.False(result.Success);
        Assert.Equal(ReasonCode.JsRenderRequired, result.ReasonCode);
    }

    [Fact]
    public void FieldOutsideTheHostAllowlist_IsDiscardedAtProjectionTime()
    {
        // Colour and Vin are present in the fixture but omitted from this policy. The value
        // must be dropped during projection, not stored and filtered later (FR-010).
        var narrow = new HostAccessPolicy
        {
            Host = "dealer.example",
            PermittedFields =
            [
                VehicleFieldNames.Make, VehicleFieldNames.Model, VehicleFieldNames.Year,
                VehicleFieldNames.PriceAmount, VehicleFieldNames.PriceCurrency,
            ],
        };

        var result = Extract("valid-jsonld.html", narrow);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Volkswagen", result.Fields!.Make);
        Assert.Equal(12000.00m, result.Fields.PriceAmount);

        Assert.Null(result.Fields.Colour);
        Assert.Null(result.Fields.BodyStyle);
        Assert.Null(result.Fields.MileageValue);
        Assert.Null(result.Fields.Trim);
    }

    [Theory]
    [InlineData(1899, "Year")]
    [InlineData(2200, "Year")]
    public void YearOutsideSanityRange_FailsAsValidationRange_RatherThanBeingClamped(int year, string field)
    {
        // Out-of-range values indicate schema abuse. Clamping would store a fabricated value
        // that looks legitimate (research D9).
        var html = $$"""
            <!doctype html><html><head>
            <script type="application/ld+json">
            { "@context": "https://schema.org", "@type": "Car",
              "brand": { "@type": "Brand", "name": "Mazda" },
              "model": "6", "vehicleModelDate": "{{year}}" }
            </script></head><body></body></html>
            """;

        var result = new JsonLdCarExtractor().Extract(html, SourceUrl, FullPolicy).Single().Extraction;

        Assert.False(result.Success);
        Assert.Equal(ReasonCode.ValidationRange, result.ReasonCode);
        Assert.Contains(field, result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KilometreOdometer_IsRecordedWithItsUnit_NotSilentlyTreatedAsMiles()
    {
        var html = """
            <!doctype html><html><head>
            <script type="application/ld+json">
            { "@context": "https://schema.org", "@type": "Car",
              "brand": { "@type": "Brand", "name": "Volkswagen" },
              "model": "Golf", "vehicleModelDate": "2018",
              "mileageFromOdometer": { "@type": "QuantitativeValue", "value": 72000, "unitCode": "KMT" } }
            </script></head><body></body></html>
            """;

        var result = new JsonLdCarExtractor().Extract(html, SourceUrl, FullPolicy).Single().Extraction;

        Assert.True(result.Success, result.Message);
        Assert.Equal(72000, result.Fields!.MileageValue);
        Assert.Equal("km", result.Fields.MileageUnit);
    }

    [Fact]
    public void GraphWrappedCarNode_IsFound()
    {
        // @graph is the common wrapper when a page declares several entities in one block.
        var html = """
            <!doctype html><html><head>
            <script type="application/ld+json">
            { "@context": "https://schema.org", "@graph": [
                { "@type": "Organization", "name": "Dealer", "telephone": "+44 20 7946 0000" },
                { "@type": "Car", "brand": { "@type": "Brand", "name": "Audi" },
                  "model": "A4", "vehicleModelDate": "2019" } ] }
            </script></head><body></body></html>
            """;

        var result = new JsonLdCarExtractor().Extract(html, SourceUrl, FullPolicy).Single().Extraction;

        Assert.True(result.Success, result.Message);
        Assert.Equal("Audi", result.Fields!.Make);
    }
}
