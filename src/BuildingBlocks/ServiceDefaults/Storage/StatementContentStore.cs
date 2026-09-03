using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StatementDelivery.Domain.Statements;

namespace StatementDelivery.ServiceDefaults.Storage;

/// <summary>
/// An open, readable statement. The caller owns the stream and must dispose it.
/// </summary>
/// <param name="Stream">
/// A forward-only, streaming source. NEVER a <see cref="System.IO.MemoryStream"/> over the whole
/// object - buffering a statement into memory would put its plaintext on the large object heap and
/// tie memory use to concurrency.
/// </param>
/// <param name="Length">Total bytes, so <c>Content-Length</c> can be set before streaming begins.</param>
/// <param name="ContentType">The media type.</param>
public sealed record StatementContent(Stream Stream, long Length, string ContentType) : IAsyncDisposable
{
    /// <inheritdoc />
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>
/// Reads statement bytes from wherever they live.
/// </summary>
/// <remarks>
/// <para>
/// THE SEAM THE ENCRYPTION SWAP WENT THROUGH, AND IT HELD. The encrypting object-storage adapter
/// (<c>S3StatementContentStore</c>) went behind this interface and the download gateway's request
/// handling did not change by a single line - only its dependency registration, and one catch clause
/// for a failure mode that did not previously exist. This paragraph is written in the past tense
/// because the claim has been tested rather than intended.
/// </para>
/// <para>
/// That was the design intent, and it was also the test: IF ADDING ENCRYPTION HAD FORCED A CHANGE TO
/// <c>Download.Gateway</c>, THIS PORT WOULD HAVE BEEN THE WRONG SHAPE. Note in particular what is
/// NOT here -
/// no key identifier, no IV, no auth tag, no decryption callback. The gateway asks for bytes at a
/// location and receives a stream; whether those bytes were encrypted at rest is entirely the
/// adapter's business.
/// </para>
/// <para>
/// Returning a stream rather than a byte array is the other half of that: an adapter that decrypts
/// can wrap the source in a <c>CryptoStream</c> and the caller is none the wiser, whereas an
/// adapter returning an array would force the whole statement into memory before the first byte
/// reaches the client.
/// </para>
/// </remarks>
public interface IStatementContentStore
{
    /// <summary>
    /// Opens a statement for reading, or returns null when it is not present.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception for "not found": at this point in the flow the token has
    /// already been consumed, and a missing object is an operational fault to be audited and
    /// answered generically, not an exceptional condition to unwind through.
    /// </remarks>
    /// <param name="location">Where the bytes live. Computed from the database, never discovered by listing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The open content, or null.</returns>
    Task<StatementContent?> OpenReadAsync(StorageLocation location, CancellationToken cancellationToken);
}

/// <summary>Configuration for the filesystem content store.</summary>
public sealed class FileSystemContentStoreOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ContentStore";

    /// <summary>Gets or sets the root directory holding statement files.</summary>
    /// <remarks>A temporary directory in tests. No deployed service binds this section any more.</remarks>
    [Required(AllowEmptyStrings = false)]
    public string RootPath { get; set; } = string.Empty;
}

/// <summary>
/// Reads statements from plain files under a configured root.
/// </summary>
/// <remarks>
/// <para>
/// UNENCRYPTED, AND KEPT ON PURPOSE. No deployed service registers this any more - every one of
/// them was swapped to the encrypting S3 adapter. It survives as the TEST DOUBLE for the port:
/// exercising <c>IStatementContentStore</c> without a bucket, a key hierarchy or a container, which
/// is what keeps the port's own contract (null for absent, a forward-only stream, a traversal guard
/// on the key) testable in isolation from everything that now sits behind it.
/// </para>
/// <para>
/// ⚠ IF THIS EVER APPEARS IN A SERVICE'S Program.cs AGAIN, that service is serving statements in
/// plaintext from a local volume. There is no configuration that makes that correct.
/// </para>
/// </remarks>
public sealed class FileSystemStatementContentStore : IStatementContentStore
{
    private const int StreamBufferSize = 80 * 1024;

    private readonly string _root;

    /// <summary>Initialises a new instance of the <see cref="FileSystemStatementContentStore"/> class.</summary>
    /// <param name="options">Store options.</param>
    public FileSystemStatementContentStore(IOptions<FileSystemContentStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _root = Path.GetFullPath(options.Value.RootPath);
    }

    /// <inheritdoc />
    public Task<StatementContent?> OpenReadAsync(StorageLocation location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);

        // PATH TRAVERSAL GUARD. The key comes from a database column, so it is not attacker-supplied
        // today - but "not attacker-supplied today" is exactly the assumption that stops being true
        // later, and a key of "../../etc/passwd" would otherwise read whatever the process can.
        // Resolve first, then require the result to still be inside the root.
        string candidate = Path.GetFullPath(Path.Combine(_root, location.Key));

        if (!candidate.StartsWith(_root, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(string.Create(
                CultureInfo.InvariantCulture,
                $"Storage key resolves outside the content root: {location.Key}"));
        }

        if (!File.Exists(candidate))
        {
            return Task.FromResult<StatementContent?>(null);
        }

        var info = new FileInfo(candidate);

        // Asynchronous, sequential-scan, bounded buffer. FileOptions.Asynchronous matters: without
        // it the read blocks a thread-pool thread for the whole transfer, and a few hundred
        // concurrent downloads would starve the pool.
        var stream = new FileStream(
            candidate,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = StreamBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });

        return Task.FromResult<StatementContent?>(new StatementContent(stream, info.Length, "application/pdf"));
    }
}

/// <summary>Registers the content store.</summary>
public static class StatementContentStoreExtensions
{
    /// <summary>
    /// Adds the filesystem-backed statement content store.
    /// </summary>
    /// <param name="builder">The host application builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IHostApplicationBuilder AddFileSystemContentStore(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOptions<FileSystemContentStoreOptions>()
            .Bind(builder.Configuration.GetSection(FileSystemContentStoreOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddSingleton<IStatementContentStore, FileSystemStatementContentStore>();

        return builder;
    }
}
