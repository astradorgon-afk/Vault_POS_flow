using System.Globalization;

namespace Pos.Infrastructure.Identity;

/// <summary>
/// The development demo business: a neighbourhood grocery chain with a
/// distribution centre and three branches, stocking the kind of range a real
/// Philippine grocery carries. Brands are fictional; sizes, prices and margins
/// follow the local market, and every barcode is a valid EAN-13 under the
/// Philippine GS1 prefix.
/// </summary>
/// <remarks>
/// Everything here is deterministic, so every developer's database holds the
/// same catalogue and the same numbers. <see cref="DevelopmentProduct.DailyUnits"/>
/// is the product's expected daily sale at the flagship branch; the seeder
/// sizes opening stock and shelf targets from it, and the demo trading tool
/// sells in proportion to those targets, so stock and sales agree.
/// </remarks>
internal static class DevelopmentCatalogue
{
    /// <summary>The trading name the branches print on receipts.</summary>
    public const string TradingName = "Suki Mart";

    /// <summary>The business's VAT registration, printed on every receipt.</summary>
    public const string VatRegistration = "VAT REG TIN 009-482-715-000";

    public static IReadOnlyList<DevelopmentLocation> Locations { get; } =
    [
        new("MAIN", "Valenzuela Distribution Center", 0m, "000", "Gen. T. de Leon, Valenzuela City"),
        new("STORE01", "Legazpi Village, Makati", 1.0m, "001", "Rufino St., Legazpi Village, Makati City"),
        new("STORE02", "Tomas Morato, Quezon City", 0.8m, "002", "Tomas Morato Ave., Quezon City"),
        new("STORE03", "Kapitolyo, Pasig", 0.6m, "003", "United St., Kapitolyo, Pasig City"),
    ];

    public static IReadOnlyList<DevelopmentCategory> Categories { get; } =
    [
        new("RICE", "Rice & Grains", 10, "RG", 0.09m),
        new("CANNED", "Canned & Packaged Goods", 20, "CG", 0.16m),
        new("NOODLES", "Noodles & Pasta", 30, "NP", 0.15m),
        new("CONDIMENTS", "Condiments & Cooking", 40, "CC", 0.17m),
        new("BREAKFAST", "Breakfast & Spreads", 50, "BS", 0.18m),
        new("SNACKS", "Snacks & Chips", 60, "SN", 0.24m),
        new("BISCUITS", "Biscuits & Sweets", 70, "BK", 0.22m),
        new("BEVERAGES", "Beverages", 80, "BV", 0.20m),
        new("COFFEE", "Coffee & Tea", 90, "CT", 0.18m),
        new("DAIRY", "Milk & Dairy", 100, "DY", 0.15m),
        new("FROZEN", "Frozen & Chilled", 110, "FZ", 0.20m),
        new("BAKERY", "Bakery", 120, "BR", 0.25m),
        new("HOUSEHOLD", "Household & Cleaning", 130, "HH", 0.21m),
        new("PERSONAL", "Personal Care", 140, "PC", 0.23m),
        new("BABY", "Baby Care", 150, "BB", 0.17m),
        new("HEALTH", "Health & Wellness", 160, "HW", 0.26m),
        new("PET", "Pet Care", 170, "PT", 0.20m),
        new("SCHOOL", "School & Office", 180, "SO", 0.30m),
    ];

    public static IReadOnlyList<DevelopmentSupplier> Suppliers { get; } =
    [
        new("PGC", "Pacific Grains Corporation", "004-218-993-000", 30, 5),
        new("MLD", "Metro Luzon Distributors, Inc.", "006-771-204-000", 30, 4),
        new("SNX", "Snackworks Trading Corp.", "008-340-117-000", 30, 5),
        new("IBC", "Island Beverage Company", "005-902-338-000", 15, 3),
        new("HCT", "Highland Coffee Traders", "007-156-842-000", 30, 7),
        new("DFP", "DairyFresh Philippines, Inc.", "003-647-590-000", 15, 2),
        new("CCF", "Cold Chain Foods Corp.", "009-013-276-000", 15, 3),
        new("PLB", "Panaderia Luna Bakeshop", "010-455-601-000", 7, 1),
        new("HCS", "HomeCare Supply Co.", "004-889-163-000", 45, 7),
        new("PCD", "PureCare Distribution, Inc.", "006-324-078-000", 45, 7),
        new("MSD", "MediSigla Distributors", "008-712-935-000", 30, 5),
        new("OSP", "Office & School Plus Trading", "011-263-480-000", 30, 7),
    ];

    // Tiers: how fast the line's first variant sells at the flagship, in units a day.
    private const decimal A = 8m; // staples every other basket holds
    private const decimal B = 3.5m;
    private const decimal C = 1.5m;
    private const decimal D = 0.5m;

    private static (string Size, decimal Price)[] S(params (string, decimal)[] sizes) => sizes;

