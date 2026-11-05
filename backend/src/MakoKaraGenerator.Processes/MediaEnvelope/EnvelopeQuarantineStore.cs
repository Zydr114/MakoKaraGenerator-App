using System;
using System.Collections.Generic;

namespace MakoKaraGenerator.Processes.MediaEnvelope;

/// <summary>Worker-lifetime failed-terminal retention. Handles, native partitions and source leases remain owned.</summary>
/// <remarks>Construct this once in the worker composition root and keep it reachable for the worker lifetime.
/// This store never silently retries, disposes or converts a failed drain to success. A host may schedule a separately
/// audited bounded recovery job; ownership uncertainty must remain retained if that job cannot prove drain.</remarks>
public sealed class EnvelopeQuarantineStore : IEnvelopeQuarantine
{
    private readonly object _gate = new();
    private readonly List<EnvelopeQuarantine> _records = [];

    /// <summary>Current failed-terminal count. A nonzero count is an operational failure, not successful cleanup.</summary>
    public int Count { get { lock (_gate) return _records.Count; } }

    /// <summary>Retains the complete record for the worker lifetime without releasing any native or source ownership.</summary>
    public void Retain(EnvelopeQuarantine record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate) _records.Add(record);
    }

    /// <summary>Returns a bounded-by-failures snapshot for host-private recovery; never serialize its paths or handles.</summary>
    public IReadOnlyList<EnvelopeQuarantine> GetSnapshot()
    { lock (_gate) return _records.ToArray(); }
}
