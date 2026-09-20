using System.Runtime.InteropServices;

namespace AudioTranscriber.Granite;

public static class GraniteModelMetadata
{
    public const string ModelId = "ibm-granite/granite-embedding-278m-multilingual";
    public const string Revision = "a9cb5338491faf32b73dd17b714a31821c021bbf";
    public const string RepositoryUrl =
        "https://huggingface.co/ibm-granite/granite-embedding-278m-multilingual";
    public const string ModelFileName = "model.onnx";
    public const string TokenizerFileName = "sentencepiece.bpe.model";
    public const string ModelSha256 =
        "aefac97b384f92932a61a19900d41c870679d5b8e6ceb682768eb153d0e31c7d";
    public const string TokenizerSha256 =
        "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865";
    public const int EmbeddingDimensions = 768;
    public const int MaxTokens = 512;
    public const string Pooling = "CLS";
    public const string Normalization = "L2";
    public const string PackageVersion = "0.1.0";
    public const string OnnxRuntimePackageVersion = "1.30.0";
    public const string TokenizersPackageVersion = "2.0.0";

    public static Uri AssetUri(string fileName) =>
        new($"{RepositoryUrl}/resolve/{Revision}/{fileName}");
}

public sealed class GraniteModelConfiguration
{
    public GraniteModelConfiguration(
        string? cacheRoot = null,
        bool allowNetworkDownload = false,
        bool requireAvx2 = false,
        string? repositoryRoot = null)
    {
        ModelId = GraniteModelMetadata.ModelId;
        Revision = GraniteModelMetadata.Revision;
        CacheDirectory = GraniteCachePathResolver.Resolve(cacheRoot, repositoryRoot);
        AllowNetworkDownload = allowNetworkDownload;
        RequireAvx2 = requireAvx2;
    }

    public string ModelId { get; }

    public string Revision { get; }

    public string CacheDirectory { get; }

    public bool AllowNetworkDownload { get; }

    public bool RequireAvx2 { get; }

    public int EmbeddingDimensions => GraniteModelMetadata.EmbeddingDimensions;

    public int MaxTokens => GraniteModelMetadata.MaxTokens;

    public GraniteAssetDescriptor ModelAsset =>
        new(
            GraniteAssetKind.Model,
            GraniteModelMetadata.ModelFileName,
            GraniteModelMetadata.ModelSha256,
            GraniteModelMetadata.AssetUri(GraniteModelMetadata.ModelFileName));

    public GraniteAssetDescriptor TokenizerAsset =>
        new(
            GraniteAssetKind.Tokenizer,
            GraniteModelMetadata.TokenizerFileName,
            GraniteModelMetadata.TokenizerSha256,
            GraniteModelMetadata.AssetUri(GraniteModelMetadata.TokenizerFileName));

    internal GraniteModelProvenance Provenance =>
        new(
            Provider: "IBM Granite/ONNX Runtime",
            Model: ModelId,
            PackageId:
                $"AudioTranscriber.Granite;Microsoft.ML.OnnxRuntime;Microsoft.ML.Tokenizers",
            PackageVersion:
                $"{GraniteModelMetadata.PackageVersion};" +
                $"{GraniteModelMetadata.OnnxRuntimePackageVersion};" +
                GraniteModelMetadata.TokenizersPackageVersion,
            Source:
                $"repository={GraniteModelMetadata.RepositoryUrl};" +
                $"revision={Revision};" +
                $"modelAsset={ModelAsset.FileName};" +
                $"modelSha256={ModelAsset.Sha256};" +
                $"tokenizerAsset={TokenizerAsset.FileName};" +
                $"tokenizerSha256={TokenizerAsset.Sha256};" +
                $"pooling={GraniteModelMetadata.Pooling};" +
                $"normalization={GraniteModelMetadata.Normalization}",
            CachePath: CacheDirectory,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["embeddingDimensions"] = EmbeddingDimensions.ToString(),
                ["maxTokens"] = MaxTokens.ToString(),
                ["allowNetworkDownload"] = AllowNetworkDownload.ToString(),
                ["requireAvx2"] = RequireAvx2.ToString(),
            });

    internal void ValidateCapabilities(GraniteRuntimeCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (RequireAvx2 && !capabilities.Avx2Supported)
        {
            throw new GraniteModelAssetException(
                GraniteDiagnosticCode.UnsupportedCpu,
                GraniteAssetKind.Model,
                "Granite embedding requires AVX2, but the current process does not support AVX2.");
        }
    }

    internal sealed record GraniteModelProvenance(
        string Provider,
        string Model,
        string PackageId,
        string PackageVersion,
        string Source,
        string CachePath,
        IReadOnlyDictionary<string, string> Metadata);
}

