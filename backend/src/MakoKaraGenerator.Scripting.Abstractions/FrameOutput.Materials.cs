using System.Collections.Immutable;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting;

public sealed partial record FrameOutput
{
    // Raw v2 writes survive replacement for owning/schema/budget admission.
    internal ImmutableArray<TextWrite> MaterialWrites { get; init; } = [];
}
