using Pos.Domain.Common;

namespace Pos.Domain.Sales;

/// <summary>
/// A customer record. Customers are referenced by sales and returns to
/// associate transactions with named accounts.
/// </summary>
public sealed class Customer : AggregateRoot<CustomerId>
{
    /// <summary>Maximum length of the display name.</summary>
    public const int DisplayNameMaxLength = 128;

    /// <summary>Maximum length of the phone number.</summary>
    public const int PhoneMaxLength = 32;

    /// <summary>Maximum length of the email address.</summary>
    public const int EmailMaxLength = 160;

    /// <summary>Maximum length of the tax identification number.</summary>
    public const int TinMaxLength = 64;

    /// <summary>Maximum length of the note.</summary>
    public const int NoteMaxLength = 512;

    /// <summary>Maximum length of the deactivation reason.</summary>
    public const int DeactivationReasonMaxLength = 200;

    private Customer()
    {
        DisplayName = string.Empty;
        CreatedByUserId = UserId.Empty;
        UpdatedByUserId = UserId.Empty;
    }

    private Customer(
        CustomerId id,
        string displayName,
        string? phone,
        string? email,
        string? tin,
        string? note,
        UserId createdBy,
        DateTimeOffset now)
    {
        Id = id;
        DisplayName = displayName;
        Phone = phone;
        Email = email;
        Tin = tin;
        Note = note;
        IsActive = true;
        CreatedAtUtc = now;
        CreatedByUserId = createdBy;
        UpdatedAtUtc = now;
        UpdatedByUserId = createdBy;
    }

    /// <summary>Gets the customer's display name.</summary>
    public string DisplayName { get; private set; }

    /// <summary>Gets the phone number, when provided.</summary>
    public string? Phone { get; private set; }

    /// <summary>Gets the email address, when provided.</summary>
    public string? Email { get; private set; }

    /// <summary>Gets the tax identification number (Philippine TIN), when provided.</summary>
    public string? Tin { get; private set; }

    /// <summary>Gets the free-text note, when provided.</summary>
    public string? Note { get; private set; }

    /// <summary>Gets whether the customer is active.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Gets when the customer was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>Gets the user who created the customer.</summary>
    public UserId CreatedByUserId { get; private set; }

    /// <summary>Gets when the customer was last updated.</summary>
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    /// <summary>Gets the user who last updated the customer.</summary>
    public UserId UpdatedByUserId { get; private set; }

    /// <summary>Gets when the customer was deactivated, when applicable.</summary>
    public DateTimeOffset? DeactivatedAtUtc { get; private set; }

    /// <summary>Gets the deactivation reason, when applicable.</summary>
    public string? DeactivationReason { get; private set; }

    /// <summary>
    /// Creates a new customer.
    /// </summary>
    /// <param name="id">The customer identifier.</param>
    /// <param name="displayName">The display name (required, 1-128 characters).</param>
    /// <param name="phone">The phone number (optional, max 32 characters).</param>
    /// <param name="email">The email address (optional, max 160 characters).</param>
    /// <param name="tin">The tax identification number (optional, max 64 characters).</param>
    /// <param name="note">A free-text note (optional, max 512 characters).</param>
    /// <param name="createdBy">The user creating the customer.</param>
    /// <param name="now">The creation timestamp.</param>
    /// <returns>The new customer, or a validation failure.</returns>
    public static Result<Customer> Create(
        CustomerId id,
        string displayName,
        string? phone,
        string? email,
        string? tin,
        string? note,
        UserId createdBy,
        DateTimeOffset now)
    {
        if (id.IsEmpty)
        {
            return Result<Customer>.Failure(CustomerErrors.IdRequired);
        }

        string name = displayName?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > DisplayNameMaxLength)
        {
            return Result<Customer>.Failure(CustomerErrors.DisplayNameInvalid(DisplayNameMaxLength));
        }

        string? p = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        if (p is { Length: > PhoneMaxLength })
        {
            return Result<Customer>.Failure(CustomerErrors.PhoneTooLong(PhoneMaxLength));
        }

        string? e = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        if (e is { Length: > EmailMaxLength })
        {
            return Result<Customer>.Failure(CustomerErrors.EmailTooLong(EmailMaxLength));
        }

        if (e is not null)
        {
            try
            {
                var _ = new System.Net.Mail.MailAddress(e);
            }
            catch (FormatException)
            {
                return Result<Customer>.Failure(CustomerErrors.EmailInvalid);
            }
            catch (ArgumentException)
            {
                return Result<Customer>.Failure(CustomerErrors.EmailInvalid);
            }
        }

        string? t = string.IsNullOrWhiteSpace(tin) ? null : tin.Trim();
        if (t is { Length: > TinMaxLength })
        {
            return Result<Customer>.Failure(CustomerErrors.TinTooLong(TinMaxLength));
        }

        string? n = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (n is { Length: > NoteMaxLength })
        {
            return Result<Customer>.Failure(CustomerErrors.NoteTooLong(NoteMaxLength));
        }