    /// <summary>
    /// Product lines: category, brand, supplier, name, velocity tier, sizes with
    /// their shelf price, and the variants (flavours, scents, types) each size
    /// comes in. A line yields one product per variant and size.
    /// </summary>
    private static readonly ProductLine[] Lines =
    [
        // Rice & grains
        new("RICE", "Golden Grain", "PGC", "Premium Jasmine Rice", A, S(("5kg", 345m), ("10kg", 680m), ("25kg", 1650m)), true),
        new("RICE", "Golden Grain", "PGC", "Dinorado Rice", B, S(("5kg", 395m), ("10kg", 780m)), true),
        new("RICE", "Harvest Queen", "PGC", "Well-Milled Rice", A, S(("2kg", 118m), ("5kg", 285m), ("10kg", 560m), ("25kg", 1375m)), true),
        new("RICE", "Harvest Queen", "PGC", "Brown Rice", D, S(("2kg", 185m)), true),
        new("RICE", "Sakahan", "PGC", "Glutinous Rice", D, S(("1kg", 95m), ("2kg", 185m)), true),
        new("RICE", "Sakahan", "PGC", "Mung Beans", C, S(("250g", 42m), ("500g", 78m)), true),
        new("RICE", "Sakahan", "PGC", "Red Beans", D, S(("500g", 82m)), true),
        new("RICE", "Morning Fields", "PGC", "Rolled Oats", C, S(("400g", 89m), ("800g", 159m))),
        new("RICE", "Morning Fields", "PGC", "Quick-Cooking Oats", C, S(("400g", 85m), ("800g", 152m))),
        new("RICE", "Tamis", "PGC", "Refined Sugar", A, S(("500g", 42m), ("1kg", 82m), ("2kg", 158m))),
        new("RICE", "Tamis", "PGC", "Washed Sugar", B, S(("1kg", 76m), ("2kg", 146m))),
        new("RICE", "Tamis", "PGC", "Brown Sugar", B, S(("500g", 38m), ("1kg", 72m))),
        new("RICE", "Harina", "PGC", "All-Purpose Flour", C, S(("400g", 36m), ("1kg", 78m))),
        new("RICE", "Harvest Queen", "PGC", "Sinandomeng Rice", A, S(("5kg", 305m), ("10kg", 598m), ("25kg", 1460m)), true),
        new("RICE", "Sakahan", "PGC", "Black Rice", D, S(("1kg", 128m)), true),
        new("RICE", "Harina", "PGC", "Cake Flour", D, S(("1kg", 92m))),
        new("RICE", "Harina", "PGC", "Bread Flour", D, S(("1kg", 84m))),
        new("RICE", "Harina", "PGC", "Baking Powder", D, S(("50g", 28m), ("100g", 48m))),
        new("RICE", "Harina", "PGC", "Instant Dry Yeast", D, S(("11g", 18m))),

        // Canned & packaged goods
        new("CANNED", "Marina Blue", "MLD", "Tuna Flakes in Oil", A, S(("155g", 36m), ("180g", 42m)), false, "Regular", "Hot & Spicy", "Afritada", "Caldereta"),
        new("CANNED", "Marina Blue", "MLD", "Tuna Chunks in Brine", C, S(("180g", 58m))),
        new("CANNED", "Ligaya", "MLD", "Sardines in Tomato Sauce", A, S(("155g", 26m), ("425g", 62m)), false, "Regular", "Chili"),
        new("CANNED", "Ligaya", "MLD", "Spanish-Style Sardines", D, S(("225g", 89m)), false, "Hot", "Mild"),
        new("CANNED", "Ligaya", "MLD", "Mackerel in Tomato Sauce", B, S(("155g", 32m), ("425g", 72m))),
        new("CANNED", "Rancho Fino", "MLD", "Corned Beef", A, S(("150g", 48m), ("175g", 56m), ("260g", 84m))),
        new("CANNED", "Rancho Fino", "MLD", "Premium Corned Beef", C, S(("150g", 72m), ("260g", 118m))),
        new("CANNED", "Rancho Fino", "MLD", "Meat Loaf", B, S(("150g", 34m), ("250g", 52m))),
        new("CANNED", "Rancho Fino", "MLD", "Luncheon Meat", B, S(("165g", 48m), ("340g", 118m))),
        new("CANNED", "Rancho Fino", "MLD", "Vienna Sausage", B, S(("130g", 34m))),
        new("CANNED", "Rancho Fino", "MLD", "Pork & Beans", C, S(("230g", 34m), ("390g", 52m))),
        new("CANNED", "Rancho Fino", "MLD", "Liver Spread", B, S(("85g", 28m))),
        new("CANNED", "Del Sol", "MLD", "Fruit Cocktail", C, S(("432g", 78m), ("836g", 142m))),
        new("CANNED", "Del Sol", "MLD", "Pineapple Chunks", D, S(("567g", 76m))),
        new("CANNED", "Del Sol", "MLD", "Whole Kernel Corn", D, S(("425g", 56m))),
        new("CANNED", "Del Sol", "MLD", "Mushroom Pieces & Stems", D, S(("198g", 42m), ("400g", 76m))),
        new("CANNED", "Del Sol", "MLD", "Cream-Style Corn", D, S(("425g", 58m))),
        new("CANNED", "Marina Blue", "MLD", "Bangus Milkfish in Oil", C, S(("184g", 78m)), false, "Spanish Style", "Hot", "Tausi"),
        new("CANNED", "Ligaya", "MLD", "Sardines in Oil", B, S(("155g", 28m))),
        new("CANNED", "Rancho Fino", "MLD", "Beef Loaf", C, S(("150g", 36m))),
        new("CANNED", "Rancho Fino", "MLD", "Chicken Vienna Sausage", C, S(("130g", 32m))),
        new("CANNED", "Del Sol", "MLD", "Peach Halves", D, S(("432g", 88m))),
        new("CANNED", "Del Sol", "MLD", "Mixed Vegetables", D, S(("425g", 62m))),
        new("CANNED", "Del Sol", "MLD", "Green Peas", D, S(("400g", 48m))),
        new("CANNED", "Del Sol", "MLD", "Red Kidney Beans", D, S(("400g", 58m))),
        new("CANNED", "Del Sol", "MLD", "Garbanzos", D, S(("400g", 62m))),

        // Noodles & pasta
        new("NOODLES", "Masarap", "MLD", "Instant Noodles", A, S(("55g", 15m), ("70g", 19m)), false, "Chicken", "Beef", "Calamansi", "Chili-Mansi", "Bulalo"),
        new("NOODLES", "Masarap", "MLD", "Instant Pancit Canton", A, S(("60g", 18m), ("80g", 24m)), false, "Original", "Chilimansi", "Sweet & Spicy", "Extra Hot"),
        new("NOODLES", "Masarap", "MLD", "Cup Noodles", B, S(("60g", 32m)), false, "Seafood", "Beef", "Spicy Bulalo"),
        new("NOODLES", "Pancit Express", "MLD", "Bihon Rice Noodles", C, S(("227g", 38m), ("500g", 78m))),
        new("NOODLES", "Pancit Express", "MLD", "Canton Egg Noodles", C, S(("227g", 42m), ("500g", 86m))),
        new("NOODLES", "Pancit Express", "MLD", "Sotanghon Glass Noodles", D, S(("100g", 26m), ("250g", 58m))),
        new("NOODLES", "Buena Pasta", "MLD", "Spaghetti", B, S(("400g", 48m), ("900g", 98m), ("1kg", 108m))),
        new("NOODLES", "Buena Pasta", "MLD", "Elbow Macaroni", C, S(("400g", 46m), ("1kg", 104m))),
        new("NOODLES", "Buena Pasta", "MLD", "Salad Macaroni", D, S(("400g", 48m))),
        new("NOODLES", "Masarap", "MLD", "Instant Mami", B, S(("55g", 16m)), false, "Beef", "Chicken", "Pork"),
        new("NOODLES", "Masarap", "MLD", "Instant Ramen", C, S(("120g", 58m)), false, "Tonkotsu", "Shoyu", "Spicy Miso"),
        new("NOODLES", "Buena Pasta", "MLD", "Lasagna Sheets", D, S(("250g", 78m))),
        new("NOODLES", "Buena Pasta", "MLD", "Penne", D, S(("500g", 88m))),
        new("NOODLES", "Pancit Express", "MLD", "Miki Noodles", D, S(("250g", 38m))),

        // Condiments & cooking
        new("CONDIMENTS", "Timplado", "MLD", "Soy Sauce", A, S(("200ml", 18m), ("385ml", 32m), ("1L", 62m)), false),
        new("CONDIMENTS", "Timplado", "MLD", "Cane Vinegar", A, S(("200ml", 16m), ("385ml", 28m), ("1L", 52m))),
        new("CONDIMENTS", "Timplado", "MLD", "Spiced Vinegar", C, S(("375ml", 42m))),
        new("CONDIMENTS", "Timplado", "MLD", "Fish Sauce", B, S(("350ml", 36m), ("750ml", 62m))),
        new("CONDIMENTS", "Timplado", "MLD", "Oyster Sauce", C, S(("156g", 42m), ("510g", 108m))),
        new("CONDIMENTS", "Rojo", "MLD", "Banana Ketchup", B, S(("320g", 36m), ("550g", 58m), ("1kg", 98m))),
        new("CONDIMENTS", "Rojo", "MLD", "Tomato Ketchup", C, S(("320g", 48m))),
        new("CONDIMENTS", "Rojo", "MLD", "Tomato Sauce", A, S(("115g", 16m), ("250g", 28m), ("1kg", 92m))),
        new("CONDIMENTS", "Rojo", "MLD", "Filipino-Style Spaghetti Sauce", B, S(("250g", 36m), ("560g", 72m), ("1kg", 118m)), false, "Sweet", "Italian"),
        new("CONDIMENTS", "Rojo", "MLD", "Tomato Paste", D, S(("70g", 22m), ("150g", 36m))),
        new("CONDIMENTS", "Kusinera", "MLD", "Palm Cooking Oil", A, S(("500ml", 68m), ("1L", 126m), ("2L", 238m))),
        new("CONDIMENTS", "Kusinera", "MLD", "Canola Oil", C, S(("1L", 168m), ("2L", 318m))),
        new("CONDIMENTS", "Kusinera", "MLD", "Coconut Oil", D, S(("1L", 158m))),
        new("CONDIMENTS", "Salinas", "PGC", "Iodized Salt", B, S(("250g", 12m), ("500g", 22m), ("1kg", 38m))),
        new("CONDIMENTS", "Salinas", "PGC", "Rock Salt", D, S(("1kg", 34m))),
        new("CONDIMENTS", "Kusinera", "MLD", "Ground Black Pepper", C, S(("25g", 28m), ("50g", 52m))),
        new("CONDIMENTS", "Kusinera", "MLD", "Seasoning Granules", B, S(("8g", 5m), ("50g", 32m), ("250g", 118m)), false, "Chicken", "Pork", "Beef"),
        new("CONDIMENTS", "Kusinera", "MLD", "Recipe Mix", B, S(("40g", 26m)), false, "Sinigang", "Kare-Kare", "Adobo", "Menudo", "Gata"),
        new("CONDIMENTS", "Kusinera", "MLD", "Cornstarch", D, S(("200g", 22m), ("400g", 38m))),
        new("CONDIMENTS", "Kusinera", "MLD", "Garlic Powder", D, S(("50g", 36m))),
        new("CONDIMENTS", "Rojo", "MLD", "Mayonnaise", C, S(("220ml", 72m), ("470ml", 138m)), false, "Real", "Lite"),
        new("CONDIMENTS", "Timplado", "MLD", "Calamansi Soy Sauce", C, S(("250ml", 36m))),
        new("CONDIMENTS", "Timplado", "MLD", "Bagoong Alamang", C, S(("250g", 78m)), false, "Sweet", "Spicy"),
        new("CONDIMENTS", "Rojo", "MLD", "Hot Sauce", D, S(("150ml", 42m))),
        new("CONDIMENTS", "Rojo", "MLD", "Barbecue Marinade", C, S(("300ml", 62m))),
        new("CONDIMENTS", "Kusinera", "MLD", "Bread Crumbs", D, S(("230g", 48m))),
        new("CONDIMENTS", "Kusinera", "MLD", "Crispy Fry Breading Mix", C, S(("62g", 22m)), false, "Original", "Spicy"),
        new("CONDIMENTS", "Kusinera", "MLD", "Instant Soup", C, S(("60g", 38m)), false, "Mushroom", "Crab & Corn", "Chicken"),
        new("CONDIMENTS", "Kusinera", "MLD", "Bay Leaves", D, S(("10g", 18m))),
        new("CONDIMENTS", "Kusinera", "MLD", "Paprika", D, S(("35g", 42m))),

        // Breakfast & spreads
        new("BREAKFAST", "Spread Joy", "MLD", "Peanut Butter", B, S(("224g", 72m), ("340g", 102m)), false, "Creamy", "Chunky"),
        new("BREAKFAST", "Spread Joy", "MLD", "Chocolate Hazelnut Spread", C, S(("200g", 118m), ("350g", 189m))),
        new("BREAKFAST", "Spread Joy", "MLD", "Cheese Spread", B, S(("220g", 82m))),
        new("BREAKFAST", "Spread Joy", "MLD", "Coco Jam", C, S(("250g", 56m))),
        new("BREAKFAST", "Spread Joy", "MLD", "Strawberry Jam", D, S(("250g", 92m))),
        new("BREAKFAST", "Bee Farm", "MLD", "Wild Honey", D, S(("250ml", 148m))),
        new("BREAKFAST", "Morning Fields", "PGC", "Corn Flakes", C, S(("275g", 112m), ("500g", 185m))),
        new("BREAKFAST", "Morning Fields", "PGC", "Choco Cereal", C, S(("170g", 92m), ("330g", 165m))),
        new("BREAKFAST", "Morning Fields", "PGC", "Champorado Mix", D, S(("200g", 48m))),
        new("BREAKFAST", "Spread Joy", "MLD", "Sandwich Spread", C, S(("220ml", 72m))),
        new("BREAKFAST", "Bee Farm", "MLD", "Pancake Syrup", D, S(("355ml", 98m))),
        new("BREAKFAST", "Morning Fields", "PGC", "Granola", D, S(("400g", 198m)), false, "Honey", "Berries"),
        new("BREAKFAST", "Morning Fields", "PGC", "Instant Oatmeal", C, S(("10x35g", 118m)), false, "Original", "Chocolate", "Fruits"),

        // Snacks & chips
        new("SNACKS", "Crunchy Isla", "SNX", "Potato Chips", A, S(("25g", 20m), ("60g", 42m), ("160g", 98m)), false, "Original", "Sour Cream", "Barbecue", "Cheese"),
        new("SNACKS", "Crunchy Isla", "SNX", "Cheese Rings", B, S(("60g", 28m), ("110g", 48m))),
        new("SNACKS", "Crunchy Isla", "SNX", "Corn Chips", B, S(("26g", 14m), ("110g", 42m)), false, "Cheese", "Chili & Cheese"),
        new("SNACKS", "Crunchy Isla", "SNX", "Tortilla Chips", C, S(("100g", 68m)), false, "Nacho Cheese", "Salsa"),
        new("SNACKS", "Kropek King", "SNX", "Prawn Crackers", B, S(("30g", 16m), ("100g", 42m)), false, "Original", "Spicy"),
        new("SNACKS", "Kropek King", "SNX", "Chicharron", C, S(("30g", 22m), ("90g", 58m)), false, "Plain", "Spicy Vinegar"),
        new("SNACKS", "Kropek King", "SNX", "Banana Chips", C, S(("100g", 38m))),
        new("SNACKS", "Mani Masa", "SNX", "Roasted Peanuts", B, S(("40g", 14m), ("100g", 32m)), false, "Garlic", "Adobo", "Salted"),
        new("SNACKS", "Mani Masa", "SNX", "Coated Green Peas", C, S(("30g", 12m), ("100g", 32m))),
        new("SNACKS", "Mani Masa", "SNX", "Cornick", C, S(("100g", 26m)), false, "Garlic", "Chili", "Barbecue"),
        new("SNACKS", "Mani Masa", "SNX", "Mixed Nuts", D, S(("150g", 98m))),
        new("SNACKS", "Crunchy Isla", "SNX", "Kettle Chips", D, S(("150g", 108m)), false, "Sea Salt", "Salt & Vinegar", "Jalapeño"),
        new("SNACKS", "Crunchy Isla", "SNX", "Fish Crackers", B, S(("30g", 12m), ("100g", 32m)), false, "Original", "Spicy"),
        new("SNACKS", "Kropek King", "SNX", "Squid Rings", C, S(("60g", 32m)), false, "Original", "Spicy"),
        new("SNACKS", "Mani Masa", "SNX", "Dried Mango", C, S(("100g", 98m), ("200g", 185m))),

        // Biscuits & sweets
        new("BISCUITS", "Galletas Rico", "SNX", "Crackers", A, S(("10x25g", 62m)), false, "Plain", "Sugar-Coated", "Wheat"),
        new("BISCUITS", "Galletas Rico", "SNX", "Butter Cookies", C, S(("200g", 86m), ("454g", 178m))),
        new("BISCUITS", "Galletas Rico", "SNX", "Cream Sandwich Biscuits", B, S(("10x30g", 72m)), false, "Chocolate", "Vanilla", "Strawberry", "Peanut Butter"),
        new("BISCUITS", "Galletas Rico", "SNX", "Wafer Sticks", C, S(("10x16g", 56m)), false, "Chocolate", "Ube"),
        new("BISCUITS", "Galletas Rico", "SNX", "Graham Crackers", C, S(("200g", 48m), ("700g", 142m))),
        new("BISCUITS", "Choco Tambo", "SNX", "Chocolate Bar", B, S(("25g", 20m), ("40g", 32m), ("100g", 72m)), false, "Milk", "Dark", "Almond"),
        new("BISCUITS", "Choco Tambo", "SNX", "Chocolate-Coated Wafer", B, S(("26g", 12m))),
        new("BISCUITS", "Choco Tambo", "SNX", "Chocolate Crinkles", D, S(("6pcs", 68m))),
        new("BISCUITS", "Sweet Bayan", "SNX", "Hard Candy", C, S(("50pcs", 58m)), false, "Menthol", "Coffee", "Fruit"),
        new("BISCUITS", "Sweet Bayan", "SNX", "Chewing Gum", C, S(("12pcs", 38m)), false, "Spearmint", "Peppermint"),
        new("BISCUITS", "Sweet Bayan", "SNX", "Polvoron", D, S(("12pcs", 72m)), false, "Classic", "Cookies & Cream", "Pinipig"),
        new("BISCUITS", "Galletas Rico", "SNX", "Marie Biscuits", C, S(("250g", 58m))),
        new("BISCUITS", "Galletas Rico", "SNX", "Chocolate Chip Cookies", C, S(("180g", 88m), ("350g", 158m))),
        new("BISCUITS", "Galletas Rico", "SNX", "Egg Cookies", D, S(("200g", 62m))),
        new("BISCUITS", "Galletas Rico", "SNX", "Oatmeal Cookies", D, S(("200g", 78m))),
        new("BISCUITS", "Galletas Rico", "SNX", "Rice Crackers", C, S(("100g", 42m)), false, "Original", "Cheese"),
        new("BISCUITS", "Choco Tambo", "SNX", "Chocolate-Coated Peanuts", C, S(("40g", 22m), ("100g", 52m))),
        new("BISCUITS", "Sweet Bayan", "SNX", "Gummy Candy", C, S(("50g", 28m)), false, "Bears", "Worms", "Sour Mix"),
        new("BISCUITS", "Sweet Bayan", "SNX", "Lollipop", C, S(("10pcs", 45m))),

        // Beverages
        new("BEVERAGES", "Agua Pura", "IBC", "Purified Drinking Water", A, S(("350ml", 12m), ("500ml", 15m), ("1L", 25m), ("6L", 85m))),
        new("BEVERAGES", "Agua Pura", "IBC", "Natural Mineral Water", B, S(("500ml", 22m), ("1.5L", 42m))),
        new("BEVERAGES", "Fizzco", "IBC", "Cola", A, S(("290ml", 20m), ("1L", 58m), ("1.5L", 78m)), false, "Regular", "Zero Sugar"),
        new("BEVERAGES", "Fizzco", "IBC", "Lemon-Lime Soda", B, S(("290ml", 20m), ("1.5L", 74m))),
        new("BEVERAGES", "Fizzco", "IBC", "Orange Soda", B, S(("290ml", 20m), ("1.5L", 74m))),
        new("BEVERAGES", "Fizzco", "IBC", "Root Beer", C, S(("330ml", 32m), ("1.5L", 78m))),
        new("BEVERAGES", "Katas", "IBC", "Juice Drink", B, S(("250ml", 18m), ("1L", 62m)), false, "Orange", "Mango", "Pineapple", "Four Seasons"),
        new("BEVERAGES", "Katas", "IBC", "100% Pineapple Juice", C, S(("240ml", 38m), ("1L", 118m))),
        new("BEVERAGES", "Katas", "IBC", "Powdered Juice Drink", A, S(("25g", 12m), ("250g", 92m)), false, "Orange", "Mango", "Calamansi", "Pineapple"),
        new("BEVERAGES", "Tsaa Leaf", "IBC", "Iced Tea", B, S(("500ml", 32m), ("1L", 52m)), false, "Lemon", "Apple", "Red Tea"),
        new("BEVERAGES", "Volta", "IBC", "Energy Drink", B, S(("150ml", 20m), ("250ml", 42m)), false, "Original", "Sugar-Free"),
        new("BEVERAGES", "Volta", "IBC", "Sports Drink", C, S(("500ml", 42m)), false, "Blue", "Lemon-Lime", "Orange"),
        new("BEVERAGES", "Buko Gold", "IBC", "Coconut Water", C, S(("330ml", 45m), ("1L", 118m))),
        new("BEVERAGES", "Fizzco", "IBC", "Soda in Can", B, S(("330ml", 38m)), false, "Cola", "Lemon-Lime", "Orange", "Grape", "Cream Soda"),
        new("BEVERAGES", "Katas", "IBC", "Juice Box", B, S(("200ml", 20m)), false, "Apple", "Orange", "Mango", "Guava", "Grape"),
        new("BEVERAGES", "Tsaa Leaf", "IBC", "Milk Tea", C, S(("350ml", 48m)), false, "Wintermelon", "Okinawa", "Taro", "Classic"),
        new("BEVERAGES", "Buko Gold", "IBC", "Soy Milk", C, S(("250ml", 32m), ("1L", 98m)), false, "Original", "Chocolate"),
        new("BEVERAGES", "Volta", "IBC", "Vitamin Water", D, S(("500ml", 45m)), false, "Lemon", "Berry", "Citrus"),
        new("BEVERAGES", "Agua Pura", "IBC", "Alkaline Water", C, S(("500ml", 25m), ("1L", 42m))),

        // Coffee & tea
        new("COFFEE", "Barako Heights", "HCT", "3-in-1 Coffee Mix", A, S(("10x20g", 72m), ("30x20g", 205m)), false, "Original", "Brown", "Creamy White", "Mild"),
        new("COFFEE", "Barako Heights", "HCT", "Instant Coffee", B, S(("25g", 42m), ("50g", 78m), ("100g", 148m))),
        new("COFFEE", "Barako Heights", "HCT", "Ground Barako Coffee", C, S(("250g", 168m))),
        new("COFFEE", "Kape Uno", "HCT", "Twin-Pack Coffee Stick", A, S(("28g", 11m)), false, "Original", "Brown", "White"),
        new("COFFEE", "Kape Uno", "HCT", "Premium Coffee Beans", D, S(("250g", 265m)), false, "Arabica", "Barako Blend"),
        new("COFFEE", "Kape Uno", "HCT", "Coffee Creamer", B, S(("80g", 28m), ("170g", 56m), ("450g", 132m))),
        new("COFFEE", "Tsaa Leaf", "HCT", "Black Tea Bags", D, S(("25s", 68m), ("100s", 238m))),
        new("COFFEE", "Tsaa Leaf", "HCT", "Salabat Ginger Tea", C, S(("10s", 72m))),
        new("COFFEE", "Barako Heights", "HCT", "Canned Coffee", C, S(("240ml", 42m)), false, "Black", "Latte", "Mocha"),
        new("COFFEE", "Barako Heights", "HCT", "Decaf Instant Coffee", D, S(("50g", 98m))),
        new("COFFEE", "Kape Uno", "HCT", "Iced Coffee Mix", C, S(("10x20g", 88m)), false, "Caramel", "Vanilla"),
        new("COFFEE", "Tsaa Leaf", "HCT", "Chamomile Tea", D, S(("20s", 92m))),
        new("COFFEE", "Tsaa Leaf", "HCT", "Lemongrass Tea", D, S(("20s", 82m))),

        // Milk & dairy
        new("DAIRY", "Pastulan", "DFP", "Evaporated Milk", A, S(("154ml", 22m), ("370ml", 42m))),
        new("DAIRY", "Pastulan", "DFP", "Sweetened Condensed Milk", B, S(("168ml", 28m), ("300ml", 56m))),
        new("DAIRY", "Pastulan", "DFP", "Fresh Milk", B, S(("200ml", 22m), ("1L", 98m)), false, "Full Cream", "Low Fat"),
        new("DAIRY", "Pastulan", "DFP", "Powdered Milk", B, S(("33g", 12m), ("150g", 72m), ("700g", 312m), ("1.2kg", 528m))),
        new("DAIRY", "Pastulan", "DFP", "All-Purpose Cream", C, S(("250ml", 72m))),
        new("DAIRY", "Creamfield", "DFP", "Yogurt Drink", C, S(("110ml", 28m)), false, "Strawberry", "Blueberry", "Plain"),
        new("DAIRY", "Creamfield", "DFP", "Chocolate Milk Drink", B, S(("180ml", 28m), ("1L", 108m))),
        new("DAIRY", "Keso Real", "DFP", "Processed Cheddar Cheese", B, S(("165g", 76m), ("440g", 178m))),
        new("DAIRY", "Keso Real", "DFP", "Quick-Melt Cheese", C, S(("160g", 82m))),
        new("DAIRY", "Keso Real", "DFP", "Cream Cheese", D, S(("225g", 158m))),
        new("DAIRY", "Creamfield", "DFP", "Salted Butter", C, S(("100g", 72m), ("225g", 142m))),
        new("DAIRY", "Creamfield", "DFP", "Margarine", B, S(("100g", 32m), ("250g", 62m))),
        new("DAIRY", "Bukid Farms", "DFP", "Fresh Chicken Eggs", A, S(("6s", 58m), ("12s", 112m), ("30s", 270m)), true, "Medium", "Large"),
        new("DAIRY", "Creamfield", "DFP", "Greek Yogurt", D, S(("150g", 68m)), false, "Plain", "Honey"),
        new("DAIRY", "Keso Real", "DFP", "Grated Parmesan", D, S(("85g", 168m))),
        new("DAIRY", "Bukid Farms", "DFP", "Kesong Puti", D, S(("150g", 92m))),
        new("DAIRY", "Creamfield", "DFP", "Whipping Cream", D, S(("250ml", 128m))),

        // Frozen & chilled
        new("FROZEN", "Pinoy Grill", "CCF", "Jumbo Hotdog", B, S(("500g", 118m), ("1kg", 218m)), false, "Regular", "Cheese"),
        new("FROZEN", "Pinoy Grill", "CCF", "Chicken Hotdog", B, S(("500g", 98m), ("1kg", 182m))),
        new("FROZEN", "Pinoy Grill", "CCF", "Sweet Pork Tocino", B, S(("225g", 88m), ("450g", 168m))),
        new("FROZEN", "Pinoy Grill", "CCF", "Pork Longganisa", B, S(("250g", 92m), ("500g", 172m)), false, "Hamonado", "Garlic"),
        new("FROZEN", "Pinoy Grill", "CCF", "Beef Tapa", C, S(("250g", 128m))),
        new("FROZEN", "Pinoy Grill", "CCF", "Skinless Longganisa", C, S(("250g", 84m))),
        new("FROZEN", "Hapag", "CCF", "Chicken Nuggets", C, S(("200g", 82m), ("500g", 178m))),
        new("FROZEN", "Hapag", "CCF", "Breaded Fish Fillet", D, S(("400g", 148m))),
        new("FROZEN", "Hapag", "CCF", "Lumpiang Shanghai", C, S(("20pcs", 138m))),
        new("FROZEN", "Hapag", "CCF", "Siomai", C, S(("12pcs", 118m)), false, "Pork", "Shrimp"),
        new("FROZEN", "Hapag", "CCF", "Siopao Asado", D, S(("6pcs", 148m))),
        new("FROZEN", "Hapag", "CCF", "Sliced Ham", C, S(("250g", 128m))),
        new("FROZEN", "Hapag", "CCF", "Bacon", C, S(("200g", 148m))),
        new("FROZEN", "Hapag", "CCF", "Ice Cream", C, S(("750ml", 168m), ("1.5L", 298m)), false, "Ube", "Cookies & Cream", "Mango", "Rocky Road"),
        new("FROZEN", "Hapag", "CCF", "Ice Pop", B, S(("60ml", 15m)), false, "Buko", "Mango", "Chocolate"),
        new("FROZEN", "Pinoy Grill", "CCF", "Pork Embutido", D, S(("400g", 158m))),
        new("FROZEN", "Hapag", "CCF", "Mixed Vegetables", D, S(("500g", 98m))),
        new("FROZEN", "Hapag", "CCF", "French Fries", C, S(("1kg", 188m))),
        new("FROZEN", "Hapag", "CCF", "Chicken Wings", C, S(("1kg", 268m))),
        new("FROZEN", "Hapag", "CCF", "Fish Balls", C, S(("500g", 78m))),
        new("FROZEN", "Hapag", "CCF", "Squid Balls", C, S(("500g", 92m))),
        new("FROZEN", "Hapag", "CCF", "Frozen Pizza 9in", D, S(("1s", 198m)), false, "Hawaiian", "Pepperoni"),

        // Bakery
        new("BAKERY", "Panaderia Luna", "PLB", "Pandesal", A, S(("10pcs", 50m), ("20pcs", 95m))),
        new("BAKERY", "Panaderia Luna", "PLB", "Sliced White Bread", A, S(("400g", 64m), ("600g", 88m))),
        new("BAKERY", "Panaderia Luna", "PLB", "Whole Wheat Bread", C, S(("450g", 92m))),
        new("BAKERY", "Panaderia Luna", "PLB", "Spanish Bread", C, S(("6pcs", 58m))),
        new("BAKERY", "Panaderia Luna", "PLB", "Ensaymada", C, S(("4pcs", 88m)), false, "Classic", "Ube Cheese"),
        new("BAKERY", "Panaderia Luna", "PLB", "Monay", D, S(("6pcs", 52m))),
        new("BAKERY", "Panaderia Luna", "PLB", "Hopia", C, S(("5pcs", 55m)), false, "Mongo", "Ube", "Baboy"),
        new("BAKERY", "Panaderia Luna", "PLB", "Chiffon Cake", D, S(("1 slice box", 45m), ("whole", 285m)), false, "Mocha", "Pandan"),
        new("BAKERY", "Panaderia Luna", "PLB", "Burger Buns", D, S(("6pcs", 62m))),
        new("BAKERY", "Panaderia Luna", "PLB", "Pan de Coco", C, S(("6pcs", 58m))),
        new("BAKERY", "Panaderia Luna", "PLB", "Cheese Bread", C, S(("6pcs", 62m))),
        new("BAKERY", "Panaderia Luna", "PLB", "Kababayan", D, S(("6pcs", 55m))),
        new("BAKERY", "Panaderia Luna", "PLB", "Ube Loaf", D, S(("1s", 95m))),
        new("BAKERY", "Panaderia Luna", "PLB", "Banana Cake", D, S(("1 slice box", 42m))),

        // Household & cleaning
        new("HOUSEHOLD", "Presko", "HCS", "Laundry Powder", A, S(("66g", 12m), ("400g", 68m), ("1kg", 158m), ("2.1kg", 318m)), false, "Original", "Antibac", "Floral"),
        new("HOUSEHOLD", "Presko", "HCS", "Liquid Laundry Detergent", C, S(("1L", 198m), ("2L", 368m))),
        new("HOUSEHOLD", "Presko", "HCS", "Laundry Bar Soap", B, S(("130g", 22m), ("4x130g", 84m))),
        new("HOUSEHOLD", "Bango", "HCS", "Fabric Conditioner", B, S(("20ml", 8m), ("450ml", 78m), ("1L", 158m)), false, "Blue", "Pink", "Sunrise Fresh"),
        new("HOUSEHOLD", "Kinis", "HCS", "Dishwashing Liquid", A, S(("250ml", 38m), ("500ml", 68m), ("1L", 128m)), false, "Calamansi", "Lemon", "Antibac"),
        new("HOUSEHOLD", "Kinis", "HCS", "Dishwashing Paste", C, S(("400g", 56m))),
        new("HOUSEHOLD", "Puti", "HCS", "Color-Safe Bleach", C, S(("500ml", 42m), ("1L", 78m))),
        new("HOUSEHOLD", "Puti", "HCS", "Regular Bleach", B, S(("250ml", 22m), ("1L", 58m))),
        new("HOUSEHOLD", "Linis Pro", "HCS", "Toilet Bowl Cleaner", C, S(("500ml", 88m))),
        new("HOUSEHOLD", "Linis Pro", "HCS", "Multi-Surface Cleaner", C, S(("500ml", 118m)), false, "Pine", "Lemon"),
        new("HOUSEHOLD", "Linis Pro", "HCS", "Glass Cleaner", D, S(("500ml", 98m))),
        new("HOUSEHOLD", "Linis Pro", "HCS", "Insect Killer Spray", C, S(("300ml", 168m), ("600ml", 298m))),
        new("HOUSEHOLD", "Linis Pro", "HCS", "Mosquito Coil", B, S(("10s", 42m))),
        new("HOUSEHOLD", "Lambot", "HCS", "Bathroom Tissue", A, S(("1 roll", 18m), ("4 rolls", 72m), ("12 rolls", 198m))),
        new("HOUSEHOLD", "Lambot", "HCS", "Paper Towels", C, S(("2 rolls", 88m))),
        new("HOUSEHOLD", "Lambot", "HCS", "Facial Tissue", C, S(("150 sheets", 62m))),
        new("HOUSEHOLD", "Lambot", "HCS", "Table Napkins", C, S(("100s", 38m))),
        new("HOUSEHOLD", "Linis Pro", "HCS", "Garbage Bags", B, S(("Medium 10s", 45m), ("Large 10s", 58m), ("XL 10s", 72m))),
        new("HOUSEHOLD", "Linis Pro", "HCS", "Sponge Scourer", C, S(("3s", 38m))),
        new("HOUSEHOLD", "Linis Pro", "HCS", "Aluminum Foil", D, S(("7.6m", 78m))),
        new("HOUSEHOLD", "Linis Pro", "HCS", "Cling Wrap", D, S(("30m", 72m))),
        new("HOUSEHOLD", "Linis Pro", "HCS", "Disposable Plastic Cups", D, S(("50s", 58m))),
        new("HOUSEHOLD", "Linis Pro", "HCS", "Matches", C, S(("10 boxes", 25m))),
        new("HOUSEHOLD", "Liwanag", "HCS", "AA Batteries", C, S(("2s", 52m), ("4s", 98m))),
        new("HOUSEHOLD", "Liwanag", "HCS", "LED Bulb", D, S(("9W", 98m), ("13W", 138m))),
        new("HOUSEHOLD", "Liwanag", "HCS", "Butane Canister", C, S(("220g", 68m))),
        new("HOUSEHOLD", "Presko", "HCS", "Stain Remover", D, S(("500ml", 118m))),
        new("HOUSEHOLD", "Linis Pro", "HCS", "Air Freshener Spray", C, S(("300ml", 138m)), false, "Lavender", "Citrus", "Sampaguita"),
        new("HOUSEHOLD", "Linis Pro", "HCS", "Floor Wax", D, S(("300g", 88m))),
        new("HOUSEHOLD", "Linis Pro", "HCS", "Rubber Gloves", D, S(("Medium", 72m), ("Large", 72m))),
        new("HOUSEHOLD", "Linis Pro", "HCS", "Soft Broom", D, S(("1s", 148m))),
        new("HOUSEHOLD", "Linis Pro", "HCS", "Dustpan", D, S(("1s", 68m))),
        new("HOUSEHOLD", "Linis Pro", "HCS", "Ice Bags", C, S(("100s", 38m))),
        new("HOUSEHOLD", "Linis Pro", "HCS", "Resealable Zip Bags", D, S(("Medium 20s", 68m))),
        new("HOUSEHOLD", "Liwanag", "HCS", "AAA Batteries", C, S(("2s", 52m), ("4s", 98m))),
        new("HOUSEHOLD", "Liwanag", "HCS", "9V Battery", D, S(("1s", 88m))),
        new("HOUSEHOLD", "Liwanag", "HCS", "LED Flashlight", D, S(("1s", 168m))),
        new("HOUSEHOLD", "Liwanag", "HCS", "Emergency Candles", C, S(("6s", 42m))),
        new("HOUSEHOLD", "Liwanag", "HCS", "Disposable Lighter", C, S(("1s", 22m))),

        // Personal care
        new("PERSONAL", "Aloe Isla", "PCD", "Shampoo", B, S(("12ml sachet", 7m), ("170ml", 118m), ("340ml", 208m)), false, "Anti-Dandruff", "Smooth & Silky", "Keratin", "Anti-Hairfall"),
        new("PERSONAL", "Aloe Isla", "PCD", "Conditioner", C, S(("12ml sachet", 7m), ("170ml", 122m)), false, "Smooth & Silky", "Damage Repair"),
        new("PERSONAL", "Fresco", "PCD", "Bath Soap", A, S(("60g", 22m), ("135g", 42m), ("3x135g", 118m)), false, "White", "Papaya", "Antibac", "Oatmeal"),
        new("PERSONAL", "Fresco", "PCD", "Body Wash", D, S(("250ml", 168m))),
        new("PERSONAL", "Ngiti", "PCD", "Toothpaste", B, S(("50g", 42m), ("150g", 98m), ("2x150g", 185m)), false, "Fresh Mint", "Whitening"),
        new("PERSONAL", "Ngiti", "PCD", "Toothbrush", C, S(("1s", 38m), ("3s", 98m)), false, "Soft", "Medium"),
        new("PERSONAL", "Ngiti", "PCD", "Mouthwash", D, S(("250ml", 128m))),
        new("PERSONAL", "Aircool", "PCD", "Roll-On Deodorant", C, S(("25ml", 48m), ("50ml", 92m)), false, "Men", "Women"),
        new("PERSONAL", "Aircool", "PCD", "Men's Body Spray", D, S(("150ml", 178m))),
        new("PERSONAL", "Lambot", "PCD", "Sanitary Napkins", B, S(("8s", 42m), ("16s", 78m)), false, "Day", "Overnight"),
        new("PERSONAL", "Lambot", "PCD", "Pantyliners", C, S(("20s", 48m))),
        new("PERSONAL", "Fresco", "PCD", "Cotton Buds", C, S(("100s", 32m))),
        new("PERSONAL", "Fresco", "PCD", "Disposable Razor", C, S(("2s", 52m))),
        new("PERSONAL", "Fresco", "PCD", "Hand & Body Lotion", C, S(("100ml", 98m), ("200ml", 168m))),
        new("PERSONAL", "Fresco", "PCD", "Petroleum Jelly", D, S(("50g", 52m))),
        new("PERSONAL", "Fresco", "PCD", "Hair Gel", D, S(("150ml", 78m))),
        new("PERSONAL", "Aloe Isla", "PCD", "Hair Color Cream", D, S(("1 kit", 148m)), false, "Natural Black", "Dark Brown"),
        new("PERSONAL", "Fresco", "PCD", "Facial Wash", C, S(("100g", 128m)), false, "Oil Control", "Brightening"),
        new("PERSONAL", "Fresco", "PCD", "Sunscreen Lotion SPF 50", D, S(("50ml", 248m))),
        new("PERSONAL", "Aircool", "PCD", "Anti-Perspirant Spray", D, S(("150ml", 188m))),
        new("PERSONAL", "Ngiti", "PCD", "Dental Floss", D, S(("50m", 88m))),
        new("PERSONAL", "Fresco", "PCD", "Nail Polish Remover", D, S(("60ml", 42m))),

        // Baby care
        new("BABY", "Munting Anghel", "PCD", "Baby Diapers", B, S(("Small 20s", 298m), ("Medium 18s", 298m), ("Large 16s", 298m), ("XL 14s", 298m))),
        new("BABY", "Munting Anghel", "PCD", "Baby Pants", C, S(("Large 20s", 348m), ("XL 18s", 348m))),
        new("BABY", "Munting Anghel", "PCD", "Baby Wipes", B, S(("30s", 58m), ("80s", 128m))),
        new("BABY", "Munting Anghel", "PCD", "Baby Bath", D, S(("200ml", 138m))),
        new("BABY", "Munting Anghel", "PCD", "Baby Powder", D, S(("100g", 68m))),
        new("BABY", "Baby Kalinga", "DFP", "Infant Formula 0-6 Months", D, S(("350g", 398m))),
        new("BABY", "Baby Kalinga", "DFP", "Growing-Up Milk 1-3 Years", C, S(("350g", 258m), ("700g", 498m))),
        new("BABY", "Baby Kalinga", "DFP", "Baby Cereal", D, S(("120g", 88m)), false, "Rice", "Banana"),
        new("BABY", "Munting Anghel", "PCD", "Baby Oil", D, S(("100ml", 98m))),
        new("BABY", "Munting Anghel", "PCD", "Baby Cologne", D, S(("100ml", 88m))),
        new("BABY", "Munting Anghel", "PCD", "Diaper Rash Cream", D, S(("50g", 168m))),

        // Health & wellness
        new("HEALTH", "Sanitas", "MSD", "Isopropyl Alcohol 70%", B, S(("60ml", 28m), ("250ml", 58m), ("500ml", 92m))),
        new("HEALTH", "Sanitas", "MSD", "Ethyl Alcohol 70%", C, S(("250ml", 68m), ("500ml", 108m))),
        new("HEALTH", "Sanitas", "MSD", "Hand Sanitizer", C, S(("50ml", 45m), ("250ml", 128m))),
        new("HEALTH", "Sanitas", "MSD", "Face Masks", C, S(("10s", 58m), ("50s", 198m))),
        new("HEALTH", "Sanitas", "MSD", "Adhesive Bandages", C, S(("20s", 42m))),
        new("HEALTH", "Sanitas", "MSD", "Cotton Balls", D, S(("100s", 42m))),
        new("HEALTH", "VitaSigla", "MSD", "Vitamin C 500mg", C, S(("10s", 58m), ("100s", 498m))),
        new("HEALTH", "VitaSigla", "MSD", "Multivitamins", D, S(("30s", 285m))),
        new("HEALTH", "VitaSigla", "MSD", "Oral Rehydration Salts", D, S(("4 sachets", 68m))),
        new("HEALTH", "Lunas", "MSD", "Medicated Oil", C, S(("25ml", 58m), ("50ml", 98m))),
        new("HEALTH", "Lunas", "MSD", "Menthol Ointment", C, S(("10g", 42m), ("25g", 78m))),
        new("HEALTH", "Lunas", "MSD", "Pain Relief Patch", D, S(("5s", 88m))),
        new("HEALTH", "Lunas", "MSD", "Throat Lozenges", C, S(("8s", 42m)), false, "Honey Lemon", "Mint"),
        new("HEALTH", "Sanitas", "MSD", "Digital Thermometer", D, S(("1s", 148m))),
        new("HEALTH", "Sanitas", "MSD", "Povidone-Iodine Solution", D, S(("60ml", 98m))),
        new("HEALTH", "Sanitas", "MSD", "Elastic Bandage", D, S(("3in", 68m))),
        new("HEALTH", "VitaSigla", "MSD", "Zinc + Vitamin C", D, S(("30s", 198m))),
        new("HEALTH", "VitaSigla", "MSD", "Fish Oil 1000mg", D, S(("30s", 258m))),
        new("HEALTH", "Lunas", "MSD", "Lagundi Herbal Syrup", D, S(("60ml", 98m))),

        // Pet care
        new("PET", "Bantay", "MLD", "Adult Dog Food", C, S(("1kg", 158m), ("3kg", 445m)), false, "Beef", "Chicken"),
        new("PET", "Bantay", "MLD", "Puppy Food", D, S(("1kg", 178m))),
        new("PET", "Bantay", "MLD", "Dog Treats", D, S(("100g", 88m))),
        new("PET", "Mingming", "MLD", "Dry Cat Food", C, S(("1kg", 188m)), false, "Tuna", "Ocean Fish"),
        new("PET", "Mingming", "MLD", "Wet Cat Food Pouch", B, S(("85g", 32m)), false, "Tuna", "Chicken", "Salmon"),
        new("PET", "Mingming", "MLD", "Cat Litter", D, S(("5L", 198m))),

        // School & office
        new("SCHOOL", "Sulat", "OSP", "Ballpen", B, S(("1s", 10m), ("12s", 108m)), false, "Black", "Blue", "Red"),
        new("SCHOOL", "Sulat", "OSP", "Pencil No. 2", C, S(("12s", 72m))),
        new("SCHOOL", "Sulat", "OSP", "Composition Notebook", C, S(("80 leaves", 32m))),
        new("SCHOOL", "Sulat", "OSP", "Spiral Notebook", C, S(("100 leaves", 58m))),
        new("SCHOOL", "Sulat", "OSP", "Intermediate Pad Paper", C, S(("80 leaves", 38m))),
        new("SCHOOL", "Sulat", "OSP", "Yellow Pad", C, S(("80 leaves", 48m))),
        new("SCHOOL", "Sulat", "OSP", "Short Bond Paper", C, S(("50s", 62m), ("500s", 298m))),
        new("SCHOOL", "Sulat", "OSP", "Glue Stick", D, S(("8g", 28m))),
        new("SCHOOL", "Sulat", "OSP", "Clear Tape", D, S(("18mm", 32m))),
        new("SCHOOL", "Sulat", "OSP", "Correction Tape", D, S(("5mm", 48m))),
        new("SCHOOL", "Sulat", "OSP", "Crayons", D, S(("16 colors", 58m))),
        new("SCHOOL", "Sulat", "OSP", "Long Brown Envelope", D, S(("10s", 42m))),
        new("SCHOOL", "Sulat", "OSP", "Scissors", D, S(("1s", 68m))),
        new("SCHOOL", "Sulat", "OSP", "Permanent Marker", D, S(("1s", 38m)), false, "Black", "Blue"),
    ];

