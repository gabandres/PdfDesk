using InkvoltPdf.Core;

namespace InkvoltPdf.Tests;

public class BookmarkTreeTests
{
    private static IReadOnlyList<Bookmark> Tree() =>
    [
        new Bookmark("A", 0) { Children = [new Bookmark("A1", 0), new Bookmark("A2", 1)] },
        new Bookmark("B", 1),
        new Bookmark("C", 2) { Children = [new Bookmark("C1", 2)] },
    ];

    private static string Show(IReadOnlyList<Bookmark> tree) =>
        string.Join(" ", tree.Select(b => b.Children.Count == 0 ? b.Title : $"{b.Title}({Show(b.Children)})"));

    [Fact]
    public void RenamesRemovesAndInserts()
    {
        var t = Tree();
        Assert.Equal("A(A1 A2) B C(C1)", Show(t));
        Assert.Equal("A(A1 X) B C(C1)", Show(BookmarkTree.Replace(t, [0, 1], b => b with { Title = "X" })));
        Assert.Equal("A(A1 A2) C(C1)", Show(BookmarkTree.Remove(t, [1])));
        Assert.Equal("A(A2) B C(C1)", Show(BookmarkTree.Remove(t, [0, 0])));
        Assert.Equal("A(A1 A2) B(N) C(C1)", Show(BookmarkTree.Insert(t, [1], 0, new Bookmark("N"))));
        Assert.Equal("A(A1 A2) B C(C1) N", Show(BookmarkTree.Insert(t, [], 99, new Bookmark("N"))));
        // Untouched branches stay the same records.
        Assert.Same(t[2], BookmarkTree.Replace(t, [0, 1], b => b with { Title = "X" })[2]);
    }

    [Fact]
    public void MovesAndNests()
    {
        var t = Tree();
        Assert.Equal("B A(A1 A2) C(C1)", Show(BookmarkTree.Move(t, [1], [], 0)));
        Assert.Equal("B C(C1) A(A1 A2)", Show(BookmarkTree.Move(t, [0], [], 3)));
        Assert.Equal("A(A1 A2) C(C1 B)", Show(BookmarkTree.Move(t, [1], [2], 1)));
        Assert.Equal("A(A1) B C(A2 C1)", Show(BookmarkTree.Move(t, [0, 1], [2], 0)));
        // Into a later sibling: the target's path shifts when the moved one leaves.
        Assert.Equal("B(A(A1 A2)) C(C1)", Show(BookmarkTree.Move(t, [0], [1], 0)));
        Assert.True(BookmarkTree.Move(t, [0], [1], 0)[0].Open);
        // Out to the top level.
        Assert.Equal("A(A1) A2 B C(C1)", Show(BookmarkTree.Move(t, [0, 1], [], 1)));
        Assert.Throws<InvalidOperationException>(() => BookmarkTree.Move(t, [0], [0, 1], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BookmarkTree.Remove(t, [5]));
    }

    [Fact]
    public void EditsRoundTripThroughTheFile()
    {
        var pdf = Bookmarks.SetOutline(TestPdf.Make(3), null, Tree());
        var read = Bookmarks.Read(pdf, null);
        var edited = Bookmarks.SetOutline(pdf, null, BookmarkTree.Move(BookmarkTree.Replace(read, [1], b => b with { Title = "Bee" }), [2, 0], [], 0));
        Assert.Equal("C1 A(A1 A2) Bee C", Show(Bookmarks.Read(edited, null)));
        Assert.Equal(2, Bookmarks.Read(edited, null)[0].PageIndex);
    }
}
