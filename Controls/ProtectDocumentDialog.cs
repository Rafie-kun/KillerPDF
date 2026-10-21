using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using KillerPDF.Services;
using KillerPDF.Services.Signing;
using KillerPdf.Engine.Security;

namespace KillerPDF
{
    /// <summary>
    /// Themed modal dialog that writes an AES-256 encrypted copy of the saved document, protected
    /// either with passwords and permission restrictions or for certificate recipients. The open
    /// file is never changed. Chrome and helpers mirror SignDocumentDialog.
    /// </summary>
    internal sealed class ProtectDocumentDialog : Window
    {
        private readonly string _sourcePdf;
        private readonly string _displayPath;

        private RadioButton _passwordRadio = null!;
        private RadioButton _recipientRadio = null!;
        private PasswordBox _passwordBox = null!;
        private PasswordBox _confirmBox = null!;
        private PasswordBox _ownerBox = null!;
        private ComboBox _storeCombo = null!;
        private Button _addStore = null!;
        private Button _addFile = null!;
        private Button _remove = null!;
        private ListBox _recipientList = null!;
        private CheckBox _allowPrint = null!;
        private CheckBox _allowCopy = null!;
        private CheckBox _allowEdit = null!;
        private TextBox _outputBox = null!;
        private readonly List<X509Certificate2> _storeCerts = [];
        private readonly List<X509Certificate2> _recipients = [];

        private static SolidColorBrush R(string key) => (SolidColorBrush)Application.Current.Resources[key];

        // Localized string from the active locale dictionary (falls back to the key if missing).
        private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;

        /// <param name="sourcePdf">The unencrypted file to protect.</param>
        /// <param name="displayPath">The user's file name, used for the description and default output.</param>
        public ProtectDocumentDialog(Window? owner, string sourcePdf, string displayPath)
        {
            _sourcePdf = sourcePdf;
            _displayPath = displayPath;
            Title = "KillerPDF - " + L("Str_Protect_Name");
            Width = 620;
            MaxHeight = 860;
            SizeToContent = SizeToContent.Height;
            UseLayoutRounding = true;
            DialogChrome.Configure(this, owner);
            BuildUi();
        }

        private void BuildUi()
        {
            var body = new StackPanel { Margin = new Thickness(20, 6, 20, 18) };

            body.Children.Add(new TextBlock
            {
                Text = string.Format(L("Str_Protect_Desc"), Path.GetFileName(_displayPath)),
                Foreground = R("MutedTextBrush"), FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            });

            // --- Passwords -----------------------------------------------------------------------
            body.Children.Add(Label(L("Str_Protect_Method")));
            _passwordRadio = Radio(L("Str_Protect_WithPassword"), true);
            _recipientRadio = Radio(L("Str_Protect_ForRecipients"), false);
            _passwordRadio.Checked += (_, _) => SyncMode();
            _recipientRadio.Checked += (_, _) => SyncMode();
            body.Children.Add(_passwordRadio);

            body.Children.Add(Hint(L("Str_Protect_Password"), new Thickness(20, 4, 0, 2)));
            _passwordBox = Password(); body.Children.Add(_passwordBox);
            body.Children.Add(Hint(L("Str_Protect_Confirm"), new Thickness(20, 2, 0, 2)));
            _confirmBox = Password(); body.Children.Add(_confirmBox);
            body.Children.Add(Hint(L("Str_Protect_OwnerPassword"), new Thickness(20, 2, 0, 2)));
            _ownerBox = Password(); body.Children.Add(_ownerBox);
            var ownerHint = Hint(L("Str_Protect_OwnerHint"), new Thickness(20, 0, 0, 10));
            ownerHint.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(ownerHint);

            // --- Certificate recipients ----------------------------------------------------------
            body.Children.Add(_recipientRadio);
            var recipientHint = Hint(L("Str_Protect_RecipientHint"), new Thickness(20, 2, 0, 4));
            recipientHint.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(recipientHint);

            var storeRow = new Grid { Margin = new Thickness(20, 2, 0, 4) };
            storeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            storeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _storeCombo = new ComboBox { Height = 26, Margin = new Thickness(0, 0, 6, 0) };
            ApplyComboStyle(_storeCombo);
            try
            {
                foreach (var c in WindowsCertificateStore.ListEncryptionCertificates())
                {
                    _storeCerts.Add(c);
                    _storeCombo.Items.Add(new StoreCertificateProvider(c).DisplayName);
                }
            }
            catch { /* store unavailable - certificate files still work */ }
            if (_storeCombo.Items.Count > 0) _storeCombo.SelectedIndex = 0;
            Grid.SetColumn(_storeCombo, 0);
            _addStore = MakeButton(L("Str_Protect_AddMine"), false);
            _addStore.Click += (_, _) => AddStoreCertificate();
            Grid.SetColumn(_addStore, 1);
            storeRow.Children.Add(_storeCombo);
            storeRow.Children.Add(_addStore);
            body.Children.Add(storeRow);

            _recipientList = new ListBox
            {
                Height = 86, Margin = new Thickness(20, 2, 0, 4),
                Background = R("BgCanvas"), Foreground = R("TextBrush"),
                BorderBrush = R("CardBorderBrush"), BorderThickness = new Thickness(1)
            };
            _recipientList.SelectionChanged += (_, _) => SyncMode();
            body.Children.Add(_recipientList);

            var listButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 2, 0, 10) };
            _addFile = MakeButton(L("Str_Protect_AddFile"), false);
            _addFile.Click += (_, _) => AddCertificateFile();
            _remove = MakeButton(L("Str_Protect_Remove"), false);
            _remove.Margin = new Thickness(8, 0, 0, 0);
            _remove.Click += (_, _) => RemoveRecipient();
            listButtons.Children.Add(_addFile);
            listButtons.Children.Add(_remove);
            body.Children.Add(listButtons);

