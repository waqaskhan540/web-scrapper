using System.Net;
using CarIngest.Core.Abstractions;
using CarIngest.Core.Configuration;
using CarIngest.Infrastructure.Fetching;
using CarIngest.Infrastructure.Storage;
using CarIngest.Robots;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Polly;
using Polly.RateLimiting;
using Polly.Retry;
using Polly.Timeout;

namespace CarIngest.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>Connection string used by the service at runtime. Least-privilege.</summary>
    public const string RuntimeConnectionName = "Runtime";

    /// <summary>
    /// Connection string used only by migrations. Elevated, because CREATE EXTENSION requires
    /// superuser — confirmed empirically: a least-privileged role gets
    /// "permission denied to create extension". Splitting the identity is the fix; granting the
    /// app superuser is not (research D14).
    /// </summary>
    public const string MigratorConnectionName = "Migrator";

    public static IServiceCollection AddCarIngestOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CarIngestOptions>()
            .Bind(configuration.GetSection(CarIngestOptions.SectionName))

            // ValidateOnStart: a bad host policy must stop the process at boot, not surface as
            // a mid-run extraction failure on address 4,000 (data-model §3).
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<CarIngestOptions>, CarIngestOptionsValidator>();
        return services;
    }

    public static IServiceCollection AddCarIngestStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var runtimeConnection = configuration.GetConnectionString(RuntimeConnectionName)
            ?? throw new InvalidOperationException(
                $"ConnectionStrings:{RuntimeConnectionName} is required. " +
                $"Migrations use ConnectionStrings:{MigratorConnectionName} instead — the two are " +
                "deliberately different identities.");

        services.AddSingleton(_ =>
        {
            var builder = new NpgsqlDataSourceBuilder(runtimeConnection);

            // Registers the vector type with Npgsql. The EF provider needs its own UseVector()
            // below; both are required (research D3).
            builder.UseVector();
            return builder.Build();
        });

        services.AddDbContext<CarIngestDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>(), o => o.UseVector()));

        return services;
    }

    /// <summary>
    /// Registers the single named HttpClient that is the only egress path in the service.
    /// </summary>
    /// <remarks>
    /// "Only" is load-bearing. Because every outbound request goes through this client, and the
    /// fetcher consults robots before building a request, there is no second route out of the
    /// process that could skip the check (constitution gates I-1, I-2, I-3, T-3).
    /// </remarks>
    public static IServiceCollection AddCarIngestFetching(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<PerHostRateLimiter>();
        services.AddTransient<HostStampingHandler>();

        services.AddHttpClient(PageFetcher.HttpClientName)
            .ConfigureHttpClient((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<CarIngestOptions>>().Value;
                client.DefaultRequestHeaders.UserAgent.Clear();
                client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
                client.Timeout = Timeout.InfiniteTimeSpan; // the Polly timeout strategy owns this
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,

                // Redirects are followed manually by the fetcher so the destination host's own
                // robots policy is evaluated before we follow (spec edge case).
                AllowAutoRedirect = false,
            })

            // Ordered before the resilience handler so the limiter can read the host.
            .AddHttpMessageHandler<HostStampingHandler>()
            .AddResilienceHandler("per-host-politeness", (builder, context) =>
            {
                var limiter = context.ServiceProvider.GetRequiredService<PerHostRateLimiter>();
                var options = context.ServiceProvider
                    .GetRequiredService<IOptionsMonitor<CarIngestOptions>>();

                builder.AddRateLimiter(new RateLimiterStrategyOptions
                {
                    RateLimiter = args =>
                    {
                        var host = args.Context.Properties
                            .GetValue(HostStampingHandler.HostKey, "unknown");
                        return limiter.AcquireAsync(host, args.Context.CancellationToken);
                    },
                });

                builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
                {
                    MaxRetryAttempts = 3,
                    BackoffType = DelayBackoffType.Exponential,

                    // Jitter is required by gate I-2: synchronized retries across a run would
                    // hammer a struggling host in lockstep.
                    UseJitter = true,
                    Delay = TimeSpan.FromSeconds(2),
                    ShouldHandle = args => ValueTask.FromResult(
                        args.Outcome.Exception is HttpRequestException ||
                        args.Outcome.Result?.StatusCode is HttpStatusCode.TooManyRequests
                            or HttpStatusCode.ServiceUnavailable
                            or HttpStatusCode.BadGateway
                            or HttpStatusCode.GatewayTimeout),
                });

                builder.AddTimeout(new TimeoutStrategyOptions
                {
                    Timeout = options.CurrentValue.FetchTimeout,
                });
            });

        services.AddSingleton<IRobotsEvaluator>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<CarIngestOptions>>().Value;
            var factory = provider.GetRequiredService<IHttpClientFactory>();
            return new RobotsCache(
                factory.CreateClient(PageFetcher.HttpClientName),
                provider.GetRequiredService<ILogger<RobotsCache>>(),
                UserAgentToken(options.UserAgent),
                options.RobotsCacheLifetime);
        });

        services.AddSingleton<IPageFetcher>(provider =>
        {
            var factory = provider.GetRequiredService<IHttpClientFactory>();
            return new PageFetcher(
                factory.CreateClient(PageFetcher.HttpClientName),
                provider.GetRequiredService<IRobotsEvaluator>(),
                provider.GetRequiredService<PerHostRateLimiter>(),
                provider.GetRequiredService<IOptionsMonitor<CarIngestOptions>>(),
                provider.GetRequiredService<ILogger<PageFetcher>>());
        });

        return services;
    }

    /// <summary>
    /// Extracts the product token from a User-Agent string: robots.txt groups are matched on
    /// "CarIngestBot", not on the whole "CarIngestBot/1.0 (+https://...)" header value.
    /// </summary>
    internal static string UserAgentToken(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "*";
        }

        var firstToken = userAgent.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var slash = firstToken.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? firstToken[..slash] : firstToken;
    }
}
