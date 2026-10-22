using System.Text.Json;
using FireBlazor.Testing;

namespace FireBlazor.Tests.Testing;

public class FakeFirestoreFieldValueTests
{
    private readonly FakeFirestore _firestore = new();

    public class Counter
    {
        public int Count { get; set; }
        public DateTime? LastUpdated { get; set; }
        public List<string> Tags { get; set; } = [];
    }

    [Fact]
    public async Task UpdateAsync_WithIncrement_IncreasesValue()
    {
        // Arrange
        var doc = _firestore.Collection<Counter>("counters").Document("c1");
        await doc.SetAsync(new Counter { Count = 10 });

        // Act
        var result = await doc.UpdateAsync(new { Count = FieldValue.Increment(5) });

        // Assert
        Assert.True(result.IsSuccess);
        var snapshot = await doc.GetAsync();
        Assert.Equal(15, snapshot.Value.Data!.Count);
    }

    [Fact]
    public async Task UpdateAsync_WithServerTimestamp_SetsCurrentTime()
    {
        // Arrange
        var doc = _firestore.Collection<Counter>("counters").Document("c1");
        await doc.SetAsync(new Counter { Count = 0 });
        var before = DateTime.UtcNow;

        // Act
        var result = await doc.UpdateAsync(new { LastUpdated = FieldValue.ServerTimestamp() });

        // Assert
        Assert.True(result.IsSuccess);
        var snapshot = await doc.GetAsync();
        Assert.NotNull(snapshot.Value.Data!.LastUpdated);
        Assert.True(snapshot.Value.Data.LastUpdated >= before);
    }

    [Fact]
    public async Task UpdateAsync_WithArrayUnion_AddsElements()
    {
        // Arrange
        var doc = _firestore.Collection<Counter>("counters").Document("c1");
        await doc.SetAsync(new Counter { Tags = new List<string> { "a", "b" } });

        // Act
        var result = await doc.UpdateAsync(new { Tags = FieldValue.ArrayUnion("c", "d") });

        // Assert
        Assert.True(result.IsSuccess);
        var snapshot = await doc.GetAsync();
        Assert.Equal(new[] { "a", "b", "c", "d" }, snapshot.Value.Data!.Tags);
    }

    [Fact]
    public async Task UpdateAsync_WithArrayUnion_DoesNotAddDuplicates()
    {
        // Arrange
        var doc = _firestore.Collection<Counter>("counters").Document("c1");
        await doc.SetAsync(new Counter { Tags = new List<string> { "a", "b" } });

        // Act
        var result = await doc.UpdateAsync(new { Tags = FieldValue.ArrayUnion("b", "c") });

        // Assert
        Assert.True(result.IsSuccess);
        var snapshot = await doc.GetAsync();
        Assert.Equal(new[] { "a", "b", "c" }, snapshot.Value.Data!.Tags);
    }

    [Fact]
    public async Task UpdateAsync_WithArrayRemove_RemovesElements()
    {
        // Arrange
        var doc = _firestore.Collection<Counter>("counters").Document("c1");
        await doc.SetAsync(new Counter { Tags = new List<string> { "a", "b", "c" } });

        // Act
        var result = await doc.UpdateAsync(new { Tags = FieldValue.ArrayRemove("b") });

        // Assert
        Assert.True(result.IsSuccess);
        var snapshot = await doc.GetAsync();
        Assert.Equal(new[] { "a", "c" }, snapshot.Value.Data!.Tags);
    }

    [Fact]
    public async Task UpdateAsync_WithDelete_RemovesField()
    {
        // Arrange
        var doc = _firestore.Collection<Counter>("counters").Document("c1");
        await doc.SetAsync(new Counter { Count = 10, LastUpdated = DateTime.UtcNow });

        // Act
        var result = await doc.UpdateAsync(new { LastUpdated = FieldValue.Delete() });

        // Assert
        Assert.True(result.IsSuccess);
        var snapshot = await doc.GetAsync();
        Assert.Null(snapshot.Value.Data!.LastUpdated);
    }

    // Sentinels written through object-typed containers (Dictionary<string, object>, nested maps) must be
    // applied as operations, not stored as maps such as {} or {"elements":[...]}.

    private async Task<Dictionary<string, JsonElement>> RawAsync(string path)
    {
        var snapshot = await _firestore.Collection<Dictionary<string, JsonElement>>("counters").Document(path).GetAsync();
        return snapshot.Value.Data!;
    }

