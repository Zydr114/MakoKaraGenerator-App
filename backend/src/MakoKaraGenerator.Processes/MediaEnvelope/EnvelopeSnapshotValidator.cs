using System;
using System.Linq;
using System.Text;

namespace MakoKaraGenerator.Processes.MediaEnvelope;

/// <summary>Shared fixed owner and hard-control policy for local execution and independent observation.</summary>
internal static class EnvelopeSnapshotValidator
{
    internal static void ValidateControls(EnvelopeSnapshot value, ulong limit)
    {
        if (!value.ControlsComplete || !value.KernelKillAvailable || value.MainPid == 0 || value.MainStartTicks == 0 || !value.MainInControlGroup
            || !value.CgroupExists || value.CgroupInode == 0 || value.MemoryMax != limit || value.KernelMemoryMax != limit
            || value.EffectiveMemoryMax == 0 || value.EffectiveMemoryMax > limit || value.MemorySwapMax != 0 || value.KernelSwapMax != 0
            || !value.KernelOomGroup || !value.MemoryAccounting || value.Delegate || value.Type != "exec"
            || value.KillMode != "control-group" || value.Restart != "no" || value.OomPolicy != "kill")
            throw new EnvelopeException(EnvelopeFailure.CapacityUnsupported, "native-envelope-controls-unverified");
    }

    internal static EnvelopeIdentity CaptureOwner(EnvelopeSnapshot value, string nonce, string unit)
    {
        if (!IsHex(nonce) || value.UnitName != unit || value.UnitPath.Length == 0
            || value.Description != "Mako media envelope " + nonce || !IsHex(value.InvocationId)
            || value.InvocationId.All(c => c == '0')
            || value.ControlGroup.Length == 0 && !(value.Terminal && value.MainPid == 0 && value.CgroupRemoved)
            || value.ControlGroup.Length != 0 && (!value.ControlGroup.EndsWith('/' + unit, StringComparison.Ordinal) || value.CgroupInode == 0))
            throw new EnvelopeException(EnvelopeFailure.OwnershipLost, "unconfirmed-native-start-owner");
        return new(nonce, unit, value.UnitPath, value.InvocationId, value.ControlGroup, value.MainPid,
            value.MainStartTicks, value.CgroupDevice, value.CgroupInode);
    }

    internal static void VerifyIdentity(EnvelopeSnapshot value, EnvelopeIdentity identity)
    {
        // Keep the local executor's sequential manager/proc exit-race allowance.
        // Every fixed token remains exact; only live PID samples require membership.
        if (value.UnitName != identity.UnitName || value.UnitPath != identity.UnitPath || value.InvocationId != identity.InvocationId
            || value.Description != "Mako media envelope " + identity.Nonce || value.ControlGroup != identity.ControlGroup
            || value.CgroupDevice != identity.CgroupDevice || value.CgroupInode != identity.CgroupInode
            || value.MainPid != 0 && (value.MainPid != identity.MainPid || value.MainStartTicks != identity.StartTicks || !value.MainInControlGroup))
            throw new EnvelopeException(EnvelopeFailure.OwnershipLost, "native-envelope-owner-changed");
    }

    internal static void RequireDrained(EnvelopeSnapshot value)
    {
        if (!value.Terminal || value.CgroupPopulated || !(value.CgroupExists || value.CgroupRemoved))
            throw new EnvelopeException(EnvelopeFailure.DrainFailed, "native-unit-not-terminal");
    }

    internal static void ValidateObserverExpected(EnvelopeIdentity identity, ulong limit)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Nonce is null || identity.UnitName is null || identity.UnitPath is null
            || identity.InvocationId is null || identity.ControlGroup is null
            || !IsHex(identity.Nonce) || identity.UnitName != "mako-media-" + identity.Nonce + ".service"
            || !IsHex(identity.InvocationId) || identity.InvocationId.All(c => c == '0')
            || !ValidObjectPath(identity.UnitPath) || !Bounded(identity.UnitPath, 512)
            || !Bounded(identity.UnitName, 256) || !Bounded(identity.ControlGroup, 2048)
            || !identity.ControlGroup.StartsWith('/')
            || identity.ControlGroup.Contains("..", StringComparison.Ordinal)
            || identity.ControlGroup.Contains("//", StringComparison.Ordinal)
            || Encoding.UTF8.GetByteCount(identity.ControlGroup) > 2000
            || !identity.ControlGroup.EndsWith('/' + identity.UnitName, StringComparison.Ordinal)
            || identity.MainPid == 0 || identity.StartTicks == 0 || identity.CgroupInode == 0
            || limit == 0 || limit % 4096 != 0)
            throw new EnvelopeException(EnvelopeFailure.OwnershipLost, "native-observer-expected-invalid");
    }

    private static bool Bounded(string text, int bytes)
        => !text.Contains('\0') && Encoding.UTF8.GetByteCount(text) < bytes;

    private static bool ValidObjectPath(string path)
        => path.Length > 1 && path[0] == '/' && path[^1] != '/'
            && path.Split('/').Skip(1).All(part => part.Length != 0
                && part.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'));

    private static bool IsHex(string value)
        => value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
