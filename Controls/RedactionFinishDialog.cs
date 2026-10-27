using System.Windows;
using System.Windows.Controls;
using KillerPDF.Services;

namespace KillerPDF;

// Shown after a redaction is applied: the hidden information to remove as well, and whether to
// save the result as a new _Redacted copy so the original file stays untouched.
internal sealed class RedactionFinishDialog : Window
{
    private readonly CheckBox _comments;
    private readonly CheckBox _attachments;
    private readonly CheckBox _forms;
    private readonly CheckBox _layers;
    private readonly CheckBox _scripts;
    private readonly CheckBox _links;
    private readonly CheckBox _bookmarks;
    private readonly CheckBox _saveCopy;

    /// <summary>The hidden information to remove; metadata and page thumbnails always go.</summary>
    internal PdfEngineIntegration.SanitizeChoices? Choices { get; private set; }
    /// <summary>True when the result is saved as a new copy.</summary>
    internal bool SaveCopy { get; private set; }

    internal RedactionFinishDialog(Window owner, string copyName)
    {
        Title = "KillerPDF - " + L("Str_RedactFinish_Title");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        body.Children.Add(new TextBlock
        {
            Text = L("Str_RedactFinish_Message"),
            Foreground = UiKit.Brush("TextBrush"),
            FontFamily = UiKit.UiFont,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        CheckBox Option(string key)
        {
            CheckBox box = UiKit.CheckBox(L(key));
            box.IsChecked = true;
            box.Margin = new Thickness(0, 0, 0, 6);
            body.Children.Add(box);
            return box;
        }
        _comments = Option("Str_RedactFinish_Comments");
        _attachments = Option("Str_RedactFinish_Attachments");
        _forms = Option("Str_RedactFinish_Forms");
        _layers = Option("Str_RedactFinish_Layers");
        _scripts = Option("Str_RedactFinish_Scripts");
        _links = Option("Str_RedactFinish_Links");
        _bookmarks = Option("Str_RedactFinish_Bookmarks");

        body.Children.Add(new TextBlock
        {
            Text = L("Str_RedactFinish_Note"),
            Foreground = UiKit.Brush("MutedTextBrush"),
            FontFamily = UiKit.UiFont,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 12)
        });
        body.Children.Add(new Border
        {
            Height = 1,
            Background = UiKit.Brush("CardBorderBrush"),
            Opacity = 0.6,
            Margin = new Thickness(0, 0, 0, 12)
        });
        _saveCopy = UiKit.CheckBox(string.Format(L("Str_RedactFinish_SaveCopy"), copyName));
        _saveCopy.IsChecked = true;
        _saveCopy.Margin = new Thickness(0, 0, 0, 14);
        body.Children.Add(_saveCopy);

        var skip = UiKit.Make(L("Str_RedactFinish_Skip"), false);
        skip.IsCancel = true;
        skip.Click += (_, _) => { DialogResult = false; Close(); };
        var proceed = UiKit.Make(L("Str_RedactFinish_Continue"), true);
        proceed.IsDefault = true;
        proceed.Click += (_, _) => Commit();
        body.Children.Add(UiKit.ButtonRow(skip, proceed));

        Content = DialogFrame.Frame(this, owner, Title, () => { DialogResult = false; Close(); }, body);
    }

    private void Commit()
    {
        Choices = new PdfEngineIntegration.SanitizeChoices(Checked(_comments), Checked(_attachments),
            Checked(_forms), Checked(_layers), Checked(_scripts), Checked(_links), Checked(_bookmarks));
        SaveCopy = Checked(_saveCopy);
        DialogResult = true;
        Close();
    }

    private static bool Checked(CheckBox box) => box.IsChecked == true;

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