            // --- Permissions ---------------------------------------------------------------------
            body.Children.Add(Label(L("Str_Protect_Allow")));
            _allowPrint = Check(L("Str_Protect_AllowPrint"));
            _allowCopy = Check(L("Str_Protect_AllowCopy"));
            _allowEdit = Check(L("Str_Protect_AllowEdit"));
            body.Children.Add(_allowPrint);
            body.Children.Add(_allowCopy);
            body.Children.Add(_allowEdit);

            // --- Output --------------------------------------------------------------------------
            body.Children.Add(Label(L("Str_Protect_SaveAs")));
            var outRow = new Grid();
            outRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            outRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _outputBox = Field(DefaultOutputPath());
            _outputBox.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(_outputBox, 0);
            var browseOut = MakeButton(L("Str_Sign_Browse"), false);
            browseOut.Click += (_, _) => BrowseOutput();
            Grid.SetColumn(browseOut, 1);
            outRow.Children.Add(_outputBox);
            outRow.Children.Add(browseOut);
            body.Children.Add(outRow);

            // --- Buttons -------------------------------------------------------------------------
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var protect = MakeButton(L("Str_Protect_Protect"), true);
            protect.Click += (_, _) => DoProtect();
            protect.IsDefault = true;    // Enter
            var cancel = MakeButton(L("Str_Sign_Cancel"), false);
            cancel.Margin = new Thickness(8, 0, 0, 0);
            cancel.Click += (_, _) => { DialogResult = false; Close(); };
            cancel.IsCancel = true;   // Esc
            btnRow.Children.Add(protect);
            btnRow.Children.Add(cancel);
            body.Children.Add(btnRow);

            SyncMode();
            Loaded += (_, _) => _passwordBox.Focus();

