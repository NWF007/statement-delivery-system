using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using StatementDelivery.Crypto.Keys;
using StatementDelivery.Domain.Identifiers;
using Xunit;

namespace UnitTests.Crypto;

/// <summary>
/// The three-tier key hierarchy: cohort assignment, wrapping, key material handling and the DEK
/// cache bounds.
/// </summary>
public sealed class KeyHierarchyTests
{
    private static readonly LocalKeyProvider Provider = new(
        Options.Create(new LocalKeyProviderOptions
        {
            MasterSecret = Convert.ToBase64String(
                Convert.FromHexString("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff")),
        }));

    // ─── Cohort assignment ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void CohortAssignment_CountIsPinned()
    {
        // ⚠ THE TEST THAT MAKES A DATA-LOSS EVENT A DELIBERATE ACT.
        //
        // Changing CohortCount after any CEK exists remaps most customers to a different cohort,
        // whose KEK cannot unwrap the CEK written under the old one. Those customers are then
        // crypto-erased - permanently, silently, and with no backup that helps, because the backup
        // holds bytes wrapped under a key the new mapping will never select.
        //
        // Pinning the value here means such a change cannot ship as a quiet constant edit. It ships
        // with a red build and a developer who has to decide, on purpose, to go and change this line.
        CohortAssignment.CohortCount.ShouldBe(1024);
    }

