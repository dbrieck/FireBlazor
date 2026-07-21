using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using NSubstitute;

namespace FireBlazor.Tests.Core;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddFirebase_RegistersIFirebase()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IJSRuntime>());

        services.AddFirebase(options => options
            .WithProject("test-project")
            .WithApiKey("test-key"));

        var provider = services.BuildServiceProvider();
        var firebase = provider.GetService<IFirebase>();

        Assert.NotNull(firebase);
    }

    [Fact]
    public void AddFirebase_ConfiguresOptions()
    {
        var services = new ServiceCollection();

        services.AddFirebase(options => options
            .WithProject("my-project")
            .WithApiKey("my-key")
            .UseAuth()
            .UseFirestore());

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<FirebaseOptions>();

        Assert.Equal("my-project", options.ProjectId);
        Assert.NotNull(options.AuthOptions);
        Assert.NotNull(options.FirestoreOptions);
    }

    [Fact]
    public void UseFirestore_DefaultsToNoPersistentLocalCache()
    {
        var services = new ServiceCollection();

        services.AddFirebase(options => options
            .WithProject("my-project")
            .UseFirestore());

        var options = services.BuildServiceProvider().GetRequiredService<FirebaseOptions>();

        Assert.NotNull(options.FirestoreOptions);
        Assert.False(options.FirestoreOptions!.PersistentLocalCacheEnabled);
        Assert.False(options.FirestoreOptions.OfflinePersistenceEnabled);
    }

    [Fact]
    public void UseFirestore_UsePersistentLocalCache_EnablesMultiTabByDefault()
    {
        var services = new ServiceCollection();

        services.AddFirebase(options => options
            .WithProject("my-project")
            .UseFirestore(fs => fs.UsePersistentLocalCache()));

        var options = services.BuildServiceProvider().GetRequiredService<FirebaseOptions>();

        Assert.NotNull(options.FirestoreOptions);
        Assert.True(options.FirestoreOptions!.PersistentLocalCacheEnabled);
        Assert.True(options.FirestoreOptions.MultiTabEnabled);
        Assert.Null(options.FirestoreOptions.CacheSizeBytes);
    }

    [Fact]
    public void UseFirestore_UsePersistentLocalCache_HonorsSingleTabAndCacheSize()
    {
        var services = new ServiceCollection();

        services.AddFirebase(options => options
            .WithProject("my-project")
            .UseFirestore(fs => fs.UsePersistentLocalCache(multiTab: false, cacheSizeBytes: 5_000_000)));

        var options = services.BuildServiceProvider().GetRequiredService<FirebaseOptions>();

        Assert.True(options.FirestoreOptions!.PersistentLocalCacheEnabled);
        Assert.False(options.FirestoreOptions.MultiTabEnabled);
        Assert.Equal(5_000_000, options.FirestoreOptions.CacheSizeBytes);
    }

    [Fact]
    public async Task FakeFirestore_ClearPersistenceAsync_SucceedsAndCounts()
    {
        var firestore = new FireBlazor.Testing.FakeFirestore();

        var result = await firestore.ClearPersistenceAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, firestore.ClearPersistenceCallCount);
    }

    [Fact]
    public void AddFirebase_ThrowsWithoutProjectId()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IJSRuntime>());

        services.AddFirebase(options => options.WithApiKey("key"));

        var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IFirebase>());
    }
}
