using System.Collections.Immutable;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting;

public sealed partial record FrameOutput
{
    // Full ordered transaction for host revalidation and global frame charging;
    // the public ImageEffects collection contains only the surviving bindings.
    internal ImmutableArray<TextWrite.GraphBinding> GraphWrites { get; init; } = [];
}
