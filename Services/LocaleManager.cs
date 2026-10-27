using System;
using System.Globalization;
using System.IO;
using System.Windows;

namespace KillerPDF.Services
{
    internal enum Locale { EnUS, Bn, CsCZ, De, Es, Fr, HuHU, ItIT, JaJP, KkKZ, PlPL, RuRU, NbNO, PtBR, TrTR, UkUA, ViVN, ZhCN, ZhTW, SlSI }

    internal static class LocaleManager
    {
        private static Locale _current = Locale.EnUS;

        public static Locale Current => _current;

        /// <summary>Localized string for a Str_ key. Falls back to the key itself when it is
        /// missing, so a translation gap shows up as itself instead of as blank UI.</summary>
        public static string Loc(string key) =>
            Application.Current.TryFindResource(key) as string ?? key;

        // ── Translator test mode (#211, thanks bovirus) ─────────────────────────
        // --lang-file <path> loads an external translation xaml as the language override, winning
        // over the saved locale, and re-applies it on every save of the file - so a translation
        // can be checked for string length and context in the live app before it is built in.
        // Untranslated keys fall back to the en-US base like any partial locale.

        /// <summary>Full path of the external translation file, or null. Set from the
        /// --lang-file switch before <see cref="Initialize"/> runs.</summary>
        internal static string? ExternalFile;

        /// <summary>Raised on the UI thread after a successful live re-apply, so the window can
        /// rebuild its code-built captions (toolbar, context menu) the way a language switch does.</summary>
        internal static event Action? ExternalReloaded;

        private static FileSystemWatcher? _watcher;
        private static System.Windows.Threading.DispatcherTimer? _reloadDebounce;
        private static bool _externalLoadedOnce;

        /// <summary>
        /// Call once at startup (after ThemeManager.Initialize) to restore the saved locale.
        /// </summary>
        public static void Initialize()
        {
            var saved = App.GetSetting("Locale");
            if (Enum.TryParse<Locale>(saved, out var locale))
            {
                _current = locale;
            }
            else
            {
                _current = LocaleForCulture(CultureInfo.CurrentUICulture);
                App.SetSetting("Locale", _current.ToString());
            }
            ApplyInternal(_current);
        }

        internal static Locale LocaleForCulture(CultureInfo culture)
        {
            string name = culture.Name;
            string language = culture.TwoLetterISOLanguageName;

            if (language == "zh")
                return name.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase)
                    ? Locale.ZhTW
                    : Locale.ZhCN;

            return language switch
            {
                "bn" => Locale.Bn,
                "cs" => Locale.CsCZ,
                "de" => Locale.De,
                "es" => Locale.Es,
                "fr" => Locale.Fr,
                "hu" => Locale.HuHU,
                "it" => Locale.ItIT,
                "ja" => Locale.JaJP,
                "kk" => Locale.KkKZ,
                "pl" => Locale.PlPL,
                "ru" => Locale.RuRU,
                "sl" => Locale.SlSI,
                "tr" => Locale.TrTR,
                "vi" => Locale.ViVN,
                "uk" => Locale.UkUA,
                "nb" or "no" or "nn" => Locale.NbNO,
                "pt" => Locale.PtBR,
                _ => Locale.EnUS,
            };
        }

        /// <summary>
        /// Switch locale, persist choice, and hot-swap the string ResourceDictionary.
        /// </summary>
        public static void Apply(Locale locale)
        {
            _current = locale;
            App.SetSetting("Locale", locale.ToString());
            ApplyInternal(locale);
        }

        // ── Internal ─────────────────────────────────────────────────────

