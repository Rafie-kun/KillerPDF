using System.Windows;
using KillerPDF.Services;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    private StampSpec? _docStampSpec;
    private readonly Dictionary<int, List<StampInstance>> _stamps = [];
    private readonly Dictionary<int, List<Rect>> _stampHitRects = [];
    private StampSpec? _builtStampSpec;
    private int _builtStampPageCount = -1;
    private DocumentSession? _stampSession;

    internal StampSpec? StampSpecRef { get => _docStampSpec; set => _docStampSpec = value; }
    internal Dictionary<int, List<StampInstance>> StampInstancesRef => _stamps;
    internal Dictionary<int, List<Rect>> StampHitRectsRef => _stampHitRects;

    private void ApplyStampSession(DocumentSession session)
    {
        _docStampSpec = session.StampSpec;
        if (ReferenceEquals(_stampSession, session)) return;
        _stampSession = session;
        _builtStampPageCount = -1;
        _stamps.Clear();
        _stampHitRects.Clear();
    }

    internal void EnsureStampInstancesExt()
    {
        if (!ReferenceEquals(_builtStampSpec, _docStampSpec) || _builtStampPageCount != (_doc?.PageCount ?? 0))
            RebuildStampInstancesExt();
    }

    internal void RebuildStampInstancesExt()
    {
        _stamps.Clear();
        _stampHitRects.Clear();
        _builtStampSpec = _docStampSpec;
        _builtStampPageCount = _doc?.PageCount ?? 0;
        if (_docStampSpec is not { } spec || _doc is null) return;
        void Add(int page, StampKind kind)
        {
            if (!_stamps.TryGetValue(page, out var list)) _stamps[page] = list = [];
            list.Add(new StampInstance { PageIndex = page, Kind = kind, Spec = spec });
        }
        if (spec.NumbersEnabled)
            foreach (int page in PdfBurn.StampPageRange(spec.NumRange, _builtStampPageCount)) Add(page, StampKind.PageNumber);
        if (spec.WmEnabled)
            foreach (int page in PdfBurn.StampPageRange(spec.WmRange, _builtStampPageCount)) Add(page, StampKind.Watermark);
    }

    internal bool StampHitTestExt(int page, Point point)
    {
        EnsureStampInstancesExt();
        return _stampHitRects.TryGetValue(page, out var rectangles) && rectangles.Any(rectangle => rectangle.Contains(point));
    }
}
