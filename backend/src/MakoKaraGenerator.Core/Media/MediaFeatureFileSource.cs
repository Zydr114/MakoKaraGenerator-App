namespace MakoKaraGenerator.Core.Media;

/// <summary>The complete accepted file-source identity shared by decoder and feature builder.</summary>
public sealed record MediaFeatureFileSource
{
    /// <summary>Creates a read-only MediaFeatureFileSource value. Accepted boundaries must validate its complete contents.</summary>
    /// <param name="key">The recomputable file-source key.</param>
    /// <param name="kind">Still or video; a solid has no file-source artifact.</param>
    /// <param name="content">The exact source content identity.</param>
    /// <param name="presentation">The immutable selected stream descriptor.</param>
    /// <param name="tool">The controlled tool closure identity.</param>
    /// <param name="profile">The fixed feature profile bound to this tool and color.</param>
    public MediaFeatureFileSource(
        MediaSourceKey key,
        MediaSourceKind kind,
        MediaFileContent content,
        MediaFilePresentationDescriptor presentation,
        MediaToolIdentity tool,
        MediaProfileBundle profile)
    {
        Key = key;
        Kind = kind;
        Content = content;
        Presentation = presentation;
        Tool = tool;
        Profile = profile;
    }

    /// <summary>The recomputable file-source key.</summary>
    public MediaSourceKey Key { get; }

    /// <summary>Still or video; a solid has no file-source artifact.</summary>
    public MediaSourceKind Kind { get; }

    /// <summary>The exact source content identity.</summary>
    public MediaFileContent Content { get; }

    /// <summary>The immutable selected stream descriptor.</summary>
    public MediaFilePresentationDescriptor Presentation { get; }

    /// <summary>The controlled tool closure identity.</summary>
    public MediaToolIdentity Tool { get; }

    /// <summary>The fixed feature profile bound to this tool and color.</summary>
    public MediaProfileBundle Profile { get; }

}
