using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace MakoKaraGenerator.Backend.Server;

/// <summary>Only the trusted launcher can supply the bootstrap credential and web origins.</summary>
internal sealed class LoopbackSecurity : IDisposable
{
    private readonly HashSet<string> _authorities = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _origins = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _credentialFile;
    private bool _ownsCredentialFile;

    public LoopbackSecurity(string resourceRoot)
    {
        string? inherited = Environment.GetEnvironmentVariable("MAKO_BACKEND_BOOTSTRAP_TOKEN");
        if (inherited is not null && (inherited.Length < 32 || inherited.Length > 256 || inherited.Any(char.IsWhiteSpace)))
            throw new ArgumentException("The inherited bootstrap credential must contain 32 to 256 non-whitespace characters.");
        BootstrapToken = inherited ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _credentialFile = Environment.GetEnvironmentVariable("MAKO_BACKEND_CREDENTIAL_FILE")
            ?? (inherited is null ? Path.Combine(resourceRoot, "bootstrap.json") : null);
        foreach (string origin in (Environment.GetEnvironmentVariable("MAKO_BACKEND_ALLOWED_ORIGINS") ?? string.Empty)
                     .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? parsed) || parsed.Scheme is not ("http" or "https")
                || parsed.AbsolutePath != "/" || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment)
                || !string.IsNullOrEmpty(parsed.UserInfo))
                throw new ArgumentException("Allowed origins must be explicit HTTP(S) origins without a path.");
            _origins.Add(parsed.GetLeftPart(UriPartial.Authority));
        }
    }

    public string BootstrapToken { get; }

    public void Started(IServiceProvider services, string hostInstanceId)
    {
        string[] addresses = services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.ToArray()
            ?? throw new InvalidOperationException("The loopback listener did not publish its addresses.");
        foreach (string address in addresses)
        {
            Uri uri = new(address);
            if (uri.Scheme is not ("http" or "https") || !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(uri.Host, out IPAddress? ip) && IPAddress.IsLoopback(ip))))
                throw new InvalidOperationException("The actual configured listener must remain on loopback.");
            _authorities.Add(uri.Authority);
            _origins.Add(uri.GetLeftPart(UriPartial.Authority));
        }
        if (_credentialFile is null) return;
        string path = Path.GetFullPath(_credentialFile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Never overwrite a caller's file or follow a pre-existing link. The launcher owns this path.
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using FileStream stream = new(path, options);
        _ownsCredentialFile = true;
        JsonSerializer.Serialize(stream, new { protocolVersion = "1", hostInstanceId, addresses, bootstrapToken = BootstrapToken });
        stream.Flush(flushToDisk: true);
    }

    public bool IsAllowed(HttpContext context, out bool originAllowed)
    {
        originAllowed = false;
        if (context.Connection.LocalIpAddress is not { } local || !IPAddress.IsLoopback(local)
            || context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote)
            || !_authorities.Contains(context.Request.Host.Value ?? string.Empty)) return false;
        string[] origins = context.Request.Headers.Origin.ToArray()!;
        if (origins.Length == 0) return true;
        if (origins.Length != 1 || string.IsNullOrEmpty(origins[0]) || !_origins.Contains(origins[0])) return false;
        originAllowed = true;
        return true;
    }

    public static string? Bearer(HttpRequest request)
    {
        if (request.Headers.Authorization.Count != 1) return null;
        string? value = request.Headers.Authorization[0];
        if (value is null || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        string token = value[7..];
        return token.Length is >= 32 and <= 512 && !token.Any(char.IsWhiteSpace) ? token : null;
    }

    public void Dispose()
    {
        if (_ownsCredentialFile && _credentialFile is not null)
        {
            try { File.Delete(Path.GetFullPath(_credentialFile)); } catch (IOException) { }
            _ownsCredentialFile = false;
        }
    }

    public static string ResolveUrls(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? urls = null;
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument.Equals("--urls", StringComparison.Ordinal))
            {
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
                    throw new ArgumentException("--urls 必须提供监听地址。", nameof(args));
                urls = args[index];
                break;
            }
            if (argument.StartsWith("--urls=", StringComparison.Ordinal))
            {
                urls = argument[7..];
                if (string.IsNullOrWhiteSpace(urls))
                    throw new ArgumentException("--urls 必须提供监听地址。", nameof(args));
                break;
            }
        }
        urls ??= Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://127.0.0.1:0";
        string[] endpoints = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (endpoints.Length == 0) throw new ArgumentException("Only explicit loopback listeners are supported.");
        foreach (string url in endpoints)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme is not ("http" or "https")
                || !string.IsNullOrEmpty(endpoint.UserInfo) || endpoint.AbsolutePath != "/" || !string.IsNullOrEmpty(endpoint.Query)
                || !string.IsNullOrEmpty(endpoint.Fragment)
                || !(string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                    || (IPAddress.TryParse(endpoint.Host, out IPAddress? address) && IPAddress.IsLoopback(address))))
                throw new ArgumentException("Only explicit loopback listeners are supported.");
        }
        return urls;
    }
}