            var scroll = new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Content = DialogChrome.Frame(this, Owner, "KillerPDF - " + L("Str_Protect_Name"),
                () => { DialogResult = false; Close(); }, scroll);
        }

        private string DefaultOutputPath()
        {
            string dir = Path.GetDirectoryName(_displayPath) ?? "";
            string name = Path.GetFileNameWithoutExtension(_displayPath);
            return Path.Combine(dir, name + "-protected.pdf");
        }

        // Enable only the inputs for the selected protection.
        private void SyncMode()
        {
            bool passwords = _passwordRadio.IsChecked == true;
            _passwordBox.IsEnabled = _confirmBox.IsEnabled = _ownerBox.IsEnabled = passwords;
            _storeCombo.IsEnabled = _addStore.IsEnabled = !passwords && _storeCerts.Count > 0;
            _addFile.IsEnabled = _recipientList.IsEnabled = !passwords;
            _remove.IsEnabled = !passwords && _recipientList.SelectedIndex >= 0;
        }

        private void AddStoreCertificate()
        {
            int i = _storeCombo.SelectedIndex;
            if (i >= 0 && i < _storeCerts.Count) AddRecipient(_storeCerts[i]);
        }

        private void AddCertificateFile()
        {
            var dlg = new KillerPDF.Controls.FileDialog(KillerPDF.Controls.FileDialogMode.Open)
            {
                Filter = L("Str_Filter_Cert") + "|*.cer;*.crt;*.pem;*.der|" + L("Str_Filter_AllFiles") + "|*.*",
                Title = L("Str_Protect_ChooseCert")
            };
            if (dlg.ShowDialog(this) != true) return;
            X509Certificate2 certificate;
            try { certificate = LoadPublicCertificate(dlg.FileName); }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                Warn(L("Str_Protect_BadCertificate"));
                return;
            }
            AddRecipient(certificate);
        }

        // A recipient file holds one public certificate, as DER or PEM text.
        private static X509Certificate2 LoadPublicCertificate(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            string text = System.Text.Encoding.ASCII.GetString(data);
            return text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
                ? X509Certificate2.CreateFromPem(text)
                : X509CertificateLoader.LoadCertificate(data);
        }

        private void AddRecipient(X509Certificate2 certificate)
        {
            if (!PdfEngineIntegration.CanEncryptForRecipient(certificate)) { Warn(L("Str_Protect_BadCertificate")); return; }
            if (_recipients.Any(existing => existing.Thumbprint == certificate.Thumbprint)) return;
            _recipients.Add(certificate);
            _recipientList.Items.Add(new StoreCertificateProvider(certificate).DisplayName);
            _recipientList.SelectedIndex = _recipientList.Items.Count - 1;
        }

        private void RemoveRecipient()
        {
            int i = _recipientList.SelectedIndex;
            if (i < 0 || i >= _recipients.Count) return;
            _recipients.RemoveAt(i);
            _recipientList.Items.RemoveAt(i);
            _recipientList.SelectedIndex = Math.Min(i, _recipientList.Items.Count - 1);
            SyncMode();
        }

        private void BrowseOutput()
        {
            var dlg = new KillerPDF.Controls.FileDialog(KillerPDF.Controls.FileDialogMode.Save)
                          { Filter = L("Str_Filter_Pdf") + "|*.pdf", Title = L("Str_Protect_SaveAs"), FileName = Path.GetFileName(_outputBox.Text) };
            if (dlg.ShowDialog(this) == true) _outputBox.Text = dlg.FileName;
        }

        private void DoProtect()
        {
            bool passwords = _passwordRadio.IsChecked == true;
            if (passwords)
            {
                if (_passwordBox.Password.Length == 0 && _ownerBox.Password.Length == 0)
                { Warn(L("Str_Protect_NeedPassword")); return; }
                if (_passwordBox.Password != _confirmBox.Password)
                { Warn(L("Str_Protect_Mismatch")); return; }
            }
            else if (_recipients.Count == 0) { Warn(L("Str_Protect_NeedRecipient")); return; }

            string output = _outputBox.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(output)) { Warn(L("Str_Protect_NeedOutput")); return; }
            string fullOutput;
            try { fullOutput = Path.GetFullPath(output); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { Warn(L("Str_Protect_NeedOutput")); return; }
            if (SamePath(fullOutput, _displayPath) || SamePath(fullOutput, _sourcePdf))
            { Warn(L("Str_Protect_SameFile")); return; }
            if (!Services.OutputOverwrite.Confirm(this, fullOutput, out Services.FileRevision? approved)) return;

            bool signed;
            try { signed = PdfEngineIntegration.HasSignedSignatures(_sourcePdf); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { signed = false; }
            if (signed && KillerDialog.Show(this, L("Str_Protect_SignedWarning"), L("Str_Protect_Name"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            bool print = _allowPrint.IsChecked == true;
            bool copy = _allowCopy.IsChecked == true;
            bool edit = _allowEdit.IsChecked == true;
            if (!Services.OutputOverwrite.StillAsApproved(this, fullOutput, approved)) return;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                if (passwords)
                {
                    // Without a permissions password, a random one keeps the restrictions in force;
                    // an empty one would let anyone open the copy with full access.
                    string owner = _ownerBox.Password.Length > 0
                        ? _ownerBox.Password
                        : Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
                    PdfEngineIntegration.ProtectWithPasswords(_sourcePdf, fullOutput,
                        new PdfPasswordEncryptionOptions
                        {
                            UserPassword = _passwordBox.Password,
                            OwnerPassword = owner,
                            AllowLowQualityPrinting = print,
                            AllowHighQualityPrinting = print,
                            AllowContentCopying = copy,
                            AllowDocumentModification = edit,
                            AllowAnnotationModification = edit,
                            AllowFormFilling = edit,
                            AllowDocumentAssembly = edit
                        }, signed);
                }
                else
                {
                    PdfEngineIntegration.ProtectForRecipients(_sourcePdf, fullOutput,
                        new PdfCertificateEncryptionOptions
                        {
                            Recipients = [.. _recipients],
                            AllowLowQualityPrinting = print,
                            AllowHighQualityPrinting = print,
                            AllowContentCopying = copy,
                            AllowDocumentModification = edit,
                            AllowAnnotationModification = edit,
                            AllowFormFilling = edit,
                            AllowDocumentAssembly = edit
                        }, signed);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Mouse.OverrideCursor = null;
                Warn(L("Str_Protect_Failed") + "\n\n" + ex.GetType().Name + ": " + ex.Message);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            KillerDialog.Show(this, L("Str_Protect_SavedTo") + "\n" + fullOutput, L("Str_Protect_Name"), MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }

        private static bool SamePath(string fullPath, string other)
        {
            try { return string.Equals(fullPath, Path.GetFullPath(other), StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        }

        private void Warn(string msg) => KillerDialog.Show(this, msg, L("Str_Protect_Name"), MessageBoxButton.OK, MessageBoxImage.Warning);

        // ---- themed control helpers (mirroring SignDocumentDialog) -------------------------------
        private Style? FindOwnerStyle(string key) => Owner?.TryFindResource(key) as Style;

        private static TextBlock Label(string text) => new()
        { Text = text, Foreground = R("TextBrush"), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) };

        private static TextBlock Hint(string text, Thickness margin) => new()
        { Text = text, Foreground = R("MutedTextBrush"), FontSize = 11, Margin = margin };

        private RadioButton Radio(string text, bool isChecked)
        {
            var r = new RadioButton { Content = text, IsChecked = isChecked, GroupName = "ProtectMode", FontSize = 12, Margin = new Thickness(0, 4, 0, 2) };
            if (FindOwnerStyle("ThemeRadio") is Style s) r.Style = s; else r.Foreground = R("TextBrush");
            return r;
        }

        private CheckBox Check(string text)
        {
            var c = new CheckBox { Content = text, IsChecked = true, Foreground = R("TextBrush"), Margin = new Thickness(20, 2, 0, 2) };
            if (FindOwnerStyle("ThemeCheckBox") is Style s) c.Style = s;
            return c;
        }

        private void ApplyComboStyle(ComboBox combo)
        {
            if (FindOwnerStyle("DarkComboBox") is Style s) combo.Style = s;
            else { combo.Foreground = R("TextBrush"); combo.BorderBrush = R("CardBorderBrush"); }
            combo.Background = R("BgCanvas");
        }

        private static PasswordBox Password() => new()
        {
            Margin = new Thickness(20, 0, 0, 4),
            Background = R("BgCanvas"), Foreground = R("TextBrush"),
            BorderBrush = R("CardBorderBrush"), BorderThickness = new Thickness(1),
            CaretBrush = R("TextBrush"), Template = MakePasswordTemplate()
        };

        private static TextBox Field(string text)
        {
            var tb = new TextBox
            {
                Text = text, Margin = new Thickness(0, 0, 0, 4),
                Background = R("BgCanvas"), Foreground = R("TextBrush"),
                BorderBrush = R("CardBorderBrush"), BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 6, 4), CaretBrush = R("TextBrush"),
                SelectionBrush = R("RowSelectedBrush"), SelectionTextBrush = R("TextBrush"),
                Template = MakeTextBoxTemplate()
            };
            return tb;
        }

        private static ControlTemplate MakeTextBoxTemplate()
        {
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            var sv = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
            b.AppendChild(sv);
            var ct = new ControlTemplate(typeof(TextBox)) { VisualTree = b };
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
            ct.Triggers.Add(disabled);
            return ct;
        }

        private static ControlTemplate MakePasswordTemplate()
        {
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            b.SetValue(Border.PaddingProperty, new Thickness(6, 4, 6, 4));
            var sv = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
            b.AppendChild(sv);
            var ct = new ControlTemplate(typeof(PasswordBox)) { VisualTree = b };
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
            ct.Triggers.Add(disabled);
            return ct;
        }

        private static Button MakeButton(string label, bool accent) => UiKit.Make(label, accent);
    }
}
