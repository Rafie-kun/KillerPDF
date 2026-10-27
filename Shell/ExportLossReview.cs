using System.Text;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private bool ConfirmExportLosses(PdfStructuredExportReport report)
    {
        var text = new StringBuilder(string.Format(Loc("Str_ExportLoss_Summary"), report.Findings.Count));
        foreach (PdfStructuredExportFinding finding in report.Findings)
        {
            string key = finding.Code switch
            {
                "VectorContentNotExported" => "Str_ExportLoss_Vector",
                "TextDirectionNotPreserved" => "Str_ExportLoss_Direction",
                "LinkContentNotExported" => "Str_ExportLoss_Link",
                "ImageContentNotExported" => "Str_ExportLoss_Image",
                "PageImagesNotEditable" => "Str_ExportDoc_PageImagesHint",
                _ => "Str_ExportLoss_Unknown"
            };
            string message = Loc(key);
            if (key == "Str_ExportLoss_Unknown") message += " (" + finding.Code + ")";
            text.AppendLine().AppendLine().Append(string.Format(Loc("Str_ExportLoss_Row"),
                string.Format(Loc("Str_PageLabel"), finding.PageIndex + 1), finding.Count, message));
        }
        return new ImportReviewDialog(this, Loc("Str_ExportDoc_Suffix"), text.ToString(), null,
            Loc("Str_Crash_Continue")).ShowDialog() == true;
    }
}
