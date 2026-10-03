using System.Threading;
using Vortice.Direct3D11;

namespace BendWin.Core;

// Thread-safe single-slot frame buffer. The capture thread writes; the render thread reads.
internal sealed class FrameStore : IDisposable
{
    private readonly Lock _lock = new();
    private ID3D11Texture2D? _texture;
    private long _timestamp;

    public void Set(ID3D11Texture2D texture, long timestamp)
    {
        lock (_lock)
        {
            _texture?.Dispose();
            _texture = texture;
            _timestamp = timestamp;
        }
    }

    // Returns the latest frame newer than afterTimestamp, or null if nothing new.
    public (ID3D11Texture2D? Texture, long Timestamp) Get(long afterTimestamp = 0)
    {
        lock (_lock)
        {
            if (_texture == null || _timestamp <= afterTimestamp) return (null, _timestamp);
            return (_texture, _timestamp);
        }
    }

    public void Dispose()
    {
        lock (_lock) { _texture?.Dispose(); _texture = null; }
    }
}
