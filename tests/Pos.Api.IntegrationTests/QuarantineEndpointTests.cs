using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Application.Identity;
using Pos.Domain.Catalog;
using Pos.Domain.Common;
using Pos.Domain.Identity;
using Pos.Domain.Inventory;
using Pos.Domain.Locations;
using Pos.Domain.Quarantine;
using Pos.Infrastructure.Identity;

namespace Pos.Api.IntegrationTests;

/// <summary>
/// Phase 8 quarantine: incidents raised at stores over unauthorized stock,
/// head office identification (link or register), disposition (release, reject,
/// write-off) with the QuarantineEntry/Release/Reject ledger groups, photographs,
/// and location scoping — all through the real pipeline.
/// </summary>
[Collection("api")]
public sealed class QuarantineEndpointTests(PosApiFactory factory)
{
    // Barcodes are unique per test class to keep the shared database free of
    // collisions (7600... sits outside the ranges the other classes use).
    private static long _barcodeSequence = 7600000000000;

    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Raise_IdentifiesKnownNonBatchLines_AndPostsEntryLedger()
    {
        Seed seed = await SeedAsync("q1");
        await factory.CreateUserAsync("q1-store", Roles.StoreManager, locations: [seed.Store]);
        await factory.CreateUserAsync("q1-ho", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string storeManager = await SignInAsync(client, "q1-store");
        string headOffice = await SignInAsync(client, "q1-ho");

        string unknownBarcode = NewBarcode();
        Guid incidentId = await CreateIncidentAsync(
            client,
            storeManager,
            seed.Store,
            [
                new { barcode = seed.PlainBarcode, quantity = 12m, claimedProductName = "Found pallet" },
                new { barcode = seed.BatchBarcode, quantity = 4m },
                new { barcode = unknownBarcode, quantity = 6m, unitCost = 50m },
            ],
            note: "Found on the stockroom floor.");

        using JsonDocument detail = await GetIncidentDetailAsync(client, headOffice, incidentId);
        JsonElement root = detail.RootElement;
        root.GetProperty("status").GetString().Should().Be("Open");
        root.GetProperty("number").GetString().Should().MatchRegex(@"^QRT-\d{4}-\d{6}$");
        root.GetProperty("note").GetString().Should().Be("Found on the stockroom floor.");

        // The known non-batch product is identified at raise time; the
        // batch-tracked and unknown lines wait for head office.
        JsonElement lines = root.GetProperty("lines");
        lines.GetArrayLength().Should().Be(3);
        lines[0].GetProperty("productId").GetGuid().Should().Be(seed.Plain.Value);
        lines[0].GetProperty("disposition").GetString().Should().Be("None");
        lines[1].TryGetProperty("productId", out JsonElement batchLine).Should().BeTrue();
        batchLine.ValueKind.Should().Be(JsonValueKind.Null);
        lines[2].GetProperty("productId").ValueKind.Should().Be(JsonValueKind.Null);

        // The entry group moves the known line from the supplier counterparty
        // into quarantine now; batch-tracked and unknown goods enter later.
        List<MovementSnapshot> movements = await MovementsByReferenceAsync(incidentId);
        movements.Should().HaveCount(2);
        movements.Should().OnlyContain(m =>
            m.MovementType == InventoryMovementType.QuarantineEntry
            && m.ApprovedByUserId == null
            && m.BatchId == null);
        movements.Sum(m => m.QuantityDelta).Should().Be(0m);
        movements.Single(m => m.LocationId == seed.Store.Value).QuantityDelta.Should().Be(12m);
        movements.Single(m => m.LocationId == seed.Store.Value).State.Should().Be(InventoryState.Quarantine);
        movements.Single(m => m.LocationId != seed.Store.Value).QuantityDelta.Should().Be(-12m);
        movements.Single(m => m.LocationId != seed.Store.Value).State.Should().Be(InventoryState.External);

        (await BalanceAsync(seed.Store, seed.Plain, null, InventoryState.Quarantine)).Should().Be(12m);
        (await BalanceAsync(seed.Store, seed.Batched, null, InventoryState.Quarantine)).Should().Be(0m);

        // The timeline records the raise and the immediate identification.
        root.GetProperty("timeline").EnumerateArray()
            .Select(e => e.GetProperty("kind").GetString())
            .Should().Equal("Raised", "ProductLinked");
    }

    [Fact]
    public async Task Raise_RefusesExternalLocationEmptyAndInvalidLines()
    {
        Seed seed = await SeedAsync("q2");
        LocationId external = await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        await factory.CreateUserAsync("q2-store", Roles.StoreManager, locations: [seed.Store]);
        // Only a business-wide user can even attempt an external location: the
        // pipeline's location-scoped check denies a store manager first.
        await factory.CreateUserAsync("q2-ho", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string storeManager = await SignInAsync(client, "q2-store");
        string headOffice = await SignInAsync(client, "q2-ho");

        using (HttpResponseMessage response = await PostAsJsonAsync(
            client,
            "/api/v1/quarantine",
            new { locationId = external.Value, lines = new[] { new { barcode = seed.PlainBarcode, quantity = 1m } } },
            headOffice))
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(response)).Should().Be("quarantine.location_external");
        }

        using (HttpResponseMessage response = await PostAsJsonAsync(
            client,
            "/api/v1/quarantine",
            new { locationId = seed.Store.Value, lines = Array.Empty<object>() },
            storeManager))
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(response)).Should().Be("quarantine.empty");
        }

        using (HttpResponseMessage response = await PostAsJsonAsync(
            client,
            "/api/v1/quarantine",
            new { locationId = seed.Store.Value, lines = new[] { new { barcode = seed.PlainBarcode, quantity = 0m } } },
            storeManager))
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(response)).Should().Be("quarantine.line_quantity_invalid");
        }
    }

    [Fact]
    public async Task Investigate_RecordsFindings_AndRefusesOtherRolesAndResolution()
    {
        Seed seed = await SeedAsync("q3");
        await factory.CreateUserAsync("q3-store", Roles.StoreManager, locations: [seed.Store]);
        await factory.CreateUserAsync("q3-ho", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("q3-red", Roles.Cashier, locations: [seed.Store]);
        using HttpClient client = factory.CreateClient();
        string storeManager = await SignInAsync(client, "q3-store");
        string headOffice = await SignInAsync(client, "q3-ho");
        string cashier = await SignInAsync(client, "q3-red");

        Guid incidentId = await CreateIncidentAsync(
            client, storeManager, seed.Store, [new { barcode = seed.PlainBarcode, quantity = 10m }]);

        // A cashier may raise an incident but not investigate one.
        using (HttpResponseMessage denied = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/investigate"),
            new { note = "Snooping." },
            cashier))
        {
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden, await denied.Content.ReadAsStringAsync());
        }

        using (HttpResponseMessage investigated = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/investigate"),
            new { note = "Barcode matches a recalled pallet." },
            storeManager))
        {
            investigated.StatusCode.Should().Be(HttpStatusCode.OK, await investigated.Content.ReadAsStringAsync());
        }

        using (JsonDocument detail = await GetIncidentDetailAsync(client, storeManager, incidentId))
        {
            detail.RootElement.GetProperty("status").GetString().Should().Be("UnderReview");
            detail.RootElement.GetProperty("investigatedAtUtc").GetDateTimeOffset().Should().BeCloseTo(
                DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
            detail.RootElement.GetProperty("timeline").EnumerateArray()
                .Select(e => e.GetProperty("kind").GetString())
                .Should().Equal("Raised", "ProductLinked", "Investigation");
        }

        // Dispositioning the last unit resolves the incident; further action is a
        // conflict rather than a duplicate movement.
        using (HttpResponseMessage released = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/release"),
            new { lineNo = 1, quantity = 10m },
            headOffice))
        {
            released.StatusCode.Should().Be(HttpStatusCode.OK, await released.Content.ReadAsStringAsync());
        }

        using (HttpResponseMessage after = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/investigate"),
            new { note = "Too late." },
            storeManager))
        {
            after.StatusCode.Should().Be(HttpStatusCode.Conflict, await after.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(after)).Should().Be("quarantine.resolved");
        }

        using (JsonDocument detail = await GetIncidentDetailAsync(client, storeManager, incidentId))
        {
            detail.RootElement.GetProperty("status").GetString().Should().Be("Resolved");
            detail.RootElement.GetProperty("resolvedAtUtc").GetDateTimeOffset().Should().BeCloseTo(
                DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        }
    }

    [Fact]
    public async Task Photos_AttachDownload_AndRefuseOversizedOrMalformed()
    {
        Seed seed = await SeedAsync("q4");
        await factory.CreateUserAsync("q4-store", Roles.StoreManager, locations: [seed.Store]);
        await factory.CreateUserAsync("q4-ho", Roles.MainInventoryManager);
        using HttpClient client = factory.CreateClient();
        string storeManager = await SignInAsync(client, "q4-store");
        string headOffice = await SignInAsync(client, "q4-ho");

        Guid incidentId = await CreateIncidentAsync(
            client, storeManager, seed.Store, [new { barcode = NewBarcode(), quantity = 3m }]);

        byte[] image = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];
        using (HttpResponseMessage added = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/photos"),
            new { fileName = "scene.jpg", contentType = "image/jpeg", data = Convert.ToBase64String(image), note = "Shelves." },
            storeManager))
        {
            added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());
        }

        Guid photoId;
        using (JsonDocument detail = await GetIncidentDetailAsync(client, headOffice, incidentId))
        {
            JsonElement photos = detail.RootElement.GetProperty("photos");
            photos.GetArrayLength().Should().Be(1);
            photoId = photos[0].GetProperty("id").GetGuid();
            photos[0].GetProperty("fileName").GetString().Should().Be("scene.jpg");
            detail.RootElement.GetProperty("timeline").EnumerateArray()
                .Select(e => e.GetProperty("kind").GetString())
                .Should().Equal("Raised", "PhotoAdded");
        }

        using (HttpResponseMessage download = await GetAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/photos/{photoId}"),
            headOffice))
        {
            download.StatusCode.Should().Be(HttpStatusCode.OK, await download.Content.ReadAsStringAsync());
            download.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
            (await download.Content.ReadAsByteArrayAsync()).Should().Equal(image);
        }

        // A payload larger than the aggregate's limit is refused before decode.
        byte[] oversized = new byte[QuarantineIncident.MaxPhotoBytes + 1];
        using (HttpResponseMessage tooLarge = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/photos"),
            new { fileName = "huge.jpg", contentType = "image/jpeg", data = Convert.ToBase64String(oversized) },
            storeManager))
        {
            tooLarge.StatusCode.Should().Be(HttpStatusCode.BadRequest, await tooLarge.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(tooLarge)).Should().Be("quarantine.photo_too_large");
        }

        using (HttpResponseMessage malformed = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/photos"),
            new { fileName = "broken.jpg", contentType = "image/jpeg", data = "not-base64!!" },
            storeManager))
        {
            malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest, await malformed.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(malformed)).Should().Be("quarantine.photo_data_invalid");
        }
    }

    [Fact]
    public async Task LinkProduct_RequiresReleasePermissionOwnedBarcodeAndLotConsistency()
    {
        Seed seed = await SeedAsync("q5");
        string barcodeOwnedByPlain = NewBarcode();
        string barcodeOwnedByBatch = NewBarcode();
        string barcodeOwnedByBatch2 = NewBarcode();
        string barcodeOwnedByOther = NewBarcode();
        ProductId plain = await factory.CreateProductAsync($"Q5P-{seed.Suffix}", "P", barcodeOwnedByPlain);
        ProductId batched = await factory.CreateProductAsync($"Q5B-{seed.Suffix}", "B", barcodeOwnedByBatch, tracksBatches: true);
        ProductId batched2 = await factory.CreateProductAsync($"Q5C-{seed.Suffix}", "C", barcodeOwnedByBatch2, tracksBatches: true);
        ProductId other = await factory.CreateProductAsync($"Q5O-{seed.Suffix}", "O", barcodeOwnedByOther);

        await factory.CreateUserAsync("q5-store", Roles.StoreManager, locations: [seed.Store]);
        await factory.CreateUserAsync("q5-ho", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string storeManager = await SignInAsync(client, "q5-store");
        string headOffice = await SignInAsync(client, "q5-ho");

        Guid incidentId = await CreateIncidentAsync(
            client,
            storeManager,
            seed.Store,
            [
                new { barcode = barcodeOwnedByPlain, quantity = 5m },
                new { barcode = barcodeOwnedByBatch, quantity = 5m },
                new { barcode = barcodeOwnedByBatch2, quantity = 5m },
            ]);

        // A store manager holds release nowhere; the endpoint refuses up front.
        using (HttpResponseMessage storeDenied = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/link-product"),
            new { lineNo = 1, productId = plain.Value },
            storeManager))
        {
            storeDenied.StatusCode.Should().Be(HttpStatusCode.Forbidden, await storeDenied.Content.ReadAsStringAsync());
        }

        // The scanned barcode must belong to the product being linked. Line two
        // carries a barcode the batch-tracked product owns, so naming any other
        // product is refused.
        using (HttpResponseMessage notOwned = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/link-product"),
            new { lineNo = 2, productId = other.Value },
            headOffice))
        {
            notOwned.StatusCode.Should().Be(HttpStatusCode.BadRequest, await notOwned.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(notOwned)).Should().Be("quarantine.barcode_not_owned");
        }

        // A lot on a product that does not track batches is a contradiction; a
        // batch-tracked product demands a lot.
        using (HttpResponseMessage mismatch = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/link-product"),
            new { lineNo = 1, productId = plain.Value, batchId = Guid.NewGuid() },
            headOffice))
        {
            mismatch.StatusCode.Should().Be(HttpStatusCode.BadRequest, await mismatch.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(mismatch)).Should().Be("quarantine.batch_mismatch");
        }

        using (HttpResponseMessage missingLot = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/link-product"),
            new { lineNo = 2, productId = batched.Value },
            headOffice))
        {
            missingLot.StatusCode.Should().Be(HttpStatusCode.BadRequest, await missingLot.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(missingLot)).Should().Be("quarantine.batch_required");
        }

        // Line three links cleanly with its lot and posts the entry ledger for
        // the batch-tracked goods.
        Guid lot = Guid.NewGuid();
        using (HttpResponseMessage linked = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/link-product"),
            new { lineNo = 3, productId = batched2.Value, batchId = lot, note = "Lot confirmed." },
            headOffice))
        {
            linked.StatusCode.Should().Be(HttpStatusCode.OK, await linked.Content.ReadAsStringAsync());
        }

        List<MovementSnapshot> movements = await MovementsByReferenceAsync(incidentId);
        // The non-batch line was identified at raise and entered then; the
        // batch-tracked goods enter with this link.
        movements.Should().HaveCount(4);
        movements.Should().OnlyContain(m => m.MovementType == InventoryMovementType.QuarantineEntry);
        movements.Single(m => m.BatchId == null && m.LocationId == seed.Store.Value).QuantityDelta.Should().Be(5m);
        movements.Single(m => m.BatchId == lot && m.LocationId == seed.Store.Value).QuantityDelta.Should().Be(5m);
        (await BalanceAsync(seed.Store, batched2, new BatchId(lot), InventoryState.Quarantine)).Should().Be(5m);

        // The store line identified at raise; a batch-tracked line cannot be
        // identified without a lot, and the failed attempts left it hanging.
        using JsonDocument detail = await GetIncidentDetailAsync(client, headOffice, incidentId);
        JsonElement lines = detail.RootElement.GetProperty("lines");
        lines[0].GetProperty("productId").GetGuid().Should().Be(plain.Value);
        lines[1].GetProperty("productId").ValueKind.Should().Be(JsonValueKind.Null);
        lines[2].GetProperty("productId").GetGuid().Should().Be(batched2.Value);
    }

    [Fact]
    public async Task RegisterProduct_CreatesMasterWithBarcode_AndPostsEntryLedger()
    {
        Seed seed = await SeedAsync("q6");
        CategoryId category = await factory.CreateCategoryAsync($"CAT-{seed.Suffix}", $"Category {seed.Suffix}");
        UnitOfMeasureId unit = await factory.CreateUnitOfMeasureAsync($"U-{seed.Suffix}", $"Unit {seed.Suffix}");
        await factory.CreateUserAsync("q6-store", Roles.StoreManager, locations: [seed.Store]);
        await factory.CreateUserAsync("q6-ho", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string storeManager = await SignInAsync(client, "q6-store");
        string headOffice = await SignInAsync(client, "q6-ho");

        string unknownBarcode = NewBarcode();
        Guid incidentId = await CreateIncidentAsync(
            client,
            storeManager,
            seed.Store,
            [new { barcode = unknownBarcode, quantity = 5m, unitCost = 40m }]);

        using (HttpResponseMessage registered = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/register-product"),
            new
            {
                lineNo = 1,
                sku = $"Q6-{seed.Suffix}",
                name = "Mystery finds",
                categoryId = category.Value,
                baseUnitOfMeasureId = unit.Value,
                defaultPurchaseCost = 40m,
                note = "No catalogue match.",
            },
            headOffice))
        {
            registered.StatusCode.Should().Be(HttpStatusCode.OK, await registered.Content.ReadAsStringAsync());
        }

        // The product master arrived carrying the scanned barcode as its primary.
        (Guid productId, string? primaryBarcode) = await factory.WithServiceAsync(async context =>
        {
            Product? product = await context.Products
                .Include(p => p.Barcodes)
                .FirstOrDefaultAsync(p => p.Barcodes.Any(b => b.IsPrimary && b.Value == unknownBarcode));
            product.Should().NotBeNull();
            return (product!.Id.Value, product.Barcodes.Single(b => b.IsPrimary).Value);
        });
        primaryBarcode.Should().Be(unknownBarcode);

        using (JsonDocument detail = await GetIncidentDetailAsync(client, headOffice, incidentId))
        {
            detail.RootElement.GetProperty("lines")[0].GetProperty("productId").GetGuid().Should().Be(productId);
            detail.RootElement.GetProperty("timeline").EnumerateArray()
                .Select(e => e.GetProperty("kind").GetString())
                .Should().Equal("Raised", "ProductRegistered");
        }

        // The entry ledger valued the goods at the declared unit cost.
        List<MovementSnapshot> movements = await MovementsByReferenceAsync(incidentId);
        movements.Should().HaveCount(2);
        movements.Should().OnlyContain(m => m.MovementType == InventoryMovementType.QuarantineEntry);
        movements.Single(m => m.LocationId == seed.Store.Value).QuantityDelta.Should().Be(5m);
        (await BalanceAsync(seed.Store, new ProductId(productId), null, InventoryState.Quarantine)).Should().Be(5m);
        (await AverageCostAsync(seed.Store, new ProductId(productId), null, InventoryState.Quarantine)).Should().Be(40m);

        // Identifying the same line again creates no second product.
        using (HttpResponseMessage again = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/register-product"),
            new
            {
                lineNo = 1,
                sku = $"Q6-{seed.Suffix}-dup",
                name = "Duplicate",
                categoryId = category.Value,
                baseUnitOfMeasureId = unit.Value,
            },
            headOffice))
        {
            again.StatusCode.Should().Be(HttpStatusCode.Conflict, await again.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(again)).Should().Be("quarantine.line_already_identified");
        }

        int catalogMatches = await factory.WithServiceAsync(
            context => context.Products.CountAsync(p => p.Barcodes.Any(b => b.IsPrimary && b.Value == unknownBarcode)));
        catalogMatches.Should().Be(1);
    }

    [Fact]
    public async Task Release_PartiallyThenFully_ApprovesLedger_AndResolvesIncident()
    {
        Seed seed = await SeedAsync("q7");
        await factory.CreateUserAsync("q7-store", Roles.StoreManager, locations: [seed.Store]);
        await factory.CreateUserAsync("q7-ho", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string storeManager = await SignInAsync(client, "q7-store");
        string headOffice = await SignInAsync(client, "q7-ho");
        UserId headOfficeId = await UserIdAsync("q7-ho");

        Guid incidentId = await CreateIncidentAsync(
            client, storeManager, seed.Store, [new { barcode = seed.PlainBarcode, quantity = 10m }]);
        (await MovementsByReferenceAsync(incidentId)).Should().HaveCount(2);

        using (HttpResponseMessage partial = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/release"),
            new { lineNo = 1, quantity = 7m, note = "Salvageable stock." },
            headOffice))
        {
            partial.StatusCode.Should().Be(HttpStatusCode.OK, await partial.Content.ReadAsStringAsync());
        }

        // The release group moves seven from quarantine into Available, carry
        // the approving head office user, and needs no reason code.
        List<MovementSnapshot> afterPartial = await MovementsByReferenceAsync(incidentId);
        afterPartial.Should().HaveCount(4);
        List<MovementSnapshot> releaseLegs = afterPartial
            .Where(m => m.MovementType == InventoryMovementType.QuarantineRelease)
            .ToList();
        releaseLegs.Should().HaveCount(2);
        releaseLegs.Should().OnlyContain(m =>
            m.ApprovedByUserId == headOfficeId.Value && m.ReasonCode == null);
        releaseLegs.Single(m => m.State == InventoryState.Quarantine).QuantityDelta.Should().Be(-7m);
        releaseLegs.Single(m => m.State == InventoryState.Available).QuantityDelta.Should().Be(7m);

        (await BalanceAsync(seed.Store, seed.Plain, null, InventoryState.Available)).Should().Be(7m);
        (await BalanceAsync(seed.Store, seed.Plain, null, InventoryState.Quarantine)).Should().Be(3m);
        (await AverageCostAsync(seed.Store, seed.Plain, null, InventoryState.Available)).Should().Be(95m);

        using (HttpResponseMessage rest = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/release"),
            new { lineNo = 1, quantity = 3m },
            headOffice))
        {
            rest.StatusCode.Should().Be(HttpStatusCode.OK, await rest.Content.ReadAsStringAsync());
        }

        using (JsonDocument detail = await GetIncidentDetailAsync(client, headOffice, incidentId))
        {
            detail.RootElement.GetProperty("status").GetString().Should().Be("Resolved");
            detail.RootElement.GetProperty("lines")[0].GetProperty("disposition").GetString().Should().Be("Released");
            detail.RootElement.GetProperty("lines")[0].GetProperty("remainingQuantity").GetDecimal().Should().Be(0m);
            detail.RootElement.GetProperty("timeline").EnumerateArray()
                .Select(e => e.GetProperty("kind").GetString())
                .Should().Equal("Raised", "ProductLinked", "Released", "Released", "Resolved");
        }

        using (HttpResponseMessage over = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/release"),
            new { lineNo = 1, quantity = 1m },
            headOffice))
        {
            over.StatusCode.Should().Be(HttpStatusCode.Conflict, await over.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(over)).Should().Be("quarantine.resolved");
        }
    }

    [Fact]
    public async Task Reject_ReturnsToSupplier_WithApprovalAndReason()
    {
        Seed seed = await SeedAsync("q8");
        LocationId external = await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        await factory.CreateUserAsync("q8-store", Roles.StoreManager, locations: [seed.Store]);
        await factory.CreateUserAsync("q8-ho", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string storeManager = await SignInAsync(client, "q8-store");
        string headOffice = await SignInAsync(client, "q8-ho");
        UserId headOfficeId = await UserIdAsync("q8-ho");

        Guid incidentId = await CreateIncidentAsync(
            client, storeManager, seed.Store, [new { barcode = seed.PlainBarcode, quantity = 6m }]);

        using (HttpResponseMessage rejected = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/reject"),
            new { lineNo = 1, quantity = 6m, note = "Wrong goods delivered." },
            headOffice))
        {
            rejected.StatusCode.Should().Be(HttpStatusCode.OK, await rejected.Content.ReadAsStringAsync());
        }

        List<MovementSnapshot> movements = await MovementsByReferenceAsync(incidentId);
        movements.Should().HaveCount(4);
        List<MovementSnapshot> rejectLegs = movements
            .Where(m => m.MovementType == InventoryMovementType.QuarantineReject)
            .ToList();
        rejectLegs.Should().HaveCount(2);
        rejectLegs.Should().OnlyContain(m =>
            m.ApprovedByUserId == headOfficeId.Value
            && m.ReasonCode == AdjustmentReasonCode.SupplierReturn);
        rejectLegs.Single(m => m.State == InventoryState.Quarantine).QuantityDelta.Should().Be(-6m);
        rejectLegs.Single(m => m.State == InventoryState.External).QuantityDelta.Should().Be(6m);
        rejectLegs.Single(m => m.State == InventoryState.External).LocationId.Should().Be(external.Value);

        (await BalanceAsync(seed.Store, seed.Plain, null, InventoryState.Quarantine)).Should().Be(0m);
        // The supplier bucket carried negative stock while the goods sat in
        // quarantine; the reject nets it back to zero, proving the returned
        // quantity reached the supplier counterparty.
        (await BalanceAsync(external, seed.Plain, null, InventoryState.External)).Should().Be(0m);

        using (HttpResponseMessage again = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/reject"),
            new { lineNo = 1, quantity = 1m },
            headOffice))
        {
            again.StatusCode.Should().Be(HttpStatusCode.Conflict, await again.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(again)).Should().Be("quarantine.resolved");
        }
    }

    [Fact]
    public async Task WriteOff_WhitelistsReasons_AndDemandsAdjustApproval()
    {
        Seed seed = await SeedAsync("q9");
        Seed other = await SeedAsync("q9b");
        LocationId writeOff = await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalWriteOff);
        await factory.CreateUserAsync("q9-store", Roles.StoreManager, locations: [seed.Store]);
        await factory.CreateUserAsync("q9-ho", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        await factory.CreateUserAsync("q9-denied", Roles.MainInventoryManager, tier: ApprovalTier.Unlimited);
        using HttpClient client = factory.CreateClient();
        string storeManager = await SignInAsync(client, "q9-store");
        string headOffice = await SignInAsync(client, "q9-ho");
        string deniedOfficer = await SignInAsync(client, "q9-denied");
        UserId headOfficeId = await UserIdAsync("q9-ho");

        // Withdraw inventory.adjust.approve from an otherwise full manager: the
        // write-off gate must stop them even though quarantine.reject remains.
        await DenyAsync("q9-denied", Permissions.Inventory.ApproveAdjustment);

        Guid incidentId = await CreateIncidentAsync(
            client, storeManager, seed.Store, [new { barcode = seed.PlainBarcode, quantity = 8m }]);

        using (HttpResponseMessage writtenOff = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/write-off"),
            new { lineNo = 1, quantity = 4m, reasonCode = AdjustmentReasonCode.Damaged },
            headOffice))
        {
            writtenOff.StatusCode.Should().Be(HttpStatusCode.OK, await writtenOff.Content.ReadAsStringAsync());
        }

        List<MovementSnapshot> movements = await MovementsByReferenceAsync(incidentId);
        List<MovementSnapshot> writeOffLegs = movements
            .Where(m => m.MovementType == InventoryMovementType.QuarantineReject)
            .ToList();
        writeOffLegs.Should().HaveCount(2);
        writeOffLegs.Should().OnlyContain(m =>
            m.ApprovedByUserId == headOfficeId.Value && m.ReasonCode == AdjustmentReasonCode.Damaged);
        writeOffLegs.Single(m => m.State == InventoryState.Quarantine).QuantityDelta.Should().Be(-4m);
        writeOffLegs.Single(m => m.State == InventoryState.External).LocationId.Should().Be(writeOff.Value);

        // Shrinkage reasons are whitelisted: a supplier-return reason is not a
        // write-off reason.
        using (HttpResponseMessage notAllowed = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}/write-off"),
            new { lineNo = 1, quantity = 4m, reasonCode = AdjustmentReasonCode.SupplierReturn },
            headOffice))
        {
            notAllowed.StatusCode.Should().Be(HttpStatusCode.BadRequest, await notAllowed.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(notAllowed)).Should().Be("quarantine.write_off_reason_not_allowed");
        }
        (await BalanceAsync(seed.Store, seed.Plain, null, InventoryState.Quarantine)).Should().Be(4m);

        // The second gate: without inventory.adjust.approve the disposition never
        // reaches the ledger, even under a valid quarantine.reject grant. A head
        // office manager raises the second incident (a store manager cannot act
        // outside their own store), then the denied officer tries to write it off.
        Guid otherIncident = await CreateIncidentAsync(
            client, headOffice, other.Store, [new { barcode = other.PlainBarcode, quantity = 3m }]);

        using (HttpResponseMessage forbidden = await PostAsJsonAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{otherIncident}/write-off"),
            new { lineNo = 1, quantity = 3m, reasonCode = AdjustmentReasonCode.Loss },
            deniedOfficer))
        {
            forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden, await forbidden.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(forbidden)).Should().Be("auth.permission_denied");
        }
        (await MovementsByReferenceAsync(otherIncident)).Should().HaveCount(2);
        (await BalanceAsync(other.Store, other.Plain, null, InventoryState.Quarantine)).Should().Be(3m);
    }

    [Fact]
    public async Task ListAndDetail_RespectAssignedLocations()
    {
        Seed seed = await SeedAsync("q10");
        LocationId storeB = await factory.CreateLocationAsync($"ST-{seed.Suffix}b", $"Store B {seed.Suffix}", LocationKind.Store);
        await factory.CreateUserAsync("q10-a", Roles.StoreManager, locations: [seed.Store]);
        await factory.CreateUserAsync("q10-b", Roles.StoreManager, locations: [storeB]);
        await factory.CreateUserAsync("q10-audit", Roles.Auditor);
        using HttpClient client = factory.CreateClient();
        string managerA = await SignInAsync(client, "q10-a");
        string managerB = await SignInAsync(client, "q10-b");
        string auditor = await SignInAsync(client, "q10-audit");

        Guid incidentAtA = await CreateIncidentAsync(
            client, managerA, seed.Store, [new { barcode = seed.PlainBarcode, quantity = 2m }]);
        Guid incidentAtB = await CreateIncidentAsync(
            client, managerB, storeB, [new { barcode = seed.BatchBarcode, quantity = 2m, unitCost = 99m }]);

        // A store manager cannot raise finds at somebody else's store.
        using (HttpResponseMessage crossStore = await PostAsJsonAsync(
            client,
            "/api/v1/quarantine",
            new { locationId = storeB.Value, lines = new[] { new { barcode = seed.PlainBarcode, quantity = 1m } } },
            managerA))
        {
            crossStore.StatusCode.Should().Be(HttpStatusCode.Forbidden, await crossStore.Content.ReadAsStringAsync());
        }

        // The list is scoped to assigned locations; the detail refuses incidents
        // outside them.
        using (HttpResponseMessage list = await GetAsync(client, "/api/v1/quarantine", managerA))
        {
            list.StatusCode.Should().Be(HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
            using JsonDocument document = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            document.RootElement.EnumerateArray()
                .Select(e => e.GetProperty("id").GetGuid())
                .Should().Equal(incidentAtA);
        }

        using (HttpResponseMessage ownDetail = await GetAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentAtA}"),
            managerA))
        {
            ownDetail.StatusCode.Should().Be(HttpStatusCode.OK, await ownDetail.Content.ReadAsStringAsync());
        }

        using (HttpResponseMessage outside = await GetAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentAtB}"),
            managerA))
        {
            outside.StatusCode.Should().Be(HttpStatusCode.Forbidden, await outside.Content.ReadAsStringAsync());
            (await ReadErrorCodeAsync(outside)).Should().Be("quarantine.outside_scope");
        }

        // Read-only auditors with business-wide scope see everything.
        using (HttpResponseMessage auditList = await GetAsync(client, "/api/v1/quarantine", auditor))
        {
            auditList.StatusCode.Should().Be(HttpStatusCode.OK, await auditList.Content.ReadAsStringAsync());
            using JsonDocument document = JsonDocument.Parse(await auditList.Content.ReadAsStringAsync());
            document.RootElement.EnumerateArray()
                .Select(e => e.GetProperty("id").GetGuid())
                .Should().Contain([incidentAtA, incidentAtB]);
        }
    }

    // ---- Seeds ---------------------------------------------------------------

    private async Task<Seed> SeedAsync(string suffix)
    {
        SupplierId supplier = await factory.CreateSupplierAsync($"SUP-{suffix}", $"Supplier {suffix}");
        LocationId store = await factory.CreateLocationAsync($"ST-{suffix}", $"Store {suffix}", LocationKind.Store);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalSupplier);
        await factory.CreateExternalLocationAsync(SystemLocationCodes.ExternalWriteOff);

        string plainBarcode = NewBarcode();
        string batchBarcode = NewBarcode();
        ProductId plain = await factory.CreateProductAsync(
            $"QP-{suffix}", $"Plain {suffix}", plainBarcode, defaultPurchaseCost: 95m);
        ProductId batched = await factory.CreateProductAsync(
            $"QB-{suffix}", $"Batch {suffix}", batchBarcode, tracksBatches: true, defaultPurchaseCost: 80m);

        return new Seed(supplier, store, plain, batched, plainBarcode, batchBarcode, suffix);
    }

    private static string NewBarcode()
        => Interlocked.Increment(ref _barcodeSequence).ToString(CultureInfo.InvariantCulture);

    private Task<bool> DenyAsync(string userName, string permissionCode)
        => factory.WithServiceAsync(async context =>
        {
            UserId userId = await UserIdAsync(userName);

            UserPermissionOverride? existing = await context.UserPermissionOverrides
                .FirstOrDefaultAsync(o => o.UserId == userId && o.PermissionCode == permissionCode);

            if (existing is null)
            {
                context.UserPermissionOverrides.Add(UserPermissionOverride.Create(
                    userId,
                    permissionCode,
                    PermissionEffect.Deny,
                    DateTimeOffset.UtcNow,
                    userId,
                    "Denied for the quarantine write-off gate test.").Value);
                await context.SaveChangesAsync();

                // The database-backed evaluator caches authorization per policy
                // version, so the deny must bump it before it takes effect.
                await factory.WithServiceAsync<IPolicyVersionProvider>(async policyVersion =>
                    await policyVersion.BumpAsync("test deny override", CancellationToken.None));
            }

            return true;
        });

    private async Task<UserId> UserIdAsync(string userName)
        => await factory.WithServiceAsync(async context =>
        {
            AppUser? user = await context.Users.SingleAsync(u => u.UserName == userName);
            return new UserId(user.Id);
        });

    // ---- Request helpers -----------------------------------------------------

    private static async Task<Guid> CreateIncidentAsync(
        HttpClient client, string accessToken, LocationId locationId,
        IReadOnlyList<object> lines, string? note = null)
    {
        using HttpResponseMessage response = await PostAsJsonAsync(
            client,
            "/api/v1/quarantine",
            new { locationId = locationId.Value, lines, note },
            accessToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<JsonDocument> GetIncidentDetailAsync(
        HttpClient client, string accessToken, Guid incidentId)
    {
        using HttpResponseMessage response = await GetAsync(
            client,
            FormattableString.Invariant($"/api/v1/quarantine/{incidentId}"),
            accessToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    // ---- Read helpers --------------------------------------------------------

    private async Task<List<MovementSnapshot>> MovementsByReferenceAsync(Guid referenceDocumentId)
        => await factory.WithServiceAsync(context => context.InventoryMovements
            .Where(m => m.ReferenceDocumentId == referenceDocumentId)
            .OrderBy(m => m.LocationId)
            .Select(m => new MovementSnapshot(
                m.QuantityDelta,
                m.State,
                m.MovementType,
                m.ApprovedByUserId != null ? m.ApprovedByUserId.Value.Value : null,
                m.ReasonCode,
                m.BatchId != null ? m.BatchId.Value.Value : (Guid?)null,
                m.LocationId.Value))
            .ToListAsync());

    private async Task<decimal> BalanceAsync(
        LocationId locationId, ProductId productId, BatchId? batchId, InventoryState state)
        => await factory.WithServiceAsync(async context =>
        {
            InventoryBalance? balance = await context.InventoryBalances
                .AsNoTracking()
                .FirstOrDefaultAsync(b =>
                    b.LocationId == locationId
                    && b.ProductId == productId
                    && b.BatchKey == (batchId ?? BatchId.Empty)
                    && b.State == state);

            return balance?.Quantity ?? 0m;
        });

    private async Task<decimal> AverageCostAsync(
        LocationId locationId, ProductId productId, BatchId? batchId, InventoryState state)
        => await factory.WithServiceAsync(async context =>
        {
            InventoryBalance? balance = await context.InventoryBalances
                .AsNoTracking()
                .FirstOrDefaultAsync(b =>
                    b.LocationId == locationId
                    && b.ProductId == productId
                    && b.BatchKey == (batchId ?? BatchId.Empty)
                    && b.State == state);

            return balance?.AverageUnitCost ?? 0m;
        });

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await client.SendAsync(request, CancellationToken.None);
    }

    private static async Task<HttpResponseMessage> PostAsJsonAsync(
        HttpClient client, string path, object body, string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.TryGetProperty("errorCode", out JsonElement code)
            ? code.GetString()
            : null;
    }

    private static async Task<string> SignInAsync(HttpClient client, string userName)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName, password = PosApiFactory.TestPassword },
            CancellationToken.None);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(CancellationToken.None));

        return document.RootElement.GetProperty("accessToken").GetString()!;
    }

    // ---- DTOs -----------------------------------------------------------------

    private sealed record MovementSnapshot(
        decimal QuantityDelta,
        InventoryState State,
        InventoryMovementType MovementType,
        Guid? ApprovedByUserId,
        AdjustmentReasonCode? ReasonCode,
        Guid? BatchId,
        Guid LocationId);

    private sealed record Seed(
        SupplierId Supplier,
        LocationId Store,
        ProductId Plain,
        ProductId Batched,
        string PlainBarcode,
        string BatchBarcode,
        string Suffix);
}