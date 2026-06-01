using FireBlazor.Testing;

namespace FireBlazor.Tests.Testing;

public class FakeCollectionGroupTests
{
    public class Comment
    {
        public string? PropertyId { get; set; }
        public string? Text { get; set; }
        public int CreatedAt { get; set; }
    }

    [Fact]
    public async Task GetAsync_ReturnsDocumentsAcrossParentPaths()
    {
        var firestore = new FakeFirestore();
        await firestore.Collection<Comment>("properties/p1/comments").AddAsync(new Comment
        {
            PropertyId = "p1",
            Text = "first",
            CreatedAt = 1
        });
        await firestore.Collection<Comment>("properties/p2/comments").AddAsync(new Comment
        {
            PropertyId = "p2",
            Text = "second",
            CreatedAt = 2
        });
        await firestore.Collection<Comment>("properties/p1/posts").AddAsync(new Comment
        {
            PropertyId = "p1",
            Text = "not a comment group hit",
            CreatedAt = 3
        });

        var result = await firestore.CollectionGroup<Comment>("comments").GetAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, s => s.Data?.PropertyId == "p1");
        Assert.Contains(result.Value, s => s.Data?.PropertyId == "p2");
    }

    [Fact]
    public async Task Where_FiltersCollectionGroupResults()
    {
        var firestore = new FakeFirestore();
        await firestore.Collection<Comment>("properties/p1/comments").AddAsync(new Comment
        {
            PropertyId = "p1",
            Text = "keep",
            CreatedAt = 1
        });
        await firestore.Collection<Comment>("properties/p2/comments").AddAsync(new Comment
        {
            PropertyId = "p2",
            Text = "drop",
            CreatedAt = 2
        });

        var result = await firestore.CollectionGroup<Comment>("comments")
            .Where(x => x.PropertyId == "p1")
            .GetAsync();

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value);
        Assert.Equal("keep", result.Value[0].Data?.Text);
    }

    [Fact]
    public async Task OrderByAndTake_AppliesLimitAfterSort()
    {
        var firestore = new FakeFirestore();
        await firestore.Collection<Comment>("properties/p1/comments").AddAsync(new Comment
        {
            PropertyId = "p1",
            Text = "old",
            CreatedAt = 1
        });
        await firestore.Collection<Comment>("properties/p2/comments").AddAsync(new Comment
        {
            PropertyId = "p2",
            Text = "new",
            CreatedAt = 3
        });
        await firestore.Collection<Comment>("properties/p3/comments").AddAsync(new Comment
        {
            PropertyId = "p3",
            Text = "middle",
            CreatedAt = 2
        });

        var result = await firestore.CollectionGroup<Comment>("comments")
            .OrderByDescending(x => x.CreatedAt)
            .Take(2)
            .GetAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal("new", result.Value[0].Data?.Text);
        Assert.Equal("middle", result.Value[1].Data?.Text);
    }

    [Fact]
    public async Task StartAfter_RequiresOrderBy()
    {
        var firestore = new FakeFirestore();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            firestore.CollectionGroup<Comment>("comments").StartAfter(1).GetAsync());
    }

    [Fact]
    public void OnSnapshot_EmitsUpdatesForMatchingCollectionGroup()
    {
        var firestore = new FakeFirestore();
        var snapshots = new List<IReadOnlyList<DocumentSnapshot<Comment>>>();

        var unsubscribe = firestore.CollectionGroup<Comment>("comments")
            .OnSnapshot(docs => snapshots.Add(docs));

        firestore.Collection<Comment>("properties/p1/comments").AddAsync(new Comment
        {
            PropertyId = "p1",
            Text = "live",
            CreatedAt = 1
        }).GetAwaiter().GetResult();

        Assert.Equal(2, snapshots.Count);
        Assert.Single(snapshots[^1]);
        Assert.Equal("live", snapshots[^1][0].Data?.Text);

        unsubscribe().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task CollectionGroup_IgnoresOtherCollectionNames()
    {
        var firestore = new FakeFirestore();
        await firestore.Collection<Comment>("properties/p1/posts").AddAsync(new Comment
        {
            PropertyId = "p1",
            Text = "post",
            CreatedAt = 1
        });

        var result = await firestore.CollectionGroup<Comment>("comments").GetAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }
}