public enum GraniteAssetKind
{
    Model,
    Tokenizer,
}

public enum GraniteDiagnosticCode
{
    MissingAsset,
    HashMismatch,
    IncompatibleAsset,
    DownloadFailed,
    UnsupportedCpu,
    InvalidOutput,
}

public sealed record GraniteAssetDescriptor
{
    public GraniteAssetDescriptor(
        GraniteAssetKind kind,
        string fileName,
        string sha256,
        Uri downloadUri)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Asset file name cannot be empty.", nameof(fileName));
        }

        if (sha256 is null || sha256.Length != 64 ||
            sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Asset SHA-256 must be a 64-character hexadecimal value.",
                nameof(sha256));
        }

        ArgumentNullException.ThrowIfNull(downloadUri);
        if (!downloadUri.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "Asset download URI must be absolute.",
                nameof(downloadUri));
        }

        Kind = kind;
        FileName = fileName;
        Sha256 = sha256.ToLowerInvariant();
        DownloadUri = downloadUri;
    }

    public GraniteAssetKind Kind { get; }

    public string FileName { get; }

    public string Sha256 { get; }

    public Uri DownloadUri { get; }
}

public sealed class GraniteModelAssetException : InvalidOperationException
{
    public GraniteModelAssetException(
        GraniteDiagnosticCode diagnosticCode,
        GraniteAssetKind assetKind,
        string message,
        string? expectedSha256 = null,
        string? actualSha256 = null)
        : base(message)
    {
        DiagnosticCode = diagnosticCode;
        AssetKind = assetKind;
        ExpectedSha256 = expectedSha256;
        ActualSha256 = actualSha256;
    }

    public GraniteDiagnosticCode DiagnosticCode { get; }

    public GraniteAssetKind AssetKind { get; }

    public string? ExpectedSha256 { get; }

    public string? ActualSha256 { get; }
}

public static class GraniteCachePathResolver
{
    public static string Resolve(string? cacheRoot = null, string? repositoryRoot = null)
    {
        var root = cacheRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.GetTempPath();
            }
        }

        var path = Path.GetFullPath(
            Path.Combine(
                root,
                "AudioTranscriber",
                "models",
                "granite-embedding-278m-multilingual",
                GraniteModelMetadata.Revision));

        if (!string.IsNullOrWhiteSpace(repositoryRoot) &&
            IsWithin(
                ResolveExistingDirectoryLinks(path),
                ResolveExistingDirectoryLinks(repositoryRoot)))
        {
            throw new ArgumentException(
                "Granite model assets must be cached outside the repository.",
                nameof(cacheRoot));
        }

        return path;
    }

    public static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = fullRoot + Path.DirectorySeparatorChar;

        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveExistingDirectoryLinks(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        var components = new Stack<string>();
        for (var current = directory; current.Parent is not null; current = current.Parent)
        {
            components.Push(current.Name);
        }

        var resolved = directory.Root;
        while (components.Count > 0)
        {
            var component = components.Pop();
            var candidate = new DirectoryInfo(
                Path.Combine(resolved.FullName, component));
            if (!candidate.Exists)
            {
                resolved = candidate;
                while (components.Count > 0)
                {
                    resolved = new DirectoryInfo(
                        Path.Combine(resolved.FullName, components.Pop()));
                }

                break;
            }

            resolved = candidate.ResolveLinkTarget(returnFinalTarget: true)
                as DirectoryInfo ?? candidate;
        }

        return resolved.FullName;
    }
}

public sealed record GraniteRuntimeCapabilities(
    Architecture Architecture,
    bool Avx2Supported);

public interface IGraniteCpuCapabilityProbe
{
    GraniteRuntimeCapabilities Probe();
}

public sealed class SystemGraniteCpuCapabilityProbe : IGraniteCpuCapabilityProbe
{
    public GraniteRuntimeCapabilities Probe() =>
        new(
            RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.ProcessArchitecture is Architecture.X86 or Architecture.X64 &&
            System.Runtime.Intrinsics.X86.Avx2.IsSupported);
}