        if (createdBy.IsEmpty)
        {
            return Result<Customer>.Failure(CustomerErrors.CreatedByRequired);
        }

        if (now == default)
        {
            return Result<Customer>.Failure(CustomerErrors.CreatedAtRequired);
        }

        return Result<Customer>.Success(new Customer(id, name, p, e, t, n, createdBy, now));
    }

    /// <summary>
    /// Updates the customer's details.
    /// </summary>
    /// <param name="displayName">The display name (required, 1-128 characters).</param>
    /// <param name="phone">The phone number (optional, max 32 characters).</param>
    /// <param name="email">The email address (optional, max 160 characters).</param>
    /// <param name="tin">The tax identification number (optional, max 64 characters).</param>
    /// <param name="note">A free-text note (optional, max 512 characters).</param>
    /// <param name="updatedBy">The user making the update.</param>
    /// <param name="now">The update timestamp.</param>
    /// <returns>A success, or a validation failure.</returns>
    public Result Update(
        string displayName,
        string? phone,
        string? email,
        string? tin,
        string? note,
        UserId updatedBy,
        DateTimeOffset now)
    {
        if (!IsActive)
        {
            return Result.Failure(CustomerErrors.Inactive);
        }

        string name = displayName?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > DisplayNameMaxLength)
        {
            return Result.Failure(CustomerErrors.DisplayNameInvalid(DisplayNameMaxLength));
        }

        string? p = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        if (p is { Length: > PhoneMaxLength })
        {
            return Result.Failure(CustomerErrors.PhoneTooLong(PhoneMaxLength));
        }

        string? e = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        if (e is { Length: > EmailMaxLength })
        {
            return Result.Failure(CustomerErrors.EmailTooLong(EmailMaxLength));
        }

        if (e is not null)
        {
            try
            {
                var _ = new System.Net.Mail.MailAddress(e);
            }
            catch (FormatException)
            {
                return Result.Failure(CustomerErrors.EmailInvalid);
            }
            catch (ArgumentException)
            {
                return Result.Failure(CustomerErrors.EmailInvalid);
            }
        }

        string? t = string.IsNullOrWhiteSpace(tin) ? null : tin.Trim();
        if (t is { Length: > TinMaxLength })
        {
            return Result.Failure(CustomerErrors.TinTooLong(TinMaxLength));
        }

        string? n = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (n is { Length: > NoteMaxLength })
        {
            return Result.Failure(CustomerErrors.NoteTooLong(NoteMaxLength));
        }

        if (updatedBy.IsEmpty)
        {
            return Result.Failure(CustomerErrors.UpdatedByRequired);
        }

        if (now == default)
        {
            return Result.Failure(CustomerErrors.UpdatedAtRequired);
        }

        DisplayName = name;
        Phone = p;
        Email = e;
        Tin = t;
        Note = n;
        UpdatedAtUtc = now;
        UpdatedByUserId = updatedBy;

        return Result.Success();
    }

    /// <summary>
    /// Deactivates the customer.
    /// </summary>
    /// <param name="reason">The deactivation reason (required, 1-200 characters).</param>
    /// <param name="deactivatedBy">The user deactivating the customer.</param>
    /// <param name="now">The deactivation timestamp.</param>
    /// <returns>A success, or a validation failure.</returns>
    public Result Deactivate(string reason, UserId deactivatedBy, DateTimeOffset now)
    {
        if (!IsActive)
        {
            return Result.Failure(CustomerErrors.AlreadyInactive);
        }

        string trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > DeactivationReasonMaxLength)
        {
            return Result.Failure(CustomerErrors.DeactivationReasonInvalid(DeactivationReasonMaxLength));
        }

        if (deactivatedBy.IsEmpty)
        {
            return Result.Failure(CustomerErrors.DeactivatedByRequired);
        }

        if (now == default)
        {
            return Result.Failure(CustomerErrors.DeactivatedAtRequired);
        }

        IsActive = false;
        DeactivatedAtUtc = now;
        DeactivationReason = trimmed;
        UpdatedAtUtc = now;
        UpdatedByUserId = deactivatedBy;

        return Result.Success();
    }

    /// <summary>
    /// Reactivates a deactivated customer.
    /// </summary>
    /// <param name="reactivatedBy">The user reactivating the customer.</param>
    /// <param name="now">The reactivation timestamp.</param>
    /// <returns>A success, or a validation failure.</returns>
    public Result Reactivate(UserId reactivatedBy, DateTimeOffset now)
    {
        if (IsActive)
        {
            return Result.Failure(CustomerErrors.AlreadyActive);
        }

        if (reactivatedBy.IsEmpty)
        {
            return Result.Failure(CustomerErrors.ReactivatedByRequired);
        }

        if (now == default)
        {
            return Result.Failure(CustomerErrors.ReactivatedAtRequired);
        }

        IsActive = true;
        DeactivatedAtUtc = null;
        DeactivationReason = null;
        UpdatedAtUtc = now;
        UpdatedByUserId = reactivatedBy;

        return Result.Success();
    }
}

