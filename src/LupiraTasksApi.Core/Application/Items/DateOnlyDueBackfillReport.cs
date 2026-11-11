namespace LupiraTasksApi.Core.Application.Items;

/// <summary>Live timed dues counted by their local time of day, with a few samples each.</summary>
public sealed class DateOnlyDueBackfillReport(string zoneId, bool dryRun, IReadOnlyList<string> buckets)
{
    public Dictionary<string, int> Counts { get; } = buckets.ToDictionary(b => b, _ => 0);

    public Dictionary<string, List<string>> Samples { get; } = buckets.ToDictionary(b => b, _ => new List<string>());

    public int Converted { get; set; }

    public IEnumerable<string> Lines()
    {
        yield return $"Timed dues by local time ({zoneId}):";
        foreach (var (bucket, count) in Counts)
        {
            yield return $"  {bucket}: {count}";
            foreach (var sample in Samples[bucket]) yield return $"    {sample}";
        }

        yield return dryRun
            ? $"Dry run: {Counts[DateOnlyDueBackfill.EndOfDay]} would become date-only."
            : $"Converted {Converted} to date-only.";
    }
}
