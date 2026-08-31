using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using StatementDelivery.Crypto.Framing;
using StatementDelivery.Crypto.Keys;
using StatementDelivery.Domain.Identifiers;
using Xunit;

namespace UnitTests.Retention;

/// <summary>
/// THE PAYOFF (acceptance check 81), proven locally through the real key hierarchy: destroy the
/// CEK and the still-present ciphertext becomes permanently unreadable — no deletion required,
/// which is the entire argument for envelope encryption under a Compliance-mode Object Lock.
/// </summary>
public sealed class CryptoErasureTests
{
    private static readonly LocalKeyProvider Provider = new(
        Options.Create(new LocalKeyProviderOptions
        {
            MasterSecret = Convert.ToBase64String(
                Convert.FromHexString("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff")),
        }));

    [Fact]
    public async Task Erasure_MakesStatementPermanentlyUndecryptable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var customer = new CustomerId(Guid.CreateVersion7());
        var store = new InMemoryCustomerKeyStore();
        var service = new CustomerKeyService(Provider, store, NullLogger<CustomerKeyService>.Instance);

        // 1. Encrypt a statement through the REAL stack: cohort KEK wraps CEK, CEK wraps DEK,
        //    DEK encrypts bytes.
        byte[] plaintext = new byte[64 * 1024];
        RandomNumberGenerator.Fill(plaintext.AsSpan(0, 4096));
        var ctx = new CryptoContext(Guid.CreateVersion7(), customer.Value, 1);
        var cipher = new FramedAeadCipher();

        byte[] wrappedDek;
        using var ciphertext = new MemoryStream();
        using (var cache = new DataKeyCache(service, Options.Create(new DataKeyCacheOptions()), TimeProvider.System))
        using (DataKeyLease lease = await cache.AcquireAsync(customer, ct).ConfigureAwait(true))
        {
            using var source = new MemoryStream(plaintext, writable: false);
            _ = await cipher.EncryptAsync(source, ciphertext, lease.Key.Span.ToArray(), ctx, ct).ConfigureAwait(true);
            lease.RecordBytes(plaintext.Length);
            wrappedDek = lease.WrappedDek;
        }

        // 2. Destroy the CEK — the erasure executor's mechanics.
        await service.DestroyCekAsync(customer, "POPIA s24 / DSR-2026-0117", ct).ConfigureAwait(true);

        // 3. The object SURVIVES. Nothing deleted it; under a Compliance lock nothing could.
        ciphertext.Length.ShouldBeGreaterThan(plaintext.Length);

        // 4. And yet the read path is dead — from a FRESH cache, as any process after the erasure
        //    (or this one after its cache TTL) would see it. The DEK cannot be unwrapped because
        //    the CEK above it no longer exists anywhere.
        using (var freshCache = new DataKeyCache(service, Options.Create(new DataKeyCacheOptions()), TimeProvider.System))
        {
            _ = await Should.ThrowAsync<CryptoErasedException>(
                () => freshCache.UnwrapDekAsync(customer, wrappedDek, ct)).ConfigureAwait(true);
        }

        // 5. Nor do the bytes yield to a wrong key: without the exact DEK the AEAD refuses,
        //    surfacing the stack's own integrity type rather than a bare crypto error.
        ciphertext.Position = 0;
        using var sink = new MemoryStream();
        _ = await Should.ThrowAsync<CiphertextIntegrityException>(
            () => cipher.DecryptAsync(
                ciphertext, sink, RandomNumberGenerator.GetBytes(32), ctx, ct)).ConfigureAwait(true);

        // 6. New statements cannot be created for the erased customer either: minting a fresh CEK
        //    for a customer whose key was destroyed would quietly resurrect them.
        _ = await Should.ThrowAsync<CryptoErasedException>(
            () => service.GetOrCreateCekAsync(customer, ct)).ConfigureAwait(true);
    }

    [Fact]
    public async Task Erasure_IsIdempotent_SecondDestroyIsANoOp()
    {
        // The executor retries after crashes; the second pass must report "already done", not
        // fail and not double-audit.
        CancellationToken ct = TestContext.Current.CancellationToken;
        var customer = new CustomerId(Guid.CreateVersion7());
        var store = new InMemoryCustomerKeyStore();
        var service = new CustomerKeyService(Provider, store, NullLogger<CustomerKeyService>.Instance);

        using (DataKey cek = await service.GetOrCreateCekAsync(customer, ct).ConfigureAwait(true))
        {
            cek.Span.Length.ShouldBe(32);
        }

        (await store.DestroyAsync(customer, "DSR-1", ct).ConfigureAwait(true)).ShouldBeTrue();
        (await store.DestroyAsync(customer, "DSR-1", ct).ConfigureAwait(true)).ShouldBeFalse(
            "a retried destruction must be a no-op, so ERASURE_COMPLETED audits exactly once");
    }

    private sealed class InMemoryCustomerKeyStore : ICustomerKeyStore
    {
        private readonly Dictionary<Guid, CustomerKeyRecord> _rows = [];

        public Task<CustomerKeyRecord?> FindAsync(CustomerId customer, CancellationToken ct) =>
            Task.FromResult(_rows.TryGetValue(customer.Value, out CustomerKeyRecord? row) ? row : null);

        public Task<bool> TryInsertAsync(CustomerKeyRecord record, CancellationToken ct) =>
            Task.FromResult(_rows.TryAdd(record.CustomerId.Value, record));

        public Task<bool> DestroyAsync(CustomerId customer, string reason, CancellationToken ct)
        {
            if (!_rows.TryGetValue(customer.Value, out CustomerKeyRecord? row) || row.DestroyedAt is not null)
            {
                return Task.FromResult(false);
            }

            _rows[customer.Value] = row with
            {
                WrappedCek = [],
                Status = "DESTROYED",
                DestroyedAt = DateTimeOffset.UtcNow,
            };
            return Task.FromResult(true);
        }
    }
}
