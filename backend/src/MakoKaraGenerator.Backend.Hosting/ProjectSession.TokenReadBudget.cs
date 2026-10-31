using System.Globalization;
using System.Text.Json;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Backend.Hosting;

public sealed partial class ProjectSession
{
    private long ValidatePreparedTokenLineBudget(LyricBranch branch, int index, LyricLine line)
    {
        ReadModelBudget budget = new(); budget.Text(ProjectSessionId, branch.Id);
        ProjectLyricLineDto projected = ProjectEditableLine(branch, line, budget);
        var response = new ProjectLyricsWindowResponse(ProjectSessionId,
            checked(ProjectRevision + 1).ToString(CultureInfo.InvariantCulture), branch.Id, index, branch.Lines.Length,
            NextOffset(index, 1, branch.Lines.Length), [projected]);
        using ReadModelCountingStream counter = new();
        JsonSerializer.Serialize(counter, response, ReadModelJson);
        return counter.Length;
    }
}
