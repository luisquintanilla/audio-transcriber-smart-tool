using AudioTranscriber.Granite;

namespace AudioTranscriber.Tests;

public sealed class GraniteConfigurationTests
{
    [Fact]
    public void Configuration_UsesExplicitModelTokenizerAndRevision()
    {
        using var temporary = new GraniteTestDirectory();
        var configuration = new GraniteModelConfiguration(temporary.Path);

        Assert.Equal(
            "ibm-granite/granite-embedding-278m-multilingual",
            configuration.ModelId);
        Assert.Equal(
            "a9cb5338491faf32b73dd17b714a31821c021bbf",
            configuration.Revision);
        Assert.Equal("model.onnx", configuration.ModelAsset.FileName);
        Assert.Equal("sentencepiece.bpe.model", configuration.TokenizerAsset.FileName);
        Assert.Equal(
            "https://huggingface.co/ibm-granite/granite-embedding-278m-multilingual/" +
            "resolve/a9cb5338491faf32b73dd17b714a31821c021bbf/model.onnx",
            configuration.ModelAsset.DownloadUri.AbsoluteUri);
        Assert.Equal(768, configuration.EmbeddingDimensions);
        Assert.Equal(512, configuration.MaxTokens);
    }

    [Fact]
    public void CacheResolver_UsesDeterministicExternalPath()
    {
        using var temporary = new GraniteTestDirectory();
        var first = GraniteCachePathResolver.Resolve(temporary.Path);
        var second = GraniteCachePathResolver.Resolve(temporary.Path);

        Assert.Equal(first, second);
        Assert.Contains(
            GraniteModelMetadata.Revision,
            first,
            StringComparison.Ordinal);
        Assert.EndsWith(
            Path.Combine(
                "granite-embedding-278m-multilingual",
                GraniteModelMetadata.Revision),
            first,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Configuration_DoesNotResolveCacheInsideRepository()
    {
        using var temporary = new GraniteTestDirectory();
        var repositoryRoot = Path.Combine(temporary.Path, "repository");
        Directory.CreateDirectory(repositoryRoot);

        var configuration = new GraniteModelConfiguration(
            Path.Combine(temporary.Path, "cache"),
            repositoryRoot: repositoryRoot);

        Assert.False(GraniteCachePathResolver.IsWithin(
            configuration.CacheDirectory,
            repositoryRoot));
        Assert.Throws<ArgumentException>(
            () => GraniteCachePathResolver.Resolve(repositoryRoot, repositoryRoot));
    }

    [Fact]
    public void CacheResolver_RejectsSymlinkedCacheInsideRepository()
    {
        using var temporary = new GraniteTestDirectory();
        var repositoryRoot = Path.Combine(temporary.Path, "repository");
        Directory.CreateDirectory(repositoryRoot);
        var cacheAlias = Path.Combine(temporary.Path, "cache-alias");
        try
        {
            Directory.CreateSymbolicLink(cacheAlias, repositoryRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.Throws<ArgumentException>(
            () => GraniteCachePathResolver.Resolve(cacheAlias, repositoryRoot));
    }

    [Fact]
    public void CacheResolver_RejectsDanglingSymlinkedCacheInsideRepository()
    {
        using var temporary = new GraniteTestDirectory();
        var repositoryRoot = Path.Combine(temporary.Path, "repository");
        Directory.CreateDirectory(repositoryRoot);
        var cacheAlias = Path.Combine(temporary.Path, "cache-alias");
        try
        {
            Directory.CreateSymbolicLink(
                cacheAlias,
                Path.Combine(repositoryRoot, "new-cache"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.Throws<ArgumentException>(
            () => GraniteCachePathResolver.Resolve(cacheAlias, repositoryRoot));
    }

    [Fact]
    public void Configuration_DefaultsToOfflineDeterministicMode()
    {
        using var temporary = new GraniteTestDirectory();
        var configuration = new GraniteModelConfiguration(temporary.Path);

        Assert.False(configuration.AllowNetworkDownload);
        Assert.False(configuration.RequireAvx2);
        Assert.True(Path.IsPathFullyQualified(configuration.CacheDirectory));
        Assert.DoesNotContain(
            "model.onnx",
            Directory.EnumerateFiles(temporary.Path, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .OfType<string>());
    }
}
