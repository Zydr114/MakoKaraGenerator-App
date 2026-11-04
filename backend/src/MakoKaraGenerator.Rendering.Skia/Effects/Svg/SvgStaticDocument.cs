using System.Collections.Immutable;

namespace MakoKaraGenerator.Rendering.Skia.Effects.Svg;

// Only this compiler creates controlled XML. A future native loader must consume this exact result,
// never reopen the original source with an independent permissive parser.
internal sealed record SvgStaticDocument(string SafeXml, ImmutableArray<SvgSourceLocation> SourceMap,
    SvgResourceInspection Inspection, ImmutableDictionary<string, SvgValidatedImage> ValidatedImages);
internal sealed record SvgSourceLocation(int OutputElement, string Path, int Line, int Column);
internal sealed record SvgStaticPackage(PackageSvgInspection Inspection, ImmutableArray<SvgStaticDocument> Documents);
internal sealed record SvgValidatedImage(string Sha256, int Width, int Height, string Mime, ImmutableArray<byte> EncodedBytes);
