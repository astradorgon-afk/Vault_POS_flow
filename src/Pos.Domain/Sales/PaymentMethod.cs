namespace Pos.Domain.Sales;

/// <summary>The way a customer settled a sale.</summary>
public enum PaymentMethod
{
    /// <summary>Physical cash tendered at the drawer.</summary>
    Cash = 1,

    /// <summary>Debit or credit card, settled through a payment provider.</summary>
    Card = 2,

    /// <summary>Digital wallet, settled through a payment provider.</summary>
    EWallet = 3,
}