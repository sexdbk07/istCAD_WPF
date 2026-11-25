using System.Windows.Media;

namespace iStCAD_1;

/// <summary>Remembers the last few finished frames per camera so returning to a view (ZOOM PREVIOUS, named views, pan back, zoom out then in) is instant.</summary>
public partial class MainWindow
{
    private readonly List<(DrawingSession Session, RenderImageKey Key, DrawingImage Image)> _frameCache = new();
    private const int FrameCacheCapacity = 4;

    private void StoreFrame(DrawingSession session, RenderImageKey key, DrawingImage image)
    {
        // Frames of an older document revision can never match again: drop them so their bitmaps are freed.
        _frameCache.RemoveAll(f => ReferenceEquals(f.Session, session) && (f.Key.Revision != key.Revision || f.Key == key));
        _frameCache.Add((session, key, image));
        while (_frameCache.Count > FrameCacheCapacity) _frameCache.RemoveAt(0);
    }

    private bool TryGetCachedFrame(DrawingSession session, RenderImageKey key, out DrawingImage image)
    {
        for (var i = _frameCache.Count - 1; i >= 0; i--)
        {
            var entry = _frameCache[i];
            if (!ReferenceEquals(entry.Session, session) || entry.Key != key) continue;
            _frameCache.RemoveAt(i); _frameCache.Add(entry); // most recently used
            image = entry.Image;
            return true;
        }
        image = null!;
        return false;
    }

    private void ClearFrameCache() => _frameCache.Clear();
}