    // Declared after Lines: static initializers run in textual order.
    /// <summary>Every product, in catalogue order.</summary>
    public static IReadOnlyList<DevelopmentProduct> Products { get; } = Build();

    /// <summary>Every brand the products carry.</summary>
    public static IReadOnlyList<string> Brands { get; } =
        [.. Lines.Select(l => l.Brand).Distinct(StringComparer.Ordinal)];

    private static List<DevelopmentProduct> Build()
    {
        Dictionary<string, DevelopmentCategory> categories = Categories.ToDictionary(c => c.Code, StringComparer.Ordinal);
        Dictionary<string, int> skuCounters = [];
        Dictionary<string, int> brandCodes = [];
        List<DevelopmentProduct> products = [];

        foreach (ProductLine line in Lines)
        {
            DevelopmentCategory category = categories[line.Category];
            if (!brandCodes.TryGetValue(line.Brand, out int brandCode))
            {
                brandCode = 1200 + (brandCodes.Count * 37);
                brandCodes[line.Brand] = brandCode;
            }

            string[] variants = line.Variants.Length == 0 ? [string.Empty] : line.Variants;
            for (int v = 0; v < variants.Length; v++)
            {
                for (int s = 0; s < line.Sizes.Length; s++)
                {
                    (string size, decimal price) = line.Sizes[s];
                    int next = skuCounters.TryGetValue(category.SkuPrefix, out int n) ? n + 1 : 1;
                    skuCounters[category.SkuPrefix] = next;
                    string sku = string.Create(CultureInfo.InvariantCulture, $"{category.SkuPrefix}-{1000 + next}");

                    string name = string.Join(' ', new[] { line.Brand, line.Name, variants[v], size }.Where(p => p.Length > 0));

                    // Premium variants of the same line sell a little dearer.
                    decimal shelf = v == 0 ? price : RoundPrice(price * (1m + (0.03m * Math.Min(v, 2))));

                    // Margins differ by line; a stable per-SKU nudge keeps them from
                    // looking machine-made.
                    decimal margin = category.Margin + ((Jitter(sku, 3) - 0.5m) * 0.05m);
                    decimal cost = decimal.Round(shelf * (1m - margin), 2, MidpointRounding.AwayFromZero);

                    // The first size and variant is the everyday pick; others sell less.
                    decimal velocity = line.Tier
                        * (s == 0 ? 1m : s == 1 ? 0.65m : 0.35m)
                        * (v == 0 ? 1m : v == 1 ? 0.7m : 0.45m)
                        * (0.75m + (Jitter(sku, 7) * 0.5m));

                    string barcode = Ean13(string.Create(
                        CultureInfo.InvariantCulture, $"480{brandCode:D4}{products.Count + 1:D5}"));

                    products.Add(new DevelopmentProduct(
                        sku,
                        name,
                        line.Category,
                        line.Brand,
                        line.Supplier,
                        barcode,
                        shelf,
                        cost,
                        decimal.Round(velocity, 2),
                        line.VatExempt));
                }
            }
        }

        return products;
    }

