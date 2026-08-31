using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using StatementDelivery.Crypto.Framing;
using StatementDelivery.Crypto.Keys;
using StatementDelivery.Domain.Identifiers;
using StatementDelivery.Domain.ValueObjects;
using StatementDelivery.ServiceDefaults.Storage;
using Xunit;

namespace UnitTests.Storage;

/// <summary>
/// THE SEAM THAT BROKE. The Prompt 5 audit's CRITICAL: RenderPipeline hands the writer a
/// NON-SEEKABLE stream, the writer computed Content-Length from CanSeek, and the AWS SDK refused
/// the upload client-side. Every S3-writing test used a seekable MemoryStream, so the untested
/// branch was exactly the branch production takes.
/// </summary>
/// <remarks>
/// <para>
/// These tests run WITHOUT Docker, against a captured stub of <see cref="IAmazonS3"/>, so the
/// contract the SDK enforces - a seekable body, or a declared length - is asserted on every build
/// on every machine. The SDK's actual refusal was proven separately by probe
/// (<c>AmazonS3Exception: Could not determine content length</c>); what these tests pin is the
/// adapter's side of the bargain: whatever shape the plaintext arrives in, the PUT it emits
/// carries a real Content-Length and a seekable body.
/// </para>
/// <para>
/// The companion Docker-gated test (<c>EncryptedStorageTests.Write_FromNonSeekableStream_RoundTrips</c>)
/// proves the same seam against a real MinIO. Both exist on purpose: this one is the always-on
/// tripwire, that one is the end-to-end truth.
/// </para>
/// </remarks>
public sealed class ContentWriterSeamTests
{
    private static readonly Guid Customer = Guid.CreateVersion7();
    private static readonly Guid StatementId = Guid.CreateVersion7();
    private static readonly Guid Account = Guid.CreateVersion7();

