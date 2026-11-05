using System.Collections.Immutable;

namespace MakoKaraGenerator.Core.Effects;

/// <summary>The fixed coordinate domain of a declared media query.</summary>
public enum EffectMediaCoordinateSpace : uint
{
    /// <summary>Unplaced media coordinates in the normalized source rectangle.</summary>
    SourceNormalized = 0,
    /// <summary>Placed and composed canvas coordinates in logical pixels.</summary>
    CanvasPixels = 1,
}

/// <summary>A fixed literal or a reference to the existing duration parameter type.</summary>
public abstract class EffectMediaWindow
{
    private EffectMediaWindow() { }
    /// <summary>An exact integer-microsecond literal.</summary>
    public sealed class Literal : EffectMediaWindow
    {
        internal Literal(long durationUs) { DurationUs = durationUs; }
        /// <summary>The nonnegative bounded window duration.</summary>
        public long DurationUs { get; }
    }
    /// <summary>A declared duration parameter, resolved by the unique package parameter reader.</summary>
    public sealed class Parameter : EffectMediaWindow
    {
        internal Parameter(string parameterId) { ParameterId = parameterId; }
        /// <summary>The same-package duration parameter identity.</summary>
        public string ParameterId { get; }
    }
}

/// <summary>A strictly read package query declaration; it does not grant sampling authority.</summary>
public sealed class EffectMediaQueryDefinition
{
    internal EffectMediaQueryDefinition(string id, EffectMediaCoordinateSpace space, EffectMediaWindow window)
    { Id = id; Space = space; Window = window; }
    /// <summary>The unique package-local identity.</summary>
    public string Id { get; }
    /// <summary>The exact coordinate domain.</summary>
    public EffectMediaCoordinateSpace Space { get; }
    /// <summary>The fixed window recipe.</summary>
    public EffectMediaWindow Window { get; }
}

/// <summary>References existing output ownership; it never introduces a second fallback material or graph.</summary>
/// <remarks>The initial contract requires each state to cover every output of the current export.</remarks>
public abstract class EffectMediaFallbackReference
{
    private EffectMediaFallbackReference(ImmutableArray<string> outputs) { OutputIds = outputs; }
    /// <summary>All current-export output identities in canonical UTF-8 order.</summary>
    public ImmutableArray<string> OutputIds { get; }
    /// <summary>The controller author explicitly owns the state branch for these outputs.</summary>
    public sealed class AuthorBranch : EffectMediaFallbackReference
    {
        internal AuthorBranch(ImmutableArray<string> outputs) : base(outputs) { }
    }
    /// <summary>Uses each existing output.Bypass, resolving to the same material fallback or graph input.</summary>
    public sealed class OutputBypass : EffectMediaFallbackReference
    {
        internal OutputBypass(ImmutableArray<string> outputs) : base(outputs) { }
    }
}

/// <summary>One export-local query permission with three explicitly declared unavailable-value states.</summary>
public sealed class EffectMediaQueryReference
{
    internal EffectMediaQueryReference(string queryId, EffectMediaFallbackReference pending,
        EffectMediaFallbackReference unavailable, EffectMediaFallbackReference noMedia)
    { QueryId = queryId; Pending = pending; Unavailable = unavailable; NoMedia = noMedia; }
    /// <summary>The exact package-local query identity.</summary>
    public string QueryId { get; }
    /// <summary>The explicitly declared pending branch.</summary>
    public EffectMediaFallbackReference Pending { get; }
    /// <summary>The explicitly declared failed-attempt branch.</summary>
    public EffectMediaFallbackReference Unavailable { get; }
    /// <summary>The explicitly declared no-background-contribution branch.</summary>
    public EffectMediaFallbackReference NoMedia { get; }
}