    [Fact]
    public async Task UpdateAsync_ObjectDictionary_ServerTimestamp_StoresTimestamp()
    {
        var doc = _firestore.Collection<Counter>("counters").Document("c1");
        await doc.SetAsync(new Counter());
        var before = DateTime.UtcNow;

        await doc.UpdateAsync(new Dictionary<string, object> { ["lastUpdated"] = FieldValue.ServerTimestamp() });

        var raw = await RawAsync("c1");
        Assert.Equal(JsonValueKind.String, raw["lastUpdated"].ValueKind);
        Assert.True((await doc.GetAsync()).Value.Data!.LastUpdated >= before);
    }

    [Fact]
    public async Task UpdateAsync_ObjectDictionary_Increment_AddsToValue()
    {
        var doc = _firestore.Collection<Counter>("counters").Document("c1");
        await doc.SetAsync(new Counter { Count = 10 });

        await doc.UpdateAsync(new Dictionary<string, object> { ["count"] = FieldValue.Increment(5) });

        Assert.Equal(15, (await doc.GetAsync()).Value.Data!.Count);
    }

    [Fact]
    public async Task UpdateAsync_ObjectDictionary_ArrayUnion_AppendsElements()
    {
        var doc = _firestore.Collection<Counter>("counters").Document("c1");
        await doc.SetAsync(new Counter { Tags = ["a"] });

        await doc.UpdateAsync(new Dictionary<string, object> { ["tags"] = FieldValue.ArrayUnion("a", "b") });

        Assert.Equal(JsonValueKind.Array, (await RawAsync("c1"))["tags"].ValueKind);
        Assert.Equal(["a", "b"], (await doc.GetAsync()).Value.Data!.Tags);
    }

    [Fact]
    public async Task UpdateAsync_ObjectDictionary_ArrayRemove_RemovesElements()
    {
        var doc = _firestore.Collection<Counter>("counters").Document("c1");
        await doc.SetAsync(new Counter { Tags = ["a", "b"] });

        await doc.UpdateAsync(new Dictionary<string, object> { ["tags"] = FieldValue.ArrayRemove("a") });

        Assert.Equal(["b"], (await doc.GetAsync()).Value.Data!.Tags);
    }

    [Fact]
    public async Task UpdateAsync_ObjectDictionary_Delete_RemovesField()
    {
        var doc = _firestore.Collection<Counter>("counters").Document("c1");
        await doc.SetAsync(new Counter { Count = 1, LastUpdated = DateTime.UtcNow });

        await doc.UpdateAsync(new Dictionary<string, object> { ["lastUpdated"] = FieldValue.Delete() });

        Assert.False((await RawAsync("c1")).ContainsKey("lastUpdated"));
    }

    [Fact]
    public async Task UpdateAsync_NestedObjectDictionary_ResolvesSentinelsInsideMap()
    {
        var doc = _firestore.Collection<Counter>("counters").Document("c1");
        await doc.SetAsync(new Counter());

        await doc.UpdateAsync(new Dictionary<string, object>
        {
            ["meta"] = new Dictionary<string, object>
            {
                ["at"] = FieldValue.ServerTimestamp(),
                ["ids"] = FieldValue.ArrayUnion("x"),
                ["label"] = "kept",
            },
        });

        var meta = (await RawAsync("c1"))["meta"];
        Assert.Equal(JsonValueKind.String, meta.GetProperty("at").ValueKind);
        Assert.Equal("x", meta.GetProperty("ids")[0].GetString());
        Assert.Equal("kept", meta.GetProperty("label").GetString());
    }

    [Fact]
    public async Task SetAsync_ObjectDictionary_ResolvesSentinels()
    {
        var doc = _firestore.Collection<Dictionary<string, object>>("counters").Document("c1");

        await doc.SetAsync(new Dictionary<string, object>
        {
            ["lastUpdated"] = FieldValue.ServerTimestamp(),
            ["count"] = FieldValue.Increment(2),
            ["tags"] = FieldValue.ArrayUnion("a"),
        });

        var raw = await RawAsync("c1");
        Assert.Equal(JsonValueKind.String, raw["lastUpdated"].ValueKind);
        Assert.Equal(2, raw["count"].GetDouble());
        Assert.Equal("a", raw["tags"][0].GetString());
    }
}
