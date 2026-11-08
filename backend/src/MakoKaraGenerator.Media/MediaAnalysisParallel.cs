using System.Runtime.ExceptionServices;

namespace MakoKaraGenerator.Media;

/// <summary>Bounded data parallelism for background analysis; every index writes only its own disjoint output.</summary>
/// <remarks>
/// Each index runs the same sequential arithmetic it would run alone, so results are bit-identical to a sequential pass.
/// Half the logical processors, at most eight, keeps preview and rendering responsive while analysis runs.
/// </remarks>
internal static class MediaAnalysisParallel
{
    /// <summary>The fixed upper bound of concurrently processed indexes.</summary>
    internal static int Degree { get; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    /// <summary>Runs <paramref name="body"/> for every index and rethrows the first failure with its original type.</summary>
    /// <param name="count">The number of independent indexes.</param>
    /// <param name="body">Work for one index.</param>
    /// <param name="cancellationToken">Cancellation propagated as <see cref="OperationCanceledException"/>.</param>
    internal static void For(int count, Action<int> body, CancellationToken cancellationToken)
    {
        try
        {
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Degree, CancellationToken = cancellationToken }, body);
        }
        catch (AggregateException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw();
        }
    }
}
