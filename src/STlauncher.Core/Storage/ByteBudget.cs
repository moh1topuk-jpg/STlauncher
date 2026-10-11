using System;
using System.Collections.Generic;

namespace STlauncher.Core.Storage;

/// <summary>
/// The bookkeeping of a cache that is held to a number of bytes: which entries it has, how
/// large each is, and which have gone unused the longest. It holds no values itself; it
/// says which keys have to go, and the owner of the values lets go of them.
/// </summary>
/// <remarks>
/// A count says little about pictures: four hundred mod logos are fifteen megabytes, four
/// hundred gallery screenshots are three gigabytes. Not thread-safe; the owner locks.
/// </remarks>
public sealed class ByteBudget<TKey> where TKey : notnull
{
    private readonly long _maxBytes;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, long Bytes)>> _nodes;

    /// <summary>Least recently used first.</summary>
    private readonly LinkedList<(TKey Key, long Bytes)> _order = new();

    public ByteBudget(long maxBytes, IEqualityComparer<TKey>? comparer = null)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        _maxBytes = maxBytes;
        _nodes = new Dictionary<TKey, LinkedListNode<(TKey, long)>>(comparer);
    }

    public long TotalBytes { get; private set; }

    public int Count => _nodes.Count;

    public bool Contains(TKey key) => _nodes.ContainsKey(key);

    /// <summary>
    /// Records an entry as just used, at its size, and returns the keys that no longer fit,
    /// the longest unused first. The entry itself is never among them: one picture larger
    /// than the whole budget is still kept until the next one arrives.
    /// </summary>
    public IReadOnlyList<TKey> Add(TKey key, long bytes)
    {
        bytes = Math.Max(0, bytes);

        if (_nodes.Remove(key, out var known))
        {
            TotalBytes -= known.Value.Bytes;
            _order.Remove(known);
        }

        _nodes[key] = _order.AddLast((key, bytes));
        TotalBytes += bytes;

        if (TotalBytes <= _maxBytes)
        {
            return Array.Empty<TKey>();
        }

        var evicted = new List<TKey>();

        while (TotalBytes > _maxBytes && _order.First is { } oldest && !ReferenceEquals(oldest, _order.Last))
        {
            _order.RemoveFirst();
            _nodes.Remove(oldest.Value.Key);
            TotalBytes -= oldest.Value.Bytes;
            evicted.Add(oldest.Value.Key);
        }

        return evicted;
    }

    /// <summary>Marks an entry as just used, so it is the last to go.</summary>
    public void Touch(TKey key)
    {
        if (_nodes.TryGetValue(key, out var node) && !ReferenceEquals(node, _order.Last))
        {
            _order.Remove(node);
            _order.AddLast(node);
        }
    }

    public bool Remove(TKey key)
    {
        if (!_nodes.Remove(key, out var node))
        {
            return false;
        }

        _order.Remove(node);
        TotalBytes -= node.Value.Bytes;
        return true;
    }

    public void Clear()
    {
        _nodes.Clear();
        _order.Clear();
        TotalBytes = 0;
    }
}
