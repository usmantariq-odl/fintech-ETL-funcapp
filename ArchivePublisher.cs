namespace CSVReader;

internal static class ArchivePublisher
{
    // Publish every required copy before moving/deleting the input. A failed copy leaves it retryable.
    public static void Publish(EtlOutcome outcome, string original, Action<ArchiveOutput> copy,
        Action<ArchiveOutput> move, Action delete)
    {
        if (outcome.Outputs.Count == 0) throw new InvalidOperationException("ETL produced no archive outputs.");
        for (var i = 0; i < outcome.Outputs.Count; i++)
        {
            var output = outcome.Outputs[i];
            if (i == outcome.Outputs.Count - 1 && output.LocalPath == original) move(output);
            else copy(output);
        }
        if (outcome.Outputs[^1].LocalPath != original) delete();
    }
}
