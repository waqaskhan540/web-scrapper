using CarIngest.Core.Abstractions;
using CarIngest.Core.Policies;

namespace CarIngest.Infrastructure.Extraction;

public interface IExtractorSelector
{
    ICarExtractor Resolve(HostAccessPolicy policy);

    string VersionFor(HostAccessPolicy policy);
}

/// <summary>
/// Picks the extractor a host's policy declares.
/// </summary>
/// <remarks>
/// Selection is by configuration only. There is deliberately no "try JSON-LD, fall back to
/// selectors" path: a selector profile aimed at one host's markup will happily match something
/// on another host's page and return plausible wrong data, which is precisely the failure
/// FR-011 exists to surface rather than absorb (research D9).
/// </remarks>
public sealed class ExtractorSelector(
    JsonLdCarExtractor jsonLd,
    MicrodataCarExtractor microdata) : IExtractorSelector
{
    public ICarExtractor Resolve(HostAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return policy.ExtractorKind switch
        {
            ExtractorKind.Css => microdata,
            _ => jsonLd,
        };
    }

    public string VersionFor(HostAccessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return policy.ExtractorKind switch
        {
            ExtractorKind.Css => MicrodataCarExtractor.Version,
            _ => JsonLdCarExtractor.Version,
        };
    }
}
