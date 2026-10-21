using System.Text.Json;

namespace KillerPDF.Services
{
    internal sealed class SignatureStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly string _dir;
        private readonly string _file;

        private static string DefaultDir => AppDataPaths.UserRoot;

        public SignatureStore()
            : this(DefaultDir, AppDataPaths.SignaturesFile) { }

        internal SignatureStore(string dir, string file)
        {
            _dir  = dir;
            _file = file;
        }

        private List<SavedSignature> _items = [];

        public IReadOnlyList<SavedSignature> Signatures => _items;

        /// <summary>
        /// The path a damaged library was moved aside to by the last <see cref="Load"/>, or null.
        /// The library then starts empty, and the damaged file is never overwritten.
        /// </summary>
        public string? PreservedDamagedFile { get; private set; }

        public void Load()
        {
            PreservedDamagedFile = null;
            _items = [];
            if (!System.IO.File.Exists(_file)) return;
            try
            {
                var json = System.IO.File.ReadAllText(_file);
                _items = JsonSerializer.Deserialize<List<SavedSignature>>(json) ?? [];
            }
            catch (Exception ex) when (ex is JsonException or System.IO.IOException or UnauthorizedAccessException
                or NotSupportedException)
            {
                // Unreadable is not the same as absent: keep the bytes before anything replaces them.
                try
                {
                    string aside = _file + ".damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    System.IO.File.Copy(_file, aside, overwrite: false);
                    PreservedDamagedFile = aside;
                }
                catch (Exception copyError) when (copyError is System.IO.IOException or UnauthorizedAccessException)
                {
                    PreservedDamagedFile = _file;   // could not copy, so the original is left in place
                }
            }
        }

        /// <summary>
        /// Writes the library through a sibling temp file and one replace, keeping the previous
        /// version as a .bak. False when it could not be saved; the in-memory list is unchanged.
        /// </summary>
        public bool Persist()
        {
            try
            {
                if (PreservedDamagedFile == _file) return false;   // never overwrite a damaged library we could not copy
                System.IO.Directory.CreateDirectory(_dir);
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(_items, JsonOptions);
                if (System.IO.File.Exists(_file) && PreservedDamagedFile is null)
                    System.IO.File.Copy(_file, _file + ".bak", overwrite: true);
                AtomicFileOutput.Write(_file, json);
                return true;
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                or NotSupportedException)
            {
                return false;
            }
        }

        public void Add(SavedSignature sig) => _items.Add(sig);

        public void Remove(SavedSignature sig) => _items.Remove(sig);
    }
}
