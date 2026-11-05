namespace MakoKaraGenerator.Media;

/// <summary>A nonempty source-normalized rectangle expressed directly as binary32 coordinates.</summary>
public readonly record struct MediaSourceRect
{
    /// <summary>Creates an immutable numerical value; accepted boundaries validate all fields.</summary>
    /// <param name="left">The inclusive left boundary in the closed interval from zero to one.</param>
    /// <param name="top">The inclusive top boundary in the closed interval from zero to one.</param>
    /// <param name="right">The exclusive right boundary strictly greater than left and at most one.</param>
    /// <param name="bottom">The exclusive bottom boundary strictly greater than top and at most one.</param>
    public MediaSourceRect(
        float left,
        float top,
        float right,
        float bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    /// <summary>The inclusive left boundary in the closed interval from zero to one.</summary>
    public float Left { get; }

    /// <summary>The inclusive top boundary in the closed interval from zero to one.</summary>
    public float Top { get; }

    /// <summary>The exclusive right boundary strictly greater than left and at most one.</summary>
    public float Right { get; }

    /// <summary>The exclusive bottom boundary strictly greater than top and at most one.</summary>
    public float Bottom { get; }

}
