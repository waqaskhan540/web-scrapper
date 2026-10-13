using System.Text.Json;
using CarIngest.Core.Documents;
using CarIngest.Core.Policies;
using CarIngest.Infrastructure.Extraction;
using CarIngest.Infrastructure.Tests.Harness;
using Xunit;

namespace CarIngest.Infrastructure.Tests.Extraction;

/// <summary>
/// The decisive test for constitution gate I-4.
/// </summary>
/// <remarks>
/// The fixture displays a seller's name, phone, email, and postal address in six places,
/// including inside the JSON-LD's own seller and description properties. These tests assert that
/// none of those literal strings survive into anything the system keeps: the projection, its
/// serialization, its canonical hash input, or the text handed to the embedding model.
/// <para>
/// Asserting on literal strings rather than on "the field is absent" is intentional. A future
/// change that widens the record, adds a description field, or starts copying an unmapped
/// property would still pass a structural check while failing this one.
/// </para>
/// </remarks>
public sealed class PiiContainmentTests
{
    private static readonly HostAccessPolicy Policy = new()
    {
        Host = "dealer.example",
        PermittedFields =
        [
            VehicleFieldNames.Make, VehicleFieldNames.Model, VehicleFieldNames.Year,
            VehicleFieldNames.Trim, VehicleFieldNames.MileageValue, VehicleFieldNames.MileageUnit,
            VehicleFieldNames.PriceAmount, VehicleFieldNames.PriceCurrency,
            VehicleFieldNames.FuelType, VehicleFieldNames.Transmission,
            VehicleFieldNames.BodyStyle, VehicleFieldNames.Colour,
        ],
    };

    private static VehicleFields ExtractPiiFixture()
    {
        var html = FixtureFiles.Read(Path.Combine("dealer.example", "seller-pii-present.html"));
        var result = new JsonLdCarExtractor()
            .Extract(html, new Uri("https://dealer.example/listing/mazda6"), Policy)
            .Single().Extraction;

        Assert.True(result.Success, result.Message);
        return result.Fields!;
    }

    [Fact]
    public void Extraction_Succeeds_SoTheTestIsActuallyExercisingAPopulatedRecord()
    {
        var fields = ExtractPiiFixture();

        // Guard against a vacuous pass: if extraction silently failed, every assertion below
        // would trivially hold on an empty record.
        Assert.Equal("Mazda", fields.Make);
        Assert.Equal("6", fields.Model);
        Assert.Equal(2016, fields.Year);
        Assert.Equal(62000, fields.MileageValue);
        Assert.Equal(7250.00m, fields.PriceAmount);
    }

    [Fact]
    public void SerializedRecord_ContainsNoSellerPii()
    {
        var json = JsonSerializer.Serialize(ExtractPiiFixture());

        foreach (var secret in FixtureFiles.PiiGoldenList)
        {
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CanonicalHashInput_ContainsNoSellerPii()
    {
        // The hash input is a second serialization path. It must be screened too, or a PII
        // string could reach a stored column by that route.
        var canonical = CanonicalHash.Canonicalize(ExtractPiiFixture());

        foreach (var secret in FixtureFiles.PiiGoldenList)
        {
            Assert.DoesNotContain(secret, canonical, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void EmbeddingText_ContainsNoSellerPii()
    {
        // The most important of the four: text that reaches the model becomes vector components,
        // which cannot be redacted after the fact.
        var text = new EmbeddingTextBuilder().Build(ExtractPiiFixture());

        foreach (var secret in FixtureFiles.PiiGoldenList)
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ProjectionType_DeclaresNoFreeTextOrCatchAllMember()
    {
        // Structural backstop. v1 deliberately stores no seller description, and the record has
        // no dictionary or JsonElement member, so unmapped page data is unrepresentable.
        var properties = typeof(VehicleFields).GetProperties();

        Assert.DoesNotContain(properties, p =>
            p.Name.Contains("Description", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("Seller", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("Contact", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("Phone", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("Telephone", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("Email", StringComparison.OrdinalIgnoreCase) ||
            p.Name.Contains("Address", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(properties, p =>
            typeof(System.Collections.IDictionary).IsAssignableFrom(p.PropertyType) ||
            p.PropertyType == typeof(JsonElement) ||
            p.PropertyType == typeof(JsonDocument) ||
            p.PropertyType == typeof(object));
    }

    [Theory]
    [InlineData("Call me on 07700 900461 any evening")]
    [InlineData("email jonathan.pemberton@seller-mailbox.example")]
    [InlineData("Ask for Jonathan Pemberton-Hayes at 14 Alder Crescent")]
    public void SellerDetailsInjectedIntoDescription_NeverReachTheProjection(string injected)
    {
        // Property-style check: wherever a seller puts their details in free text, the result is
        // the same, because no code path reads a description at all.
        var html = $$"""
            <!doctype html><html><head>
            <script type="application/ld+json">
            {
              "@context": "https://schema.org", "@type": "Car",
              "brand": { "@type": "Brand", "name": "Mazda" },
              "model": "6", "vehicleModelDate": "2016",
              "description": {{JsonSerializer.Serialize(injected)}},
              "offers": { "@type": "Offer", "price": "7250.00", "priceCurrency": "GBP",
                          "seller": { "@type": "Person", "name": {{JsonSerializer.Serialize(injected)}} } }
            }
            </script></head><body><p>{{injected}}</p></body></html>
            """;

        var result = new JsonLdCarExtractor()
            .Extract(html, new Uri("https://dealer.example/listing/x"), Policy)
            .Single().Extraction;

        Assert.True(result.Success, result.Message);
        var json = JsonSerializer.Serialize(result.Fields);
        var text = new EmbeddingTextBuilder().Build(result.Fields!);

        Assert.DoesNotContain(injected, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(injected, text, StringComparison.OrdinalIgnoreCase);
    }
}
