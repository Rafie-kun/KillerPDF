# Reading PDFs

Open documents from memory, inspect unfamiliar input, and read high-level document features without exposing parser internals to the rest of your application.

## Open a document

```csharp
using KillerPdf.Engine.Documents;

byte[] source = File.ReadAllBytes("input.pdf");
PdfDocument document = PdfDocument.Open(source);
```

For an encrypted document, pass the user or owner password:

```csharp
PdfDocument document = PdfDocument.Open(source, password);
```

The document keeps its own copy of the supplied bytes, so your buffer can be reused after the call. Use `PdfDocument.OpenWithCompatibilityRecovery` when a viewer needs bounded recovery from a damaged file. Strict parsing remains the default.

## Inspect before opening

Use the non-throwing structural inspector when a file came from an unfamiliar or untrusted source:

```csharp
using KillerPdf.Engine.Diagnostics;

PdfInspectionReport report = PdfDocumentInspector.Inspect(source);
if (!report.IsStructurallyValid)
{
    foreach (PdfDiagnostic diagnostic in report.Diagnostics)
        Console.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
}
```

Encrypted input reports that authentication is required. Use `InspectAuthenticated` when your application has a password.

## Read document information

```csharp
PdfDocumentInformation information = PdfDocumentInformation.Read(document);

Console.WriteLine(information.Title);
Console.WriteLine(information.Author);
Console.WriteLine(information.Language);
Console.WriteLine(information.PageCount);
```

## Read page geometry

```csharp
IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(document);

for (int index = 0; index < pages.Count; index++)
{
    PdfPageInformation page = pages[index];
    Console.WriteLine($"Page {index + 1}: {page.Width} x {page.Height}, {page.Rotation} degrees");
}
```

The effective geometry accounts for inherited page boxes and rotation.

## Read interactive features

| Feature | Reader |
| --- | --- |
| Bookmarks | `PdfBookmarkReader` |
| Links | `PdfLinkReader` |
| Form widgets | `PdfFormWidgetReader` |
| Signatures | `PdfSignatureReader` |
| Attachments | `PdfAttachmentReader` |
| Comments | `PdfCommentReader` |
| Layers | `PdfOptionalContentReader` |

These readers return stable, high-level records with decoded text and normalized geometry. Your application does not need to traverse raw dictionaries for common tasks.

## Read form fields

Form widgets are read one page at a time. Each result includes the fully qualified field name, field type, current value, flags, options, and normalized page coordinates.

```csharp
IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(document);

for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
{
    foreach (PdfFormWidgetInfo widget in PdfFormWidgetReader.ReadPage(document, pageIndex))
    {
        Console.WriteLine($"{widget.FieldName}: {widget.Value}");
        Console.WriteLine($"  Type: {widget.FieldKind}");
        Console.WriteLine($"  Rectangle: {widget.Left}, {widget.Bottom}, {widget.Right}, {widget.Top}");
    }
}
```

An empty page returns an empty list. Page indexes are zero-based.

## Extract text and render pages

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;

var reader = new PdfPageContentReader(document);
PdfPageContent content = reader.Read(0);
foreach (PdfExtractedWord word in content.Words)
    Console.WriteLine($"{word.Text}: {word.BoundingBox}");

var renderer = new PdfPageRenderer(document);
PdfRenderedPage image = renderer.Render(0, new PdfRenderOptions(1024, 768));
```

Extraction returns words, text runs, lines, glyph bounds, fonts, images, paths, and shadings in unrotated, crop-relative PDF points. Rendering returns tightly packed BGRA32 pixels with a top-left origin, and the caller chooses how to display or encode them. Both report diagnostics for any recovery they applied.

## Know the boundary

KillerPDF 1.9 uses The KillerPDF.Engine for rendering, text extraction, and OCR. Tesseract remains available in the desktop app as a fallback OCR provider. See the developer guide for current engine APIs.
