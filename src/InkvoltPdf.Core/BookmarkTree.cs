namespace InkvoltPdf.Core;

/// <summary>
/// Edits of a bookmark tree (as read by <see cref="Bookmarks.Read"/>) addressed by index paths: [2] is the third
/// top-level bookmark, [2, 0] its first child. Every edit returns a new tree; untouched bookmarks are the same records,
/// so <see cref="Bookmarks.SetOutline"/> keeps them exactly as they were.
/// </summary>
public static class BookmarkTree
{
    /// <exception cref="ArgumentOutOfRangeException">No bookmark at <paramref name="path"/>.</exception>
    public static Bookmark At(IReadOnlyList<Bookmark> tree, IReadOnlyList<int> path)
    {
        if (path.Count == 0) throw new ArgumentOutOfRangeException(nameof(path), "Empty bookmark path.");
        var list = tree;
        Bookmark? b = null;
        foreach (var i in path)
        {
            if (i < 0 || i >= list.Count) throw new ArgumentOutOfRangeException(nameof(path), "The bookmark isn't there any more.");
            b = list[i];
            list = b.Children;
        }
        return b!;
    }

    /// <summary>Replaces the bookmark at <paramref name="path"/> with what <paramref name="change"/> makes of it.</summary>
    public static IReadOnlyList<Bookmark> Replace(IReadOnlyList<Bookmark> tree, IReadOnlyList<int> path, Func<Bookmark, Bookmark> change)
    {
        At(tree, path);
        return Rebuild(tree, path, 0, change);
    }

    /// <summary>Removes the bookmark at <paramref name="path"/> with its children.</summary>
    public static IReadOnlyList<Bookmark> Remove(IReadOnlyList<Bookmark> tree, IReadOnlyList<int> path)
    {
        At(tree, path);
        return Splice(tree, [.. path.Take(path.Count - 1)], list => list.RemoveAt(path[^1]));
    }

    /// <summary>Inserts <paramref name="bookmark"/> as child <paramref name="index"/> of <paramref name="parent"/> (empty = top level; index clamped).</summary>
    public static IReadOnlyList<Bookmark> Insert(IReadOnlyList<Bookmark> tree, IReadOnlyList<int> parent, int index, Bookmark bookmark)
    {
        if (parent.Count > 0) At(tree, parent);
        return Splice(tree, parent, list => list.Insert(Math.Clamp(index, 0, list.Count), bookmark));
    }

    /// <summary>
    /// Moves the bookmark at <paramref name="from"/> (with its children) to child <paramref name="index"/> of
    /// <paramref name="parent"/>, where index counts the parent's children before the move. The parent is opened so the
    /// moved bookmark shows.
    /// </summary>
    /// <exception cref="InvalidOperationException">The target is the bookmark itself or one of its descendants.</exception>
    public static IReadOnlyList<Bookmark> Move(IReadOnlyList<Bookmark> tree, IReadOnlyList<int> from, IReadOnlyList<int> parent, int index)
    {
        var moving = At(tree, from);
        if (parent.Count >= from.Count && parent.Take(from.Count).SequenceEqual(from))
            throw new InvalidOperationException("A bookmark can't be moved into itself.");
        if (parent.Count > 0) At(tree, parent);

        // Removing first shifts later siblings (and the parent's path) up by one where they share the old parent.
        var target = parent.ToArray();
        var oldParent = from.Take(from.Count - 1).ToArray();
        var depth = oldParent.Length;
        if (target.Length > depth && target.Take(depth).SequenceEqual(oldParent) && target[depth] > from[^1]) target[depth]--;
        if (target.SequenceEqual(oldParent) && index > from[^1]) index--;

        var without = Remove(tree, from);
        var result = Insert(without, target, index, moving);
        return target.Length == 0 ? result : Replace(result, target, b => b.Open ? b : b with { Open = true });
    }

    private static IReadOnlyList<Bookmark> Splice(IReadOnlyList<Bookmark> tree, IReadOnlyList<int> parent, Action<List<Bookmark>> change)
    {
        if (parent.Count == 0)
        {
            var list = tree.ToList();
            change(list);
            return list;
        }
        return Rebuild(tree, parent, 0, b =>
        {
            var kids = b.Children.ToList();
            change(kids);
            return b with { Children = kids };
        });
    }

    private static IReadOnlyList<Bookmark> Rebuild(IReadOnlyList<Bookmark> list, IReadOnlyList<int> path, int depth, Func<Bookmark, Bookmark> change)
    {
        var copy = list.ToList();
        var i = path[depth];
        copy[i] = depth == path.Count - 1 ? change(copy[i]) : copy[i] with { Children = Rebuild(copy[i].Children, path, depth + 1, change) };
        return copy;
    }
}