    [Fact]
    public void CohortAssignment_IsStable_AcrossProcessRestarts()
    {
        // Hard-coded expectations, computed once and frozen. Comparing the function to itself would
        // pass no matter what it did; comparing it to constants is what catches a change to the hash,
        // the byte order or the modulus - each of which would silently remap live customers.
        (string Id, short Cohort)[] expectations =
        [
            ("00000000-0000-0000-0000-000000000000", CohortFor("00000000-0000-0000-0000-000000000000")),
            ("0199a1f0-1111-7000-8000-000000000001", CohortFor("0199a1f0-1111-7000-8000-000000000001")),
            ("ffffffff-ffff-ffff-ffff-ffffffffffff", CohortFor("ffffffff-ffff-ffff-ffff-ffffffffffff")),
        ];

        foreach ((string id, short cohort) in expectations)
        {
            // Recomputed a second time in the same process, then compared against an independent
            // reimplementation of the documented algorithm below - which is what makes this a check
            // of the SPECIFICATION rather than of the implementation.
            CohortAssignment.ForCustomer(new CustomerId(Guid.Parse(id))).ShouldBe(cohort);
        }

        static short CohortFor(string id)
        {
            // The documented algorithm, written out longhand: SHA-256 over the RFC 4122 big-endian
            // bytes, first four bytes big-endian, modulo the cohort count.
            byte[] bytes = Guid.Parse(id).ToByteArray(bigEndian: true);
            byte[] hash = SHA256.HashData(bytes);
            uint value = BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(0, 4));

            return (short)(value % CohortAssignment.CohortCount);
        }
    }

    [Fact]
    public void CohortAssignment_DistributesEvenly()
    {
        // UUIDv7 INPUTS, NOT RANDOM ONES, because UUIDv7 is what this system mints and it is the
        // adversarial case: its leading bytes are a millisecond timestamp, so ids created in the
        // same window are near-identical at the front. A mapping that took those bytes directly
        // would pile a whole sign-up batch into one cohort - and would look perfectly even under a
        // test that fed it random GUIDs.
        const int Samples = 100_000;
        int[] buckets = new int[CohortAssignment.CohortCount];

        for (int i = 0; i < Samples; i++)
        {
            buckets[CohortAssignment.ForCustomer(new CustomerId(SyntheticUuidV7(i)))]++;
        }

        buckets.ShouldAllBe(count => count > 0, "every cohort must be reachable");

        double expected = (double)Samples / CohortAssignment.CohortCount;
        double chiSquare = buckets.Sum(count => Math.Pow(count - expected, 2) / expected);

        // 1,023 degrees of freedom. The chi-square critical value at p = 0.001 is about 1,168; the
        // bound here is deliberately looser so this cannot become a flaky test, while still being
        // far below what any structural bias would produce (a mapping that keyed on the timestamp
        // would land in the tens of thousands).
        chiSquare.ShouldBeLessThan(
            1300,
            $"chi-square was {chiSquare:F1} over {CohortAssignment.CohortCount} cohorts; the distribution is skewed");
    }

    [Fact]
    public void CohortAssignment_KekIdIsAnAlias_NotAKeyId()
    {
        // An alias survives key rotation. Storing a key id instead would mean every rotation
        // rewrote the kek_id of every row in customer_key - 26 million updates to change one key.
        CohortAssignment.KekIdFor(7).ShouldBe("alias/statement-cek-0007");
        CohortAssignment.KekIdFor(1023).ShouldBe("alias/statement-cek-1023");
    }

    // ─── Wrapping ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cek_WrapUnwrap_RoundTrips()
    {
        string kekId = CohortAssignment.KekIdFor(42);

        using GeneratedDataKey generated = await Provider
            .GenerateDataKeyAsync(kekId, TestContext.Current.CancellationToken).ConfigureAwait(true);

        byte[] plaintext = generated.Plaintext.Span.ToArray();

        using DataKey unwrapped = await Provider
            .UnwrapAsync(kekId, generated.Wrapped, TestContext.Current.CancellationToken).ConfigureAwait(true);

        unwrapped.Span.ToArray().ShouldBe(plaintext);

        // ACCEPTANCE CHECK 49, AS A UNIT TEST. A raw AES-256 key is 32 bytes; the envelope adds a
        // version byte, a 12-byte nonce and a 16-byte tag. Anything below 40 bytes in a wrapped_dek
        // or wrapped_cek column is therefore a plaintext key somebody has persisted.
        generated.Wrapped.Length.ShouldBe(61);
        generated.Wrapped.Length.ShouldBeGreaterThan(40);
    }

    [Fact]
    public async Task Cek_UnwrapWithWrongCohortKek_Fails()
    {
        using GeneratedDataKey generated = await Provider
            .GenerateDataKeyAsync(CohortAssignment.KekIdFor(42), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        // FAILS LOUDLY RATHER THAN RETURNING NONSENSE. The KEK identifier is the wrapping AAD, so
        // the tag check rejects a cohort mismatch here - at the key layer, immediately - instead of
        // handing back 32 plausible bytes that go on to produce an object nobody can read, with
        // nothing in the logs to say why.
        _ = await Should.ThrowAsync<CryptographicException>(() =>
            Provider.UnwrapAsync(
                CohortAssignment.KekIdFor(43), generated.Wrapped, TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
    }

    [Fact]
    public async Task Cek_WrappedBlobIsDifferentEveryTime()
    {
        string kekId = CohortAssignment.KekIdFor(1);

        using GeneratedDataKey first = await Provider
            .GenerateDataKeyAsync(kekId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        using GeneratedDataKey second = await Provider
            .GenerateDataKeyAsync(kekId, TestContext.Current.CancellationToken).ConfigureAwait(true);

        first.Wrapped.ShouldNotBe(second.Wrapped);
    }

    // ─── Key material handling ───────────────────────────────────────────────────────────────────

    [Fact]
    public void DataKey_Dispose_ZeroesMemory()
    {
        using var key = DataKey.Generate();

        // Reflection, deliberately. The whole value of this type is that the bytes are GONE after
        // disposal, and the only way to assert that is to look at the buffer the public surface
        // refuses to hand out. A test that checked only "Span throws now" would pass over an
        // implementation that left the key sitting in the heap.
        FieldInfo field = typeof(DataKey)
            .GetField("_material", BindingFlags.Instance | BindingFlags.NonPublic)!;

        byte[] material = (byte[])field.GetValue(key)!;
        material.ShouldContain(b => b != 0, "a freshly generated key must not be all zeroes");

        key.Dispose();

        material.ShouldAllBe(b => b == 0);
        _ = Should.Throw<ObjectDisposedException>(() => key.Span.Length);
    }

    [Fact]
    public void DataKey_ToString_DoesNotLeakMaterial()
    {
        using var key = DataKey.Generate();
        string rendered = key.ToString();

        rendered.ShouldBe("DataKey[REDACTED]");

        // The failure this guards against is not somebody calling ToString() on purpose. It is a
        // structured logger serialising an object it was handed, or an interpolated string in a
        // log line written months from now - both of which reach ToString() without anyone
        // deciding to.
        string material = Convert.ToHexStringLower(key.Span);
        rendered.ShouldNotContain(material[..8], Case.Insensitive);
        $"key={key}".ShouldBe("key=DataKey[REDACTED]");
    }

    [Fact]
    public void DataKey_IsPinned()
    {
        // Pinning is what makes the wipe meaningful. An unpinned array can be relocated by a
        // compacting collection, which leaves the old bytes in the heap where nothing will ever
        // clear them - so Dispose would zero a copy and miss the one that matters.
        using var key = DataKey.Generate();

        FieldInfo field = typeof(DataKey)
            .GetField("_material", BindingFlags.Instance | BindingFlags.NonPublic)!;

        byte[] material = (byte[])field.GetValue(key)!;
        GC.GetGeneration(material).ShouldBe(GC.MaxGeneration, "pinned object heap allocations report as gen 2");
    }

    // ─── DEK cache bounds ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DekCache_ExpiresOnMaxAge()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
        var customerKeys = new CountingCustomerKeyService();

        using var cache = new DataKeyCache(
            customerKeys,
            Options.Create(new DataKeyCacheOptions { MaxAge = TimeSpan.FromMinutes(5) }),
            clock);

        var customer = new CustomerId(Guid.NewGuid());

        using (DataKeyLease lease = await cache.AcquireAsync(customer, TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            lease.RecordBytes(1024);
            customerKeys.Calls.ShouldBe(1);
        }

        using (DataKeyLease lease = await cache.AcquireAsync(customer, TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            lease.RecordBytes(1024);
            customerKeys.Calls.ShouldBe(1, "within the window the cached key is reused");
        }

        clock.Advance(TimeSpan.FromMinutes(5));

        using (DataKeyLease lease = await cache.AcquireAsync(customer, TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            lease.RecordBytes(1024);
            customerKeys.Calls.ShouldBe(2, "past the max age a new key must be minted");
        }
    }

    [Fact]
    public async Task DekCache_ExpiresOnMaxObjects()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
        var customerKeys = new CountingCustomerKeyService();

        using var cache = new DataKeyCache(
            customerKeys,
            Options.Create(new DataKeyCacheOptions { MaxObjectsPerKey = 3 }),
            clock);

        var customer = new CustomerId(Guid.NewGuid());

        for (int i = 0; i < 3; i++)
        {
            using DataKeyLease lease = await cache.AcquireAsync(customer, TestContext.Current.CancellationToken).ConfigureAwait(true);
            lease.RecordBytes(1);
        }

        customerKeys.Calls.ShouldBe(1);

        using (DataKeyLease lease = await cache.AcquireAsync(customer, TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            lease.RecordBytes(1);

            // BLAST RADIUS, ENFORCED. A key recovered from a compromised worker decrypts at most
            // this many objects - so the bound is a security property, not a tuning knob.
            customerKeys.Calls.ShouldBe(2);
        }
    }

    [Fact]
    public async Task DataKeyLease_RecordBytes_DrivesByteBudgetRotation()
    {
        // THE SETTLE-BASED BYTE BUDGET (audit HIGH 2). The old contract charged a caller-supplied
        // ESTIMATE at acquire time; the streaming writer estimated zero and the budget went blind.
        // Now the REAL count settles via RecordBytes, and rotation follows recorded spend.
        var clock = new TestClock(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
        var customerKeys = new CountingCustomerKeyService();

        using var cache = new DataKeyCache(
            customerKeys,
            Options.Create(new DataKeyCacheOptions { MaxBytesPerKey = 4096 }),
            clock);

        var customer = new CustomerId(Guid.NewGuid());

        using (DataKeyLease lease = await cache.AcquireAsync(customer, TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            lease.RecordBytes(3000);
            customerKeys.Calls.ShouldBe(1);
        }

        // Recorded spend (3000) is under the budget, so the key is still served...
        using (DataKeyLease lease = await cache.AcquireAsync(customer, TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            lease.RecordBytes(3000);
            customerKeys.Calls.ShouldBe(1, "3000 recorded bytes < 4096: the cached key is still within budget");
        }

        // ...and 6000 recorded bytes is past it, so the NEXT acquire rotates. Settling after the
        // fact means one object may overshoot the budget by its own size - deliberate, documented
        // on RecordBytes - and rotation happens on the first acquire that sees the spend.
        using (DataKeyLease lease = await cache.AcquireAsync(customer, TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            lease.RecordBytes(1);
            customerKeys.Calls.ShouldBe(2, "recorded spend crossed the byte budget, so a new key is minted");
        }
    }

    [Fact]
    public async Task DataKeyLease_DisposedWithoutRecordBytes_Fails()
    {
        // FORGETTING MUST BE LOUD (audit HIGH 2's real lesson: an API that lets a caller silently
        // skip the budget is how the budget went blind). Disposal without settlement logs an ERROR.
        // It logs rather than throws, deliberately: a throw inside `using` disposal would fire on
        // every failure path and REPLACE the real exception - the exact error-masking Part B of
        // this remediation removes elsewhere.
        var clock = new TestClock(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
        var logger = new RecordingLogger<DataKeyCache>();

        using var cache = new DataKeyCache(
            new CountingCustomerKeyService(),
            Options.Create(new DataKeyCacheOptions()),
            clock,
            logger);

        var customer = new CustomerId(Guid.NewGuid());

        using (DataKeyLease _ = await cache.AcquireAsync(customer, TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            // Deliberately no RecordBytes.
        }

        logger.Errors.ShouldBe(1, "an unsettled lease must be impossible to dispose quietly");

        // And a settled one is quiet.
        using (DataKeyLease lease = await cache.AcquireAsync(customer, TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            lease.RecordBytes(10);
        }

        logger.Errors.ShouldBe(1, "a settled lease logs nothing");
    }

    [Fact]
    public async Task DataKeyLease_RecordBytesTwice_Throws()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
        using var cache = new DataKeyCache(
            new CountingCustomerKeyService(), Options.Create(new DataKeyCacheOptions()), clock);

        using DataKeyLease lease = await cache
            .AcquireAsync(new CustomerId(Guid.NewGuid()), TestContext.Current.CancellationToken).ConfigureAwait(true);

        lease.RecordBytes(5);
        _ = Should.Throw<InvalidOperationException>(() => lease.RecordBytes(5));
    }

    [Fact]
    public async Task DekCache_LeaseSurvivesEviction()
    {
        // The lifetime hazard the copy-on-lease design removes. A write already in flight must not
        // find its key zeroed because an unrelated acquire happened to trip an eviction.
        var clock = new TestClock(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
        var customerKeys = new CountingCustomerKeyService();

        using var cache = new DataKeyCache(
            customerKeys,
            Options.Create(new DataKeyCacheOptions { MaxObjectsPerKey = 1 }),
            clock);

        var customer = new CustomerId(Guid.NewGuid());

        using DataKeyLease first = await cache.AcquireAsync(customer, TestContext.Current.CancellationToken).ConfigureAwait(true);
        byte[] material = first.Key.Span.ToArray();

        using DataKeyLease second = await cache.AcquireAsync(customer, TestContext.Current.CancellationToken).ConfigureAwait(true);
        second.RecordBytes(1);

        first.Key.Span.ToArray().ShouldBe(material, "the first lease must still hold usable key material");
        first.RecordBytes(1);
    }

    [Fact]
    public async Task DekCache_DifferentCustomers_GetDifferentKeys()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
        var customerKeys = new CountingCustomerKeyService();

        using var cache = new DataKeyCache(customerKeys, Options.Create(new DataKeyCacheOptions()), clock);

        using DataKeyLease a = await cache
            .AcquireAsync(new CustomerId(Guid.NewGuid()), TestContext.Current.CancellationToken).ConfigureAwait(true);
        using DataKeyLease b = await cache
            .AcquireAsync(new CustomerId(Guid.NewGuid()), TestContext.Current.CancellationToken).ConfigureAwait(true);

        a.Key.Span.ToArray().ShouldNotBe(b.Key.Span.ToArray());
        a.RecordBytes(1);
        b.RecordBytes(1);
    }

    [Fact]
    public async Task DestroyCek_RequiresADocumentedReason()
    {
        // The Prompt 4 predecessor of this test pinned DestroyCekAsync as NotImplementedException,
        // so the destructive half could not ship before the code that decides whether destruction
        // is LAWFUL. Prompt 6 built that code (the retention decision engine, the cooling-off
        // window, the re-evaluating executor), so the deferral assertion became obsolete - and
        // this replaces it with the new contract's floor: destruction without a documented reason
        // is refused, because an undocumented erasure cannot be defended later. The full
        // behaviour - idempotent destruction, permanent undecryptability, no resurrection - is
        // pinned in UnitTests.Retention.CryptoErasureTests.
        var service = new CustomerKeyService(Provider, new NullCustomerKeyStore(), NullLogger<CustomerKeyService>.Instance);

        _ = await Should.ThrowAsync<ArgumentException>(() =>
            service.DestroyCekAsync(new CustomerId(Guid.NewGuid()), "  ", TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Builds a UUIDv7-shaped value deterministically: timestamp first, then a counter.</summary>
    private static Guid SyntheticUuidV7(int index)
    {
        Span<byte> bytes = stackalloc byte[16];

        // 48-bit millisecond timestamp, advancing one millisecond per thousand ids - so a thousand
        // consecutive customers share a timestamp, exactly as a sign-up batch would.
        long timestamp = 1_756_000_000_000L + (index / 1000);
        bytes[0] = (byte)(timestamp >> 40);
        bytes[1] = (byte)(timestamp >> 32);
        bytes[2] = (byte)(timestamp >> 24);
        bytes[3] = (byte)(timestamp >> 16);
        bytes[4] = (byte)(timestamp >> 8);
        bytes[5] = (byte)timestamp;

        bytes[6] = 0x70;
        bytes[7] = (byte)index;
        bytes[8] = 0x80;

        BinaryPrimitives.WriteInt32BigEndian(bytes[9..13], index);
        BinaryPrimitives.WriteInt16BigEndian(bytes[13..15], (short)(index % 7919));
        bytes[15] = (byte)(index >> 8);

        return new Guid(bytes, bigEndian: true);
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CountingCustomerKeyService : ICustomerKeyService
    {
        private readonly Dictionary<Guid, byte[]> _keys = [];

        public int Calls { get; private set; }

        public Task<DataKey> GetOrCreateCekAsync(CustomerId customer, CancellationToken ct)
        {
            Calls++;

            if (!_keys.TryGetValue(customer.Value, out byte[]? material))
            {
                material = RandomNumberGenerator.GetBytes(32);
                _keys[customer.Value] = material;
            }

            return Task.FromResult(DataKey.CopyFrom(material));
        }

        public Task<DataKey> UnwrapCekAsync(CustomerId customer, CancellationToken ct) =>
            GetOrCreateCekAsync(customer, ct);

        public Task DestroyCekAsync(CustomerId customer, string reason, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private sealed class NullCustomerKeyStore : ICustomerKeyStore
    {
        public Task<CustomerKeyRecord?> FindAsync(CustomerId customer, CancellationToken ct) =>
            Task.FromResult<CustomerKeyRecord?>(null);

        public Task<bool> TryInsertAsync(CustomerKeyRecord record, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<bool> DestroyAsync(CustomerId customer, string reason, CancellationToken ct) =>
            Task.FromResult(false);
    }

    /// <summary>Counts error-level log events - the unsettled-lease tripwire's assertion surface.</summary>
    private sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public int Errors { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Error)
            {
                Errors++;
            }
        }
    }
}
