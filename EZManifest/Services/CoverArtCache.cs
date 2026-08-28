using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EZManifest.Services;

/// <summary>
/// Keeps a small LRU of downscaled covers so scrolling reuses bitmaps instead of
/// re-decoding every time a card is recycled (publish builds chug badly without this).
/// </summary>
public sealed class CoverArtCache
{
    private const int Capacity = 64;
    private const int DecodePixelWidth = 240;

    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _map =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly LinkedList<CacheEntry> _lru = new();

    public ImageSource? GetOrCreate(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return null;

        string key;
        try
        {
            key = Path.GetFullPath(imagePath);
        }
        catch
        {
            return null;
        }

        if (_map.TryGetValue(key, out var existing))
        {
            _lru.Remove(existing);
            _lru.AddFirst(existing);
            return existing.Value.Source;
        }

        if (!File.Exists(key))
            return null;

        var bitmap = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Logical,
            DecodePixelWidth = DecodePixelWidth,
            UriSource = new Uri(key, UriKind.Absolute)
        };

        var entry = new CacheEntry(key, bitmap);
        var node = _lru.AddFirst(entry);
        _map[key] = node;

        while (_map.Count > Capacity && _lru.Last is { } oldest)
        {
            _lru.RemoveLast();
            _map.Remove(oldest.Value.Path);
        }

        return bitmap;
    }

    private sealed class CacheEntry(string path, ImageSource source)
    {
        public string Path { get; } = path;
        public ImageSource Source { get; } = source;
    }
}
