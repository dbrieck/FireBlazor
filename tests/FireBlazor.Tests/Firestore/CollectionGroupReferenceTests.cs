using FireBlazor.Platform.Wasm;
using Microsoft.JSInterop;
using NSubstitute;

namespace FireBlazor.Tests.Firestore;

public class CollectionGroupReferenceTests
{
    public class Comment
    {
        public string? PropertyId { get; set; }
        public string? Text { get; set; }
        public int CreatedAt { get; set; }
    }

    [Fact]
    public void CollectionGroup_ReturnsCollectionGroupReference()
    {
        var jsRuntime = Substitute.For<IJSRuntime>();
        var jsInterop = new FirebaseJsInterop(jsRuntime);
        var firestore = new WasmFirestore(jsInterop);

        var query = firestore.CollectionGroup<Comment>("comments");

        Assert.NotNull(query);
        Assert.IsAssignableFrom<ICollectionGroupReference<Comment>>(query);
    }

    [Fact]
    public void CollectionGroup_ThrowsOnPathLikeId()
    {
        var jsRuntime = Substitute.For<IJSRuntime>();
        var jsInterop = new FirebaseJsInterop(jsRuntime);
        var firestore = new WasmFirestore(jsInterop);

        Assert.Throws<ArgumentException>(() => firestore.CollectionGroup<Comment>("properties/p1/comments"));
    }

    [Fact]
    public void ChainedQuery_ReturnsCollectionGroupReference()
    {
        var jsRuntime = Substitute.For<IJSRuntime>();
        var jsInterop = new FirebaseJsInterop(jsRuntime);
        var firestore = new WasmFirestore(jsInterop);

        var result = firestore.CollectionGroup<Comment>("comments")
            .Where(x => x.PropertyId == "p1")
            .OrderByDescending(x => x.CreatedAt)
            .StartAfter(100)
            .Take(25);

        Assert.IsAssignableFrom<ICollectionGroupReference<Comment>>(result);
    }

    [Fact]
    public void ICollectionGroupReference_HasExpectedMethods()
    {
        var type = typeof(ICollectionGroupReference<Comment>);

        Assert.NotNull(type.GetMethod("Where"));
        Assert.NotNull(type.GetMethod("OrderBy"));
        Assert.NotNull(type.GetMethod("OrderByDescending"));
        Assert.NotNull(type.GetMethod("Take"));
        Assert.NotNull(type.GetMethod("StartAt"));
        Assert.NotNull(type.GetMethod("StartAfter"));
        Assert.NotNull(type.GetMethod("GetAsync"));
        Assert.NotNull(type.GetMethod("OnSnapshot"));
    }
}
