<p align="center">
  <a href="https://killerpdf.net"><img src="docs/wordmark.png" height="180" alt="KillerPDF wordmark: a free, open-source PDF editor for Windows"></a>
</p>

# KillerPDF 1.9 "Overkill"

KillerPDF is a free, open-source PDF editor for Windows. Read, annotate, edit, organize, sign, redact and convert documents in one app. Version 1.9 uses KillerPDF's own engine for rendering, text extraction and document processing.

## What you can do

- Read documents in tabs or split panes, search text, follow bookmarks, and inspect comments, attachments, layers and signatures.
- Add text, drawings, highlights, stamps, images and reusable signatures. Edit supported source text, simple paragraphs, or matching text with Find and Replace.
- Merge, split, extract, reorder, rotate, crop and resize pages. Flatten copies with resolution, color and compression controls.
- Run OCR on scans, review recognized text, and create searchable copies. OCR language models are downloaded separately.
- Export documents to Word, Excel, PowerPoint, HTML, Markdown, text, JSON or page images. Review detected tables before exporting them to XLSX or CSV.
- Create and fill forms, recognize fields, flatten forms, and exchange supported form data and comments through FDF or XFDF.
- Mark content for redaction, review any required raster fallback, and save a separate redacted copy with verification and optional sanitization.
- Protect copies with passwords or certificate recipients. Sign PDFs and inspect signature integrity, certificate chains, timestamp evidence and later document changes.
- Use guided tasks, reusable macros, watched folders, data merge and command-line workflows for repeated work.
- Choose from 13 themes and 20 app languages, customize shortcuts, and adjust the interface scale.

Source-text and paragraph editing support specific layouts and fonts. Scanned pages may need OCR or overlays. Conversion can change document layout, so review exported files before sharing them. Signature reports distinguish failed checks from evidence that is unavailable or has not been checked.

## Get KillerPDF

Visit [killerpdf.net](https://killerpdf.net) for downloads and the [user guide](https://killerpdf.net/help). Install KillerPDF or use the portable package. The [release assets](https://github.com/SteveTheKiller/KillerPDF/releases) include the available packages and source archives.

## Build and test

Use Windows and the .NET SDK selected by [global.json](global.json).

```powershell
dotnet restore KillerPDF.csproj
dotnet build KillerPDF.csproj -c Release
dotnet test engine/KillerPdf.Engine.Tests/KillerPdf.Engine.Tests.csproj
dotnet test KillerPDF.Tests/KillerPDF.Tests.csproj
```

App tests include interactive WPF cases. Use an unlocked test desktop and follow the prerequisites in the [UI smoke guide](tests/KillerPDF.UiSmoke/README.md). Build and test one checkout at a time.

- [Application changelog](CHANGELOG.md)
- [Engine documentation](engine/README.md)
- [Engine changelog](engine/CHANGELOG.md)
- [Translation guide](TRANSLATING.md)

## License

KillerPDF is licensed under GPLv3. See [LICENSE](LICENSE) and the [1.9.0 corresponding-source archive](https://github.com/SteveTheKiller/KillerPDF/releases/download/v1.9.0/KillerPDF-1.9.0-src.zip). Third-party licenses and notices are included in the application payload.
