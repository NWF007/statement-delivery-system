using NSubstitute;
using Shouldly;
using StatementDelivery.Domain.Abstractions;
using StatementDelivery.Persistence.Ids;
using Xunit;

namespace UnitTests.Persistence;

/// <summary>
/// Demonstrates the reason <see cref="IIdGenerator"/> exists as an abstraction at all.
/// </summary>
/// <remarks>
/// Production code never calls <c>Guid.CreateVersion7()</c> directly. Going through the interface
/// is what lets a test pin the identifiers a unit under test will produce, so an assertion about
/// what was written can name the exact value instead of matching a shape. Without that, tests of
/// anything that generates an identity end up asserting "some Guid was produced", which passes for
/// the wrong reasons.
/// </remarks>
public sealed class DeterministicIdGeneratorTests
{
    [Fact]
    public void ASubstitutedGenerator_ReturnsAKnownSequence()
    {
        Guid first = Guid.Parse("01a03ee4-d9c8-7534-800d-b5b2c5ce17e8");
        Guid second = Guid.Parse("01a03ee4-d9c8-7535-800d-b5b2c5ce17e9");

        IIdGenerator generator = Substitute.For<IIdGenerator>();
        generator.NewId().Returns(first, second);

        generator.NewId().ShouldBe(first);
        generator.NewId().ShouldBe(second);
    }

    [Fact]
    public void TheProductionGenerator_IsRegisteredBehindTheInterface()
    {
        // Guards the shape of the abstraction: a caller that depends on UuidV7Generator directly
        // cannot be given a deterministic sequence, which defeats the point.
        typeof(IIdGenerator).IsAssignableFrom(typeof(UuidV7Generator)).ShouldBeTrue();
        typeof(IIdGenerator).GetMethods().Length.ShouldBe(1, "a one-method abstraction is trivial to substitute");
    }
}
