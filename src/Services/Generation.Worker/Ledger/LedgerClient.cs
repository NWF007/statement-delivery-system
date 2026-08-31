using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StatementDelivery.Domain.ValueObjects;

namespace Generation.Worker.Ledger;

/// <summary>One ledger transaction, as this worker consumes it.</summary>
/// <remarks>
/// A DELIBERATE COPY of the mock's wire shape rather than a shared assembly: the ledger is an
/// EXTERNAL system boundary, and sharing a contract type with the mock would quietly turn the
/// boundary test into a self-agreement test. Services must not reference each other - an
/// architecture test enforces it.
/// </remarks>
/// <param name="PostedOn">Posting date.</param>
/// <param name="Description">Merchant or transaction descriptor.</param>
/// <param name="AmountMinorUnits">Signed minor units. A long - never a float, never a decimal.</param>
public sealed record LedgerTransactionDto(DateOnly PostedOn, string Description, long AmountMinorUnits);

/// <summary>The ledger's transactions response.</summary>
/// <param name="AccountId">The account.</param>
/// <param name="OpeningBalanceMinorUnits">Balance at period start.</param>
/// <param name="ClosingBalanceMinorUnits">Balance at period end.</param>
/// <param name="Transactions">The period's transactions.</param>
public sealed record LedgerStatementDto(
    Guid AccountId,
    long OpeningBalanceMinorUnits,
    long ClosingBalanceMinorUnits,
    IReadOnlyList<LedgerTransactionDto> Transactions);

/// <summary>The ledger returned 200 with a payload that does not parse. The poison-item signal.</summary>
/// <remarks>
/// A DISTINCT TYPE, not a reused JsonException, because the render pipeline treats it
/// differently: an HTTP failure might be the ledger having a bad minute and is worth the retry
/// budget; a malformed 200 is the same bytes on every attempt, so each retry burns an attempt
/// toward quarantine doing predictably useless work. The type name lands in last_error, where an
/// operator reads it as "fix the data, then retry", not "wait it out".
/// </remarks>
public sealed class PoisonLedgerPayloadException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="PoisonLedgerPayloadException"/> class.</summary>
    /// <param name="message">What failed to parse.</param>
    /// <param name="inner">The parse failure.</param>
    public PoisonLedgerPayloadException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="PoisonLedgerPayloadException"/> class.</summary>
    public PoisonLedgerPayloadException()
    {
    }

    /// <summary>Initialises a new instance of the <see cref="PoisonLedgerPayloadException"/> class.</summary>
    /// <param name="message">What failed to parse.</param>
    public PoisonLedgerPayloadException(string message)
        : base(message)
    {
    }
}

/// <summary>The ledger does not know this account.</summary>
public sealed class LedgerUnknownAccountException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="LedgerUnknownAccountException"/> class.</summary>
    /// <param name="message">Which account.</param>
    public LedgerUnknownAccountException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="LedgerUnknownAccountException"/> class.</summary>
    public LedgerUnknownAccountException()
    {
    }

    /// <summary>Initialises a new instance of the <see cref="LedgerUnknownAccountException"/> class.</summary>
    /// <param name="message">Which account.</param>
    /// <param name="inner">Underlying failure.</param>
    public LedgerUnknownAccountException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Fetches transaction data from the core-banking ledger.</summary>
public interface ILedgerClient
{
    /// <summary>Fetches one account's transactions for one period.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="period">The statement period.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The ledger data.</returns>
    /// <exception cref="LedgerUnknownAccountException">The ledger has no such account.</exception>
    /// <exception cref="PoisonLedgerPayloadException">The response did not parse.</exception>
    Task<LedgerStatementDto> GetTransactionsAsync(
        Guid accountId, StatementPeriod period, CancellationToken cancellationToken);
}

/// <summary>HTTP implementation of <see cref="ILedgerClient"/>.</summary>
/// <remarks>
/// The resilience pipeline - timeout, retry with jitter, circuit breaker, client-side rate limit
/// - is attached to the underlying <see cref="HttpClient"/> in <c>LedgerClientExtensions</c>.
/// This class is deliberately dumb transport-plus-translation; policy lives in one place.
/// </remarks>
public sealed class LedgerClient : ILedgerClient
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    /// <summary>Initialises a new instance of the <see cref="LedgerClient"/> class.</summary>
    /// <param name="http">The resilient HTTP client.</param>
    public LedgerClient(HttpClient http) => _http = http;

    /// <inheritdoc />
    public async Task<LedgerStatementDto> GetTransactionsAsync(
        Guid accountId, StatementPeriod period, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);

        var uri = new Uri(
            string.Create(
                CultureInfo.InvariantCulture,
                $"ledger/v1/accounts/{accountId:D}/transactions?from={period.Start:yyyy-MM-dd}&to={period.End:yyyy-MM-dd}"),
            UriKind.Relative);

        using HttpResponseMessage response =
            await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new LedgerUnknownAccountException(
                string.Create(CultureInfo.InvariantCulture, $"Ledger has no account {accountId:D}."));
        }

        _ = response.EnsureSuccessStatusCode();

        try
        {
            LedgerStatementDto? dto = await response.Content
                .ReadFromJsonAsync<LedgerStatementDto>(Web, cancellationToken)
                .ConfigureAwait(false);

            return dto ?? throw new PoisonLedgerPayloadException("Ledger returned a null body.");
        }
        catch (JsonException ex)
        {
            throw new PoisonLedgerPayloadException(
                string.Create(CultureInfo.InvariantCulture, $"Ledger payload for {accountId:D} did not parse."),
                ex);
        }
    }
}
