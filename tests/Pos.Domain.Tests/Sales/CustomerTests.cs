using FluentAssertions;
using Pos.Domain.Common;
using Pos.Domain.Sales;

namespace Pos.Domain.Tests.Sales;

/// <summary>Customer account validation and lifecycle rules.</summary>
public sealed class CustomerTests
{
    private static readonly UserId Actor = UserId.New();
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_ValidDetails_NormalizesOptionalFields()
    {
        Result<Customer> result = Customer.Create(
            CustomerId.New(), "  Ada Lovelace  ", " 09171234567 ", " ada@example.test ",
            " 123-456-789 ", " Preferred contact ", Actor, Now);

        result.IsSuccess.Should().BeTrue();
        result.Value.DisplayName.Should().Be("Ada Lovelace");
        result.Value.Phone.Should().Be("09171234567");
        result.Value.Email.Should().Be("ada@example.test");
        result.Value.Tin.Should().Be("123-456-789");
        result.Value.Note.Should().Be("Preferred contact");
        result.Value.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Create_InvalidEmail_IsRefused()
    {
        Result<Customer> result = Customer.Create(
            CustomerId.New(), "Customer", null, "not-an-email", null, null, Actor, Now);

        result.Error.Should().Be(CustomerErrors.EmailInvalid);
    }

    [Fact]
    public void Create_MissingName_IsRefused()
    {
        Result<Customer> result = Customer.Create(
            CustomerId.New(), " ", null, null, null, null, Actor, Now);

        result.Error.Code.Should().Be(CustomerErrors.DisplayNameInvalid(Customer.DisplayNameMaxLength).Code);
    }

    [Fact]
    public void Deactivate_ThenReactivate_PreservesAuditFields()
    {
        Customer customer = NewCustomer();
        UserId manager = UserId.New();

        customer.Deactivate("Duplicate account", manager, Now.AddMinutes(1)).IsSuccess.Should().BeTrue();
        customer.IsActive.Should().BeFalse();
        customer.DeactivationReason.Should().Be("Duplicate account");
        customer.UpdatedByUserId.Should().Be(manager);

        customer.Reactivate(Actor, Now.AddMinutes(2)).IsSuccess.Should().BeTrue();
        customer.IsActive.Should().BeTrue();
        customer.DeactivatedAtUtc.Should().BeNull();
        customer.DeactivationReason.Should().BeNull();
    }

    [Fact]
    public void Update_InactiveCustomer_IsRefusedWithoutChangingDetails()
    {
        Customer customer = NewCustomer();
        customer.Deactivate("Closed account", Actor, Now.AddMinutes(1));

        Result result = customer.Update("Changed", null, null, null, null, Actor, Now.AddMinutes(2));

        result.Error.Should().Be(CustomerErrors.Inactive);
        customer.DisplayName.Should().Be("Customer One");
    }

    private static Customer NewCustomer()
        => Customer.Create(CustomerId.New(), "Customer One", null, null, null, null, Actor, Now).Value;
}