    /// <summary>How many days of trading the demo history covers, ending today
    /// (<c>tools/Pos.DemoData --days 30</c> posts days 30 back to 0).</summary>
    public const int TradingDays = 31;

    /// <summary>How many days of the whole chain's demand the distribution
    /// centre holds at the start of the history.</summary>
    public const int WarehouseCoverDays = 21;

    /// <summary>A product's expected daily sale at a branch: the flagship rate
    /// scaled by the branch's size, with a stable per-branch spread so stores do
    /// not sell identical mixes.</summary>
    public static decimal DailyUnitsAt(DevelopmentProduct product, DevelopmentLocation store)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(store);
        return store.SizeFactor <= 0m
            ? 0m
            : product.DailyUnits * store.SizeFactor * (0.7m + (Jitter(product.Sku + store.Code, 11) * 0.6m));
    }

    /// <summary>How a branch stocks a product: shelf thresholds from its daily
    /// rate, and the opening balance that leaves a realistic spread at the end
    /// of the history — mostly healthy, some low, a few sold out, a few overstocked.</summary>
    public static DevelopmentStocking StockingAt(DevelopmentProduct product, DevelopmentLocation store)
    {
        decimal daily = DailyUnitsAt(product, store);
        decimal target = Math.Max(6m, Math.Ceiling(daily * 10m));
        decimal reorder = Math.Max(3m, Math.Ceiling(daily * 4m));
        decimal minimum = Math.Max(2m, Math.Ceiling(daily * 2m));
        decimal maximum = Math.Ceiling(target * 1.6m);
        decimal replenish = Math.Max(6m, Math.Ceiling((target - reorder) / 6m) * 6m);

        decimal roll = Jitter(product.Sku + store.Code, 13);
        decimal sold = Math.Ceiling(daily * TradingDays);
        decimal opening = roll switch
        {
            < 0.035m => Math.Floor(sold * 0.85m),                                     // sells out before today
            < 0.12m => sold + Math.Ceiling(target * (0.15m + (roll * 1.5m))),         // ends below the reorder point
            > 0.965m => sold + Math.Ceiling(target * 2.2m),                          // overstocked
            _ => sold + Math.Ceiling(target * (0.55m + (roll * 0.9m))),               // healthy
        };

        return new DevelopmentStocking(daily, minimum, reorder, target, maximum, replenish, Math.Max(opening, 0m));
    }

    /// <summary>What the distribution centre holds of a product at the start of
    /// the history: three weeks of the whole chain's demand, in cases of twelve.</summary>
    public static decimal WarehouseOpening(DevelopmentProduct product)
    {
        decimal chain = Locations.Where(l => l.SizeFactor > 0m).Sum(l => DailyUnitsAt(product, l));
        return Math.Max(24m, Math.Ceiling(chain * WarehouseCoverDays / 12m) * 12m);
    }

    /// <summary>A shelf price ending the way a store prices: whole pesos under
    /// a hundred, and in fives above.</summary>
    private static decimal RoundPrice(decimal price)
        => price < 100m ? Math.Ceiling(price) : Math.Ceiling(price / 5m) * 5m;

    /// <summary>A stable number in [0, 1) derived from a SKU, so the "random"
    /// spread of margins and velocities is the same on every machine.</summary>
    internal static decimal Jitter(string key, int salt)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in key)
            {
                hash = (hash ^ c) * 16777619;
            }

            hash = (hash ^ (uint)salt) * 16777619;
            hash ^= hash >> 15;
            return (hash % 10007) / 10007m;
        }
    }

    /// <summary>Completes a 12-digit EAN body with its check digit.</summary>
    private static string Ean13(string body)
    {
        int sum = 0;
        for (int i = 0; i < 12; i++)
        {
            int digit = body[i] - '0';
            sum += (i % 2 == 0) ? digit : digit * 3;
        }

        int check = (10 - (sum % 10)) % 10;
        return body + check.ToString(CultureInfo.InvariantCulture);
    }

    private sealed record ProductLine(
        string Category,
        string Brand,
        string Supplier,
        string Name,
        decimal Tier,
        (string Size, decimal Price)[] Sizes,
        bool VatExempt = false,
        params string[] Variants);
}

