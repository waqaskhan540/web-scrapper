using Microsoft.Extensions.Options;

namespace CarIngest.Infrastructure.Tests.Harness;

/// <summary>Fixed-value <see cref="IOptionsMonitor{T}"/> for tests that do not reload config.</summary>
public sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => this.CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

    private sealed class NullDisposable : IDisposable
    {
        internal static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
