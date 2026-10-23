namespace KillerPDF.Features
{
    // Counts batch-render page rows for the console summary and the exit code. A page kept by
    // --allow-incomplete is an OK page that is also counted as incomplete. A row whose PNG could
    // not be written is a failure, whatever its render diagnostics said.
    internal sealed class BatchRenderTally
    {
        internal int Ok { get; private set; }
        internal int Skipped { get; private set; }
        internal int Failed { get; private set; }
        internal int Incomplete { get; private set; }

        internal void Add(string status, bool incomplete)
        {
            if (status == "OK")
            {
                Ok++;
                if (incomplete) Incomplete++;
            }
            else if (status == "SKIP") Skipped++;
            else Failed++;
        }

        // Kept incomplete pages stay OK in the log so gate runs compare with their baselines; the
        // console and summary name them.
        internal static string ConsoleStatus(string status, bool incomplete) =>
            status == "OK" && incomplete ? "INCOMPLETE" : status;

        internal string Summary(int files, long renderMilliseconds, long elapsedMilliseconds)
        {
            string kept = Incomplete > 0 ? $" ({Incomplete} incomplete, kept by --allow-incomplete)" : string.Empty;
            return $"Done. {files} files, {Ok} pages OK{kept}, {Skipped} skipped, {Failed} failed, {renderMilliseconds} ms summed page rendering, {elapsedMilliseconds} ms elapsed.";
        }

        // 0 when every selected file rendered; 1 when a file could not render or the input held no PDFs.
        internal int ExitCode(int files) => files == 0 || Failed > 0 || Skipped > 0 ? 1 : 0;
    }
}
