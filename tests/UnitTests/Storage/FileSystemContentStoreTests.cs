using Microsoft.Extensions.Options;
using Shouldly;
using StatementDelivery.Domain.Statements;
using StatementDelivery.ServiceDefaults.Storage;
using Xunit;

namespace UnitTests.Storage;

/// <summary>
/// The <see cref="IStatementContentStore"/> contract, exercised without a bucket or a key.
/// </summary>
/// <remarks>
/// <para>
/// WHY THE FILESYSTEM ADAPTER STILL EXISTS. No deployed service registers it - every one of them
/// was swapped to the encrypting S3 store when envelope encryption landed. It is kept as the test
/// double for the PORT, and this file is what makes that a true statement rather than a comment on
/// dead code.
/// </para>
/// <para>
/// The port has three obligations that have nothing to do with encryption: return null rather than
/// throw for an absent object, hand back a forward-only stream rather than a buffer, and refuse a
/// storage key that escapes its root. Asserting them here means they are covered on every machine,
/// in milliseconds, rather than only on one that has Docker.
/// </para>
/// </remarks>
public sealed class FileSystemContentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "content-store-tests-" + Guid.CreateVersion7().ToString("N"));

    /// <summary>Initialises a new instance of the <see cref="FileSystemContentStoreTests"/> class.</summary>
    public FileSystemContentStoreTests() => Directory.CreateDirectory(_root);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    [Fact]
    public async Task OpenRead_ReturnsNull_WhenTheObjectIsAbsent()
    {
        // NULL, NOT AN EXCEPTION, and the distinction is load-bearing rather than stylistic: by the
        // time the gateway calls this the download token has already been CONSUMED, so a missing
        // object is an operational fault to audit and answer generically - not something to unwind
        // through as though the request were malformed.
        FileSystemStatementContentStore store = CreateStore();

        StatementContent? content = await store
            .OpenReadAsync(new StorageLocation("nothing/here.pdf", "STANDARD", 0), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        content.ShouldBeNull();
    }

    [Fact]
    public async Task OpenRead_ReturnsAForwardOnlyStream_NotABuffer()
    {
        byte[] bytes = [.. Enumerable.Range(0, 4096).Select(i => (byte)i)];
        await WriteObjectAsync("statements/a.pdf", bytes).ConfigureAwait(true);

        FileSystemStatementContentStore store = CreateStore();

        await using StatementContent? content = await store
            .OpenReadAsync(new StorageLocation("statements/a.pdf", "STANDARD", bytes.Length), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        content.ShouldNotBeNull();
        content.Length.ShouldBe(bytes.Length);
        content.ContentType.ShouldBe("application/pdf");

        // NOT a MemoryStream. An adapter that buffered would satisfy every other assertion here and
        // would tie memory use to statement size times concurrency - the exact property the whole
        // streaming design exists to protect.
        content.Stream.ShouldNotBeOfType<MemoryStream>();
        content.Stream.CanRead.ShouldBeTrue();

        using var received = new MemoryStream();
        await content.Stream.CopyToAsync(received, TestContext.Current.CancellationToken).ConfigureAwait(true);
        received.ToArray().ShouldBe(bytes);
    }

    [Theory]
    [InlineData("../escaped.pdf")]
    [InlineData("statements/../../escaped.pdf")]
    [InlineData("statements/../../../../../../etc/passwd")]
    public async Task OpenRead_RefusesAKeyThatEscapesTheRoot(string key)
    {
        // The storage key comes from a database column, so it is not attacker-supplied TODAY. That
        // is precisely the assumption that stops being true later - and the guard costs one
        // comparison. The encrypting adapter answers the same threat differently, by DERIVING the
        // key from row metadata rather than accepting one, but both are answering this.
        FileSystemStatementContentStore store = CreateStore();

        _ = await Should.ThrowAsync<UnauthorizedAccessException>(() =>
            store.OpenReadAsync(new StorageLocation(key, "STANDARD", 0), TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
    }

    private FileSystemStatementContentStore CreateStore() =>
        new(Options.Create(new FileSystemContentStoreOptions { RootPath = _root }));

    private async Task WriteObjectAsync(string key, byte[] bytes)
    {
        string path = Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
}