/// <summary>Stable failures for customer operations.</summary>
public static class CustomerErrors
{
    /// <summary>The customer identifier is empty.</summary>
    public static readonly Error IdRequired = Error.Validation(
        "customer.id_required",
        "A customer identifier is required.");

    /// <summary>The display name is missing or too long.</summary>
    /// <param name="maxLength">The maximum length.</param>
    /// <returns>The error.</returns>
    public static Error DisplayNameInvalid(int maxLength) => Error.Validation(
        "customer.display_name_invalid",
        FormattableString.Invariant($"The display name is required and may be at most {maxLength} characters."));

    /// <summary>The phone number is too long.</summary>
    /// <param name="maxLength">The maximum length.</param>
    /// <returns>The error.</returns>
    public static Error PhoneTooLong(int maxLength) => Error.Validation(
        "customer.phone_too_long",
        FormattableString.Invariant($"The phone number may be at most {maxLength} characters."));

    /// <summary>The email address is too long.</summary>
    /// <param name="maxLength">The maximum length.</param>
    /// <returns>The error.</returns>
    public static Error EmailTooLong(int maxLength) => Error.Validation(
        "customer.email_too_long",
        FormattableString.Invariant($"The email address may be at most {maxLength} characters."));

    /// <summary>The email address is not a valid format.</summary>
    public static readonly Error EmailInvalid = Error.Validation(
        "customer.email_invalid",
        "The email address is not a valid format.");

    /// <summary>The tax identification number is too long.</summary>
    /// <param name="maxLength">The maximum length.</param>
    /// <returns>The error.</returns>
    public static Error TinTooLong(int maxLength) => Error.Validation(
        "customer.tin_too_long",
        FormattableString.Invariant($"The tax identification number may be at most {maxLength} characters."));

    /// <summary>The note is too long.</summary>
    /// <param name="maxLength">The maximum length.</param>
    /// <returns>The error.</returns>
    public static Error NoteTooLong(int maxLength) => Error.Validation(
        "customer.note_too_long",
        FormattableString.Invariant($"The note may be at most {maxLength} characters."));

    /// <summary>The creating user is required.</summary>
    public static readonly Error CreatedByRequired = Error.Validation(
        "customer.created_by_required",
        "A creating user is required.");

    /// <summary>The creation timestamp is required.</summary>
    public static readonly Error CreatedAtRequired = Error.Validation(
        "customer.created_at_required",
        "A creation timestamp is required.");

    /// <summary>The updating user is required.</summary>
    public static readonly Error UpdatedByRequired = Error.Validation(
        "customer.updated_by_required",
        "An updating user is required.");

    /// <summary>The update timestamp is required.</summary>
    public static readonly Error UpdatedAtRequired = Error.Validation(
        "customer.updated_at_required",
        "An update timestamp is required.");

    /// <summary>The customer is not active.</summary>
    public static readonly Error Inactive = Error.Conflict(
        "customer.inactive",
        "The customer is not active.");

    /// <summary>The customer is already inactive.</summary>
    public static readonly Error AlreadyInactive = Error.Conflict(
        "customer.already_inactive",
        "The customer is already inactive.");

    /// <summary>The customer is already active.</summary>
    public static readonly Error AlreadyActive = Error.Conflict(
        "customer.already_active",
        "The customer is already active.");

    /// <summary>The deactivation reason is missing or too long.</summary>
    /// <param name="maxLength">The maximum length.</param>
    /// <returns>The error.</returns>
    public static Error DeactivationReasonInvalid(int maxLength) => Error.Validation(
        "customer.deactivation_reason_invalid",
        FormattableString.Invariant($"A deactivation reason is required and may be at most {maxLength} characters."));

    /// <summary>The deactivating user is required.</summary>
    public static readonly Error DeactivatedByRequired = Error.Validation(
        "customer.deactivated_by_required",
        "A deactivating user is required.");

    /// <summary>The deactivation timestamp is required.</summary>
    public static readonly Error DeactivatedAtRequired = Error.Validation(
        "customer.deactivated_at_required",
        "A deactivation timestamp is required.");

    /// <summary>The reactivating user is required.</summary>
    public static readonly Error ReactivatedByRequired = Error.Validation(
        "customer.reactivated_by_required",
        "A reactivating user is required.");

    /// <summary>The reactivation timestamp is required.</summary>
    public static readonly Error ReactivatedAtRequired = Error.Validation(
        "customer.reactivated_at_required",
        "A reactivation timestamp is required.");

    /// <summary>The customer does not exist.</summary>
    /// <param name="customerId">The unknown customer.</param>
    /// <returns>The error.</returns>
    public static Error Unknown(CustomerId customerId) => Error.NotFound(
        "customer.unknown",
        FormattableString.Invariant($"Unknown customer {customerId.Value}."));
}