    [Fact]
    public async Task ContentWriter_AcceptsNonSeekableStream()
    {
        // ~600KB, deliberately past every internal buffer threshold in the pipeline, from a
        // stream that THROWS on any backward-looking member - the exact shape the render pipe
        // produces. Before the fix this failed: no Content-Length was set and the body handed to
        // the SDK was the non-seekable encrypting stream itself.
        byte[] plaintext = DeterministicBytes(600 * 1024);
        (S3StatementContentStore store, CapturedPut capture, string spoolDir) = BuildStore();

        try
        {
            StoredObject stored;
            using (var source = new NonSeekableReadStream(new MemoryStream(plaintext, writable: false)))
            {
                stored = await store.WriteAsync(
                    source,
                    new CryptoContext(StatementId, Customer, 1),
                    new AccountId(Account),
                    StatementPeriod.ForMonth(2026, 8),
                    CohortAssignment.KekIdFor(CohortAssignment.ForCustomer(new CustomerId(Customer))),
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            // The SDK's contract, asserted on our side of it: a declared length and a seekable body.
            capture.Request.ShouldNotBeNull("the PUT never happened");
            (capture.Request!.Headers.ContentLength > 0).ShouldBeTrue(
                "the PUT must declare Content-Length - without it the SDK throws 'Could not determine content length'");
            capture.BodyWasSeekable.ShouldBeTrue(
                "the body handed to the SDK must be seekable; the raw encrypting stream is not");
            capture.Request.Headers.ContentLength.ShouldBe(stored.CiphertextLength);

            // And the adapter's own accounting is right for a source it could not pre-measure.
            stored.PlaintextLength.ShouldBe(plaintext.Length);
            stored.Envelope.ContentSha256.ShouldBe(SHA256.HashData(plaintext));
        }
        finally
        {
            Directory.Delete(spoolDir, recursive: true);
        }
    }

    [Fact]
    public async Task TempSpool_HoldsCiphertextOnly_NeverPlaintext()
    {
        // R4. The whole reason the spool lives INSIDE the adapter, after encryption: what touches
        // disk is unreadable without the DEK. The captured body must be SDP1 framed ciphertext,
        // and must not contain the plaintext's distinctive bytes anywhere.
        byte[] plaintext = DeterministicBytes(64 * 1024);

        // A recognisable needle: a PDF header plus a long unique marker, planted mid-plaintext.
        "%PDF-1.7"u8.ToArray().CopyTo(plaintext, 0);
        byte[] needle = SHA256.HashData("plaintext-needle"u8.ToArray());
        needle.CopyTo(plaintext, 32 * 1024);

        (S3StatementContentStore store, CapturedPut capture, string spoolDir) = BuildStore();

        try
        {
            using (var source = new NonSeekableReadStream(new MemoryStream(plaintext, writable: false)))
            {
                _ = await store.WriteAsync(
                    source,
                    new CryptoContext(StatementId, Customer, 2),
                    new AccountId(Account),
                    StatementPeriod.ForMonth(2026, 8),
                    CohortAssignment.KekIdFor(CohortAssignment.ForCustomer(new CustomerId(Customer))),
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            byte[] body = capture.Body.ShouldNotBeNull();
            body.AsSpan(0, 4).SequenceEqual(FrameFormat.Magic).ShouldBeTrue(
                "what reaches storage (and the spool before it) must be SDP1 framed ciphertext");
            body.AsSpan().IndexOf("%PDF-"u8).ShouldBe(-1, "no plaintext PDF header may survive encryption");
            body.AsSpan().IndexOf(needle).ShouldBe(-1, "no plaintext content may survive encryption");
        }
        finally
        {
            Directory.Delete(spoolDir, recursive: true);
        }
    }

    [Fact]
    public async Task TempSpool_FaultMidEncryption_Cleanup()
    {
        // R5. A plaintext source that dies mid-read - the renderer faulting - must leave the
        // spool directory empty. DeleteOnClose is the mechanism; this proves the handle is
        // actually disposed on the failure path rather than leaked.
        (S3StatementContentStore store, CapturedPut capture, string spoolDir) = BuildStore();

        try
        {
            using var source = new NonSeekableReadStream(
                new FaultingStream(DeterministicBytes(512 * 1024), faultAfter: 100 * 1024));

            _ = await Should.ThrowAsync<IOException>(
                () => store.WriteAsync(
                    source,
                    new CryptoContext(StatementId, Customer, 3),
                    new AccountId(Account),
                    StatementPeriod.ForMonth(2026, 8),
                    CohortAssignment.KekIdFor(CohortAssignment.ForCustomer(new CustomerId(Customer))),
                    TestContext.Current.CancellationToken)).ConfigureAwait(true);

            capture.Request.ShouldBeNull("a failed encryption must never reach the PUT");
            Directory.EnumerateFileSystemEntries(spoolDir).ShouldBeEmpty(
                "the spool file must be deleted on every failure path");
        }
        finally
        {
            Directory.Delete(spoolDir, recursive: true);
        }
    }

    [Fact]
    public async Task TempSpool_SuccessPath_Cleanup()
    {
        // R5's other half: success leaves nothing behind either.
        (S3StatementContentStore store, CapturedPut capture, string spoolDir) = BuildStore();

        try
        {
            using (var source = new NonSeekableReadStream(
                new MemoryStream(DeterministicBytes(8 * 1024), writable: false)))
            {
                _ = await store.WriteAsync(
                    source,
                    new CryptoContext(StatementId, Customer, 4),
                    new AccountId(Account),
                    StatementPeriod.ForMonth(2026, 8),
                    CohortAssignment.KekIdFor(CohortAssignment.ForCustomer(new CustomerId(Customer))),
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            Directory.EnumerateFileSystemEntries(spoolDir).ShouldBeEmpty(
                "the spool file must not outlive the write");
        }
        finally
        {
            Directory.Delete(spoolDir, recursive: true);
        }
    }

    // ---------------------------------------------------------------------------------------------

    /// <summary>Builds the REAL adapter over a stubbed S3 that captures the PUT.</summary>
    [Fact]
    public async Task TempSpool_MissingDirectory_IsCreatedOnDemand()
    {
        // A configured-but-absent spool directory is the fresh-node / wiped-temp-volume shape:
        // the write must create it and proceed, not fail every item until an operator mkdirs.
        // (A directory that CANNOT be created - read-only filesystem - still fails, and the
        // spool-directory readiness probe reports it before traffic arrives.)
        (S3StatementContentStore store, CapturedPut capture, string spoolDir) = BuildStore();

        try
        {
            Directory.Delete(spoolDir, recursive: true);

            using (var source = new NonSeekableReadStream(new MemoryStream(new byte[4096])))
            {
                _ = await store.WriteAsync(
                    source,
                    new CryptoContext(StatementId, Customer, 1),
                    new AccountId(Account),
                    StatementPeriod.ForMonth(2026, 8),
                    CohortAssignment.KekIdFor(CohortAssignment.ForCustomer(new CustomerId(Customer))),
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            capture.Request.ShouldNotBeNull("the PUT must still happen after creating the directory");
            Directory.EnumerateFileSystemEntries(spoolDir).ShouldBeEmpty(
                "the created directory must be left clean after the upload");
        }
        finally
        {
            if (Directory.Exists(spoolDir))
            {
                Directory.Delete(spoolDir, recursive: true);
            }
        }
    }

    private static (S3StatementContentStore Store, CapturedPut Capture, string SpoolDir) BuildStore()
    {
        string spoolDir = Path.Combine(Path.GetTempPath(), "sdp-spool-test-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(spoolDir);

        var capture = new CapturedPut();
        IAmazonS3 s3 = Substitute.For<IAmazonS3>();
        _ = s3.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var request = call.Arg<PutObjectRequest>();
                capture.Request = request;

                // Captured AT PUT TIME: the spool stream is disposed by the time the test's
                // assertions run, and a disposed FileStream reports CanSeek = false.
                capture.BodyWasSeekable = request.InputStream.CanSeek;

                // Drain the body the way the SDK would, so lengths and digests are real.
                using var sink = new MemoryStream();
                await request.InputStream.CopyToAsync(sink, call.Arg<CancellationToken>()).ConfigureAwait(false);
                capture.Body = sink.ToArray();

                return new PutObjectResponse();
            });

        var provider = new LocalKeyProvider(Options.Create(new LocalKeyProviderOptions
        {
            MasterSecret = Convert.ToBase64String(SHA256.HashData("seam-test-master"u8.ToArray())),
        }));

        var customerKeys = new CustomerKeyService(
            provider, new InMemoryCustomerKeyStore(), NullLogger<CustomerKeyService>.Instance);
        var cache = new DataKeyCache(customerKeys, Options.Create(new DataKeyCacheOptions()), TimeProvider.System);

        var store = new S3StatementContentStore(
            s3,
            cache,
            Options.Create(new ObjectStorageOptions { BucketName = "seam-test", ServiceUrl = "http://stub" }),
            Options.Create(new ObjectLockOptions { Mode = "GOVERNANCE" }),
            Options.Create(new CipherOptions()),
            Options.Create(new RetentionOptions()),
            spool: Options.Create(new SpoolOptions { Directory = spoolDir }));

        return (store, capture, spoolDir);
    }

    private static byte[] DeterministicBytes(int length)
    {
        byte[] buffer = new byte[length];
        for (int i = 0; i < length; i++)
        {
            buffer[i] = (byte)(i * 31 % 251);
        }

        return buffer;
    }

    private sealed class CapturedPut
    {
        public PutObjectRequest? Request { get; set; }

        public byte[]? Body { get; set; }

        public bool BodyWasSeekable { get; set; }
    }

    /// <summary>An in-memory <see cref="ICustomerKeyStore"/> - no database, same contract.</summary>
    private sealed class InMemoryCustomerKeyStore : ICustomerKeyStore
    {
        private readonly Dictionary<Guid, CustomerKeyRecord> _rows = [];

        public Task<CustomerKeyRecord?> FindAsync(CustomerId customer, CancellationToken ct) =>
            Task.FromResult(_rows.TryGetValue(customer.Value, out CustomerKeyRecord? row) ? row : null);

        public Task<bool> TryInsertAsync(CustomerKeyRecord record, CancellationToken ct)
        {
            bool inserted = _rows.TryAdd(record.CustomerId.Value, record);
            return Task.FromResult(inserted);
        }

        public Task<bool> DestroyAsync(CustomerId customer, string reason, CancellationToken ct)
        {
            if (!_rows.TryGetValue(customer.Value, out CustomerKeyRecord? row) || row.DestroyedAt is not null)
            {
                return Task.FromResult(false);
            }

            _rows[customer.Value] = row with { WrappedCek = [], Status = "DESTROYED", DestroyedAt = DateTimeOffset.UtcNow };
            return Task.FromResult(true);
        }
    }

    /// <summary>A source that faults after N bytes - the renderer dying mid-document.</summary>
    private sealed class FaultingStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _faultAfter;
        private int _position;

        public FaultingStream(byte[] data, int faultAfter)
        {
            _data = data;
            _faultAfter = faultAfter;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _faultAfter)
            {
                throw new IOException("Simulated renderer failure mid-document.");
            }

            int available = Math.Min(count, Math.Min(_faultAfter - _position, _data.Length - _position));
            Array.Copy(_data, _position, buffer, offset, available);
            _position += available;
            return available;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// Wraps a stream and refuses every backward-looking member - the shape a pipe reader presents.
/// </summary>
/// <remarks>
/// THE TEST DOUBLE ADR-0032 PRESCRIBES. Convenient test inputs (a seekable MemoryStream) exercise
/// the branch production does not take; wrap the input in this wherever a stream crosses a port,
/// and the test walks the production branch instead.
/// </remarks>
public sealed class NonSeekableReadStream : Stream
{
    private readonly Stream _inner;

    /// <summary>Initialises a new instance of the <see cref="NonSeekableReadStream"/> class.</summary>
    /// <param name="inner">The source to wrap. Disposed with this stream.</param>
    public NonSeekableReadStream(Stream inner) => _inner = inner;

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("Non-seekable: no Length.");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("Non-seekable: no Position.");
        set => throw new NotSupportedException("Non-seekable: no Position.");
    }

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("Non-seekable: no Seek.");

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException("Non-seekable: no SetLength.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Read-only.");

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