/// <summary>How one branch stocks one product.</summary>
/// <param name="DailyUnits">Expected units sold a day.</param>
/// <param name="Minimum">The minimum stock threshold.</param>
/// <param name="ReorderPoint">Where replenishment is triggered.</param>
/// <param name="Target">The comfortable shelf level (about ten days).</param>
/// <param name="Maximum">The most the shelf and backroom hold.</param>
/// <param name="Replenishment">The usual replenishment quantity, in cases of six.</param>
/// <param name="Opening">The balance at the start of the demo history.</param>
internal sealed record DevelopmentStocking(
    decimal DailyUnits,
    decimal Minimum,
    decimal ReorderPoint,
    decimal Target,
    decimal Maximum,
    decimal Replenishment,
    decimal Opening);

/// <summary>A branch or the distribution centre.</summary>
/// <param name="Code">The location code.</param>
/// <param name="Name">The branch name.</param>
/// <param name="SizeFactor">How busy the branch is next to the flagship (1.0); zero for the warehouse.</param>
/// <param name="BranchCode">The BIR branch code printed after the TIN.</param>
/// <param name="Address">The street address printed on receipts.</param>
internal sealed record DevelopmentLocation(string Code, string Name, decimal SizeFactor, string BranchCode, string Address);

/// <summary>A product category.</summary>
internal sealed record DevelopmentCategory(string Code, string Name, int SortOrder, string SkuPrefix, decimal Margin);

/// <summary>A distributor the chain buys from.</summary>
internal sealed record DevelopmentSupplier(string Code, string Name, string TaxId, int PaymentTermsDays, int LeadTimeDays);

/// <summary>One sellable product.</summary>
/// <param name="Sku">The internal article number.</param>
/// <param name="Name">The shelf name.</param>
/// <param name="Category">The category code.</param>
/// <param name="Brand">The brand.</param>
/// <param name="Supplier">The supplier code.</param>
/// <param name="Barcode">The EAN-13 barcode.</param>
/// <param name="Price">The shelf price, VAT inclusive.</param>
/// <param name="UnitCost">What the chain pays for one unit.</param>
/// <param name="DailyUnits">Expected units sold a day at the flagship branch.</param>
/// <param name="VatExempt">Whether the product is VAT-exempt (rice, fresh eggs).</param>
internal sealed record DevelopmentProduct(
    string Sku,
    string Name,
    string Category,
    string Brand,
    string Supplier,
    string Barcode,
    decimal Price,
    decimal UnitCost,
    decimal DailyUnits,
    bool VatExempt);
