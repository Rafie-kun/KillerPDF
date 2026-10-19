# HTTP body deadlines and spreadsheet-safe batch reports

## BF-B007

The original GUI handoff and CLI OCR download both used `ResponseHeadersRead` without a body cancellation token. The GUI client timeout was 90 seconds and the OCR client timeout was 100 seconds. A completed header response left the body read outside that timeout.

Ranked hypotheses before the repair probe:

1. Headers complete the client's timeout scope. A linked token covering the body should cancel the stalled transfer.
2. The response stream ignores cancellation. Passing a token would still leave the transfer pending.
3. The destination file blocks. Using a MemoryStream would prevent reproduction.

A behavior-preserving extraction placed the original loop in the public `HttpDownload.CopyToAsync` service and routed both existing callers through it. The socket regression sends HTTP 200 headers, announces 100 bytes, sends one byte, and holds the connection open. With a 100ms HttpClient timeout and an expected 250ms transfer deadline, the extracted original loop remained pending after two seconds and failed. No TLS trust, hosts file, proxy, or machine network settings were changed.

The repair uses one linked token for headers, stream acquisition, every body read, and destination writes. GUI and OCR deadlines remain 90 and 100 seconds. The GUI maps OperationCanceledException to its existing localized timeout message and preserves the separate size-limit reason.

Final green result: eight checks passed, with the stalled body canceled after 272ms and one byte received. Exact successful bytes, declared oversize rejection before writing, unknown-length streaming oversize rejection, caller cancellation, and HTTP failure also passed. A final boundary review added red then green checks for negative byte limits and nonpositive deadlines (invalid policy), and a zero-byte limit accepting an empty response. Both production call sites compiled in the isolated candidate with zero warnings or errors; final argument guards were then source-linked tested and require the coordinator's combined build.

Run from `C:/Users/steve/code` with fresh scratch output paths:

```powershell
dotnet run --project KillerPDF/tests/KillerPDF.HttpDownloadSmoke/KillerPDF.HttpDownloadSmoke.csproj -p:BaseIntermediateOutputPath=C:/Users/steve/scratch/kp-boundaries-20261019/network-csv/http-obj/ -p:OutputPath=C:/Users/steve/scratch/kp-boundaries-20261019/network-csv/http-bin/
```

This proves the real socket behavior of the public production transfer service and compiled caller integration. It does not claim a 90-second actual HTTPS GUI handoff replay, a fixed GitHub OCR URL stall, rendered timeout-dialog inspection, or real OCR model installation. An interrupted CLI model remains unpublished as a `.part` file, consistent with existing body-download failure behavior. The CLI now opens its staging file before requesting headers, so a failure before successful headers can also leave an empty `.part` file. The final model is untouched until the full transfer succeeds. Partial-file cleanup and concurrent same-model download ownership were not redesigned. The GUI still closes and deletes its owned temporary file on any failure.

## BF-B008

LibreOffice was installed at `C:/Program Files/LibreOffice/program/soffice.com`; it was absent from PATH. No installation was needed. Every test used its own headless LibreOffice user profile under task scratch.

Ranked hypotheses:

1. The leading equals sign survives CSV syntax quoting and the spreadsheet interprets it as a formula.
2. Quoted CSV fields always become literal spreadsheet text.
3. KillerPDF itself evaluates the arithmetic before writing the report.

The actual original batch CLI accepted a valid extensionless PDF named `=1+1` and wrote `=1+1,OK,`. Importing this report with Calc's supported Evaluate formulas option enabled produced XLSX cell A2 with formula `1+1` and cached numeric value `2`. The expected-literal assertion failed. This establishes formula interpretation in that import configuration. No external formula, network payload, command execution, or broader impact was tested.

The report writer now prefixes an apostrophe for cells whose first non-whitespace character is `=`, `+`, `-`, or `@`, and for leading tab, CR, or LF. Normal CSV delimiter and quote escaping remains. The serialized prefix is intentionally part of the report text; it does not rename or modify input files.

The repaired actual CLI passed seven filename cases: equals, plus, minus, at sign, spaces before equals, ordinary filename, and comma-containing filename. Input bytes remained unchanged. The supported Calc import with formula evaluation enabled produced no formula and text `'=1+1`. The apostrophe remains visible in this import mode. The change is conservative report neutralization, not a claim of identical display across spreadsheet programs.

```powershell
python KillerPDF/tests/BatchCsvSpreadsheet.py C:/Users/steve/scratch/kp-boundaries-20261019/network-csv/candidate/KillerPDF.dll C:/Users/steve/scratch/kp-boundaries-20261019/network-csv/csv-green
```

The output directory must not already exist. Python uses only its standard library. The test creates synthetic PDFs, invokes the actual CLI, imports the resulting CSV, and reads XLSX formula/value cells.

The official [LibreOffice CSV filter documentation](https://help.libreoffice.org/latest/en-US/text/shared/guide/csv_params.html) documents token 13 as the formula-evaluation import option. The test explicitly enables it; this is not a statement about every UI default or a user's saved import settings.

## Build scope

The candidate was built from a scratch copy excluding `.git`, `bin`, `obj`, and the exact historical `adversarial-review-20261019-fresh` evidence directory, whose C# recipes otherwise enter the default application source glob. Original evidence and project files were preserved. SHA-256 comparisons confirmed the candidate source copies of HttpDownload.cs, ExternalOpen.cs, CliRunner.cs, and BatchRunner.cs matched the live repair files at build time. Subsequent argument guards were source-linked tested. Concurrent About updates were not yet in this snapshot, so this is a bounded network/CSV build, not final combined-candidate proof.