        private static void ApplyInternal(Locale locale)
        {
            var merged = Application.Current.Resources.MergedDictionaries;

            // [0] theme, [1] the family Controls.xaml, then the en-US BASE - always present so any
            // partial locale falls back to English for keys it doesn't translate - and directly
            // after it the chosen locale's overrides (absent for English). The base is found by its
            // source and the override by reference, the way KillerUI's LocaleManager does it, so a
            // dictionary merged elsewhere in the list is never overwritten or removed.
            int baseIndex = StringsBaseIndex(merged);
            if (merged.Count > baseIndex)
                merged[baseIndex] = new ResourceDictionary { Source = new Uri("pack://application:,,,/Strings/en-US.xaml") };

            // Translator test mode wins over the chosen locale for the override slot.
            if (ExternalFile is not null)
            {
                if (TryApplyExternal()) { EnsureWatcher(); return; }
                if (!_externalLoadedOnce)
                    MessageBox.Show($"Could not load the translation file:\n{ExternalFile}\n\nCheck the path and the file's XML, then start KillerPDF again.",
                                    "KillerPDF --lang-file", MessageBoxButton.OK, MessageBoxImage.Warning);
                // Fall through to the normal locale so the app still comes up usable.
            }

            Uri? overrideUri = locale switch
            {
                Locale.Bn   => new Uri("pack://application:,,,/Strings/bn.xaml"),
                Locale.CsCZ => new Uri("pack://application:,,,/Strings/cs-CZ.xaml"),
                Locale.De   => new Uri("pack://application:,,,/Strings/de-DE.xaml"),
                Locale.Es   => new Uri("pack://application:,,,/Strings/es.xaml"),
                Locale.Fr   => new Uri("pack://application:,,,/Strings/fr-FR.xaml"),
                Locale.HuHU => new Uri("pack://application:,,,/Strings/hu-HU.xaml"),
                Locale.ItIT => new Uri("pack://application:,,,/Strings/it-IT.xaml"),
                Locale.JaJP => new Uri("pack://application:,,,/Strings/ja-JP.xaml"),
                Locale.KkKZ => new Uri("pack://application:,,,/Strings/kk-KZ.xaml"),
                Locale.PlPL => new Uri("pack://application:,,,/Strings/pl-PL.xaml"),
                Locale.RuRU => new Uri("pack://application:,,,/Strings/ru-RU.xaml"),
                Locale.TrTR => new Uri("pack://application:,,,/Strings/tr-TR.xaml"),
                Locale.ViVN => new Uri("pack://application:,,,/Strings/vi-VN.xaml"),
                Locale.UkUA => new Uri("pack://application:,,,/Strings/uk-UA.xaml"),
                Locale.NbNO => new Uri("pack://application:,,,/Strings/nb-NO.xaml"),
                Locale.PtBR => new Uri("pack://application:,,,/Strings/pt-BR.xaml"),
                Locale.ZhCN => new Uri("pack://application:,,,/Strings/zh-CN.xaml"),
                Locale.ZhTW => new Uri("pack://application:,,,/Strings/zh-TW.xaml"),
                Locale.SlSI => new Uri("pack://application:,,,/Strings/sl-SI.xaml"),
                _           => null,   // English: base only
            };

            SetOverride(overrideUri is null ? null : new ResourceDictionary { Source = overrideUri });
        }

        // The override dictionary this class merged, found again by reference.
        private static ResourceDictionary? _override;

        /// <summary>Puts the chosen locale's dictionary directly after the English base, replacing
        /// the one this class merged before, or removes it (null) for English. Never touches a
        /// dictionary it did not put there.</summary>
        private static void SetOverride(ResourceDictionary? dictionary)
        {
            var merged = Application.Current.Resources.MergedDictionaries;
            int existing = _override == null ? -1 : merged.IndexOf(_override);
            if (dictionary == null)
            {
                if (existing >= 0) merged.RemoveAt(existing);
                _override = null;
                return;
            }
            if (existing >= 0) merged[existing] = dictionary;
            else merged.Insert(Math.Min(merged.Count, StringsBaseIndex(merged) + 1), dictionary);
            _override = dictionary;
        }

        /// <summary>Where the English string base is merged, found by its source. Slot 2, the
        /// App.xaml layout, if it cannot be found.</summary>
        private static int StringsBaseIndex(System.Collections.ObjectModel.Collection<ResourceDictionary> merged)
        {
            for (int i = 0; i < merged.Count; i++)
                if (merged[i].Source?.OriginalString.EndsWith("Strings/en-US.xaml", StringComparison.OrdinalIgnoreCase) == true)
                    return i;
            return 2;
        }

        /// <summary>Loads <see cref="ExternalFile"/> into the override slot. False on any parse or
        /// IO failure - during live reload the last good version simply stays applied, since a
        /// text editor mid-save routinely produces momentarily unreadable or invalid XML.</summary>
        private static bool TryApplyExternal()
        {
            try
            {
                var merged = Application.Current.Resources.MergedDictionaries;
                int baseIndex = StringsBaseIndex(merged);
                if (merged.Count <= baseIndex) return false;
                // Parsed as data, never as XAML: XamlReader would instantiate whatever the file names.
                var known = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach (object key in merged[baseIndex].Keys) if (key is string name) known.Add(name);
                using var fs = new FileStream(ExternalFile!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var rd = new ResourceDictionary();
                foreach (var entry in TranslationFileReader.Read(fs, known)) rd[entry.Key] = entry.Value;
                SetOverride(rd);
                _externalLoadedOnce = true;
                return true;
            }
            catch { return false; }
        }

        private static void EnsureWatcher()
        {
            if (_watcher is not null) return;
            string dir  = Path.GetDirectoryName(ExternalFile!) is { Length: > 0 } d ? d : ".";
            string name = Path.GetFileName(ExternalFile!);
            _watcher = new FileSystemWatcher(dir, name) { EnableRaisingEvents = true };
            // Editors save as write, replace, or delete-and-rename depending on the editor, so
            // watch every shape. Events arrive on a worker thread and usually several per save -
            // marshal to the UI thread and debounce into one re-apply.
            _watcher.Changed += (_, _) => QueueExternalReload();
            _watcher.Created += (_, _) => QueueExternalReload();
            _watcher.Renamed += (_, _) => QueueExternalReload();
        }

        private static void QueueExternalReload()
        {
            Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
            {
                _reloadDebounce ??= NewDebounce();
                _reloadDebounce.Stop();
                _reloadDebounce.Start();
            }));
        }

        private static System.Windows.Threading.DispatcherTimer NewDebounce()
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                if (TryApplyExternal()) ExternalReloaded?.Invoke();
            };
            return t;
        }
    }
}
