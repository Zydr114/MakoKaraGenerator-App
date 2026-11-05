namespace MakoKaraGenerator.Media;

/// <summary>Canvas pixel edges in binary64; the query boundary validates a finite nonempty rectangle.</summary>
/// <remarks>The SDK Rect2 uses binary64 coordinates. Canvas inverse mapping preserves that precision without a binary32 intermediate.</remarks>
public readonly record struct MediaCanvasRect(double Left, double Top, double Right, double Bottom);
