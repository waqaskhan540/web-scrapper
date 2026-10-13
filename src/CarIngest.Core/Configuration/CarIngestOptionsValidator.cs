using CarIngest.Core.Documents;
using CarIngest.Core.Policies;
using Microsoft.Extensions.Options;

namespace CarIngest.Core.Configuration;

/// <summary>
/// Fails startup on a bad configuration rather than letting it surface as a mid-run extraction
/// failure (data-model §3). A policy naming a field outside the allowlist is a deployment
/// mistake; discovering it on address 4,000 of a run is strictly worse than not booting.
/// </summary>
public sealed class CarIngestOptionsValidator : IValidateOptions<CarIngestOptions>
{
    public ValidateOptionsResult Validate(string? name, CarIngestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.UserAgent))
        {
            failures.Add("CarIngest:UserAgent is required — every request must identify the service and a contact address (FR-007).");
        }
        else if (!options.UserAgent.Contains('@', StringComparison.Ordinal) &&
                 !options.UserAgent.Contains("http", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("CarIngest:UserAgent must include a contact address or URL (FR-007).");
        }

        if (options.MaxContentBytes <= 0)
        {
            failures.Add("CarIngest:MaxContentBytes must be greater than zero (FR-012).");
        }

        if (options.Embedding.Dimensions is not (> 0 and <= 2000))
        {
            // An indexed pgvector column caps at 2000 dimensions (research D4). Allowing more
            // would silently forfeit the option of ever indexing the column.
            failures.Add("CarIngest:Embedding:Dimensions must be between 1 and 2000 — pgvector indexes cap at 2000 dimensions.");
        }

        if (options.Sources.Count == 0)
        {
            failures.Add("CarIngest:Sources must declare at least one address source location.");
        }

        foreach (var source in options.Sources.Where(s => string.IsNullOrWhiteSpace(s.Location)))
        {
            failures.Add("CarIngest:Sources[].Location must not be empty.");
            _ = source;
        }

        for (var i = 0; i < options.HostPolicies.Count; i++)
        {
            var policy = options.HostPolicies[i];

            if (string.IsNullOrWhiteSpace(policy.Host))
            {
                failures.Add($"CarIngest:HostPolicies:{i}:Host is required.");
                continue;
            }

            failures.AddRange(ValidatePolicy(policy.Host, policy));
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static IEnumerable<string> ValidatePolicy(string host, HostAccessPolicy policy)
    {
        var prefix = $"CarIngest:HostPolicies:{host}";

        if (policy.RequestsPerPeriod <= 0)
        {
            yield return $"{prefix}:RequestsPerPeriod must be greater than zero.";
        }

        if (policy.ReplenishmentPeriod <= TimeSpan.Zero)
        {
            yield return $"{prefix}:ReplenishmentPeriod must be greater than zero (FR-006).";
        }

        if (policy.MaxConcurrency <= 0)
        {
            yield return $"{prefix}:MaxConcurrency must be greater than zero — concurrency is bounded per host, not only globally (FR-006).";
        }

        if (policy.PermittedFields.Count == 0)
        {
            yield return $"{prefix}:PermittedFields must not be empty (FR-008).";
            yield break;
        }

        foreach (var field in policy.PermittedFields.Where(f => !VehicleFieldNames.All.Contains(f)))
        {
            yield return $"{prefix}:PermittedFields contains '{field}', which is not an allowlisted vehicle field. " +
                         $"Permitted values: {string.Join(", ", VehicleFieldNames.All.Order(StringComparer.Ordinal))}.";
        }

        var missingMandatory = VehicleFieldNames.Mandatory
            .Where(m => !policy.PermittedFields.Contains(m, StringComparer.Ordinal))
            .ToArray();

        if (missingMandatory.Length > 0)
        {
            yield return $"{prefix}:PermittedFields omits mandatory field(s) {string.Join(", ", missingMandatory)} — " +
                         "no document could ever be stored for this host (FR-011).";
        }

        if (policy.ExtractorKind == ExtractorKind.Css && string.IsNullOrWhiteSpace(policy.CssProfile))
        {
            yield return $"{prefix}:CssProfile is required when ExtractorKind is 'Css'.";
        }
    }
}
