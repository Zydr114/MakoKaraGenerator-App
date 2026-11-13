using System.Globalization;
using System.Security.Cryptography;

namespace MakoKaraGenerator.Backend.Server;

// The launch credential authenticates only private consumer claim/failure isolation, never a client operation.
internal sealed class DesktopConsumerIdentity
{
    private readonly byte[] _secret;
    private DesktopConsumerIdentity(string consumerId, byte[] secret, int mainPid)
    { ConsumerId = consumerId; _secret = secret; MainPid = mainPid; }

    internal string ConsumerId { get; }
    internal int MainPid { get; }

    internal static DesktopConsumerIdentity? FromEnvironment() => Create(
        Environment.GetEnvironmentVariable("MAKO_DESKTOP_CONSUMER_ID"),
        Environment.GetEnvironmentVariable("MAKO_DESKTOP_CONSUMER_SECRET"),
        Environment.GetEnvironmentVariable("MAKO_DESKTOP_MAIN_PID"));

    internal static DesktopConsumerIdentity? Create(string? consumerId, string? secret, string? mainPid)
    {
        if (consumerId is null && secret is null && mainPid is null) return null;
        if (consumerId is null || consumerId.Length != 32 || !consumerId.All(IsLowerHex) ||
            secret is null || secret.Length != 64 || !secret.All(IsLowerHex) ||
            !int.TryParse(mainPid, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0 ||
            pid.ToString(CultureInfo.InvariantCulture) != mainPid)
            throw new InvalidOperationException("Invalid desktop consumer launch configuration.");
        return new(consumerId, Convert.FromHexString(secret), pid);
    }

    internal bool Authenticate(string? consumerId, string? secret)
    {
        Span<byte> candidate = stackalloc byte[32];
        candidate.Clear();
        bool valid = secret is { Length: 64 } && secret.All(IsLowerHex);
        if (valid) Convert.FromHexString(secret!).CopyTo(candidate);
        bool matches = CryptographicOperations.FixedTimeEquals(_secret, candidate);
        return valid && matches && string.Equals(ConsumerId, consumerId, StringComparison.Ordinal);
    }

    private static bool IsLowerHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f';
}
