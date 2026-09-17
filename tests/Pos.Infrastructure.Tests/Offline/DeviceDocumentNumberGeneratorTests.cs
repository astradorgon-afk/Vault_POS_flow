using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Pos.Domain.Common;
using Pos.Infrastructure.Offline;

namespace Pos.Infrastructure.Tests.Offline;

/// <summary>
/// Device-scoped numbering. A number printed on an offline receipt is the one
/// the sale keeps, so these cover the two ways that can go wrong: the same
/// number twice, and a number a device had no right to mint.
/// </summary>
public sealed class DeviceDocumentNumberGeneratorTests
{
    [Fact]
    public async Task NextAsync_NumbersFromOne_AndCarriesThisDevicesShortCode()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        await database.EnrolAsync("D03");

        (DeviceDocumentNumberGenerator generator, PosDeviceDbContext context) = await database.OpenGeneratorAsync();
        await using PosDeviceDbContext _ = context;

        DocumentNumber first = await generator.NextAsync(DocumentType.Sale, CancellationToken.None);
        DocumentNumber second = await generator.NextAsync(DocumentType.Sale, CancellationToken.None);

        first.Value.Should().Be("SAL-2026-D03-000001");
        second.Value.Should().Be("SAL-2026-D03-000002");
        first.DeviceShortCode.Should().Be("D03");
    }

    [Fact]
    public async Task NextAsync_KeepsASeparateSequencePerDocumentType()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        await database.EnrolAsync("D03");

        (DeviceDocumentNumberGenerator generator, PosDeviceDbContext context) = await database.OpenGeneratorAsync();
        await using PosDeviceDbContext _ = context;

        await generator.NextAsync(DocumentType.Sale, CancellationToken.None);
        DocumentNumber sale = await generator.NextAsync(DocumentType.Sale, CancellationToken.None);
        DocumentNumber shift = await generator.NextAsync(DocumentType.CashierShift, CancellationToken.None);
        DocumentNumber salesReturn = await generator.NextAsync(DocumentType.SalesReturn, CancellationToken.None);

        sale.Value.Should().Be("SAL-2026-D03-000002");
        shift.Value.Should().Be("SHF-2026-D03-0001", "a shift number is four digits, not six");
        salesReturn.Value.Should().Be("RET-2026-D03-000001");
    }

    [Fact]
    public async Task Numbers_SurviveARestart_AndNeverRepeat()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        await database.EnrolAsync("D03");

        List<string> issued = [];

        for (int round = 0; round < 3; round++)
        {
            // A fresh context and a fresh accessor each round: the counter lives
            // in the database, not in the process that allocated the last one.
            (DeviceDocumentNumberGenerator generator, PosDeviceDbContext context) =
                await database.OpenGeneratorAsync();
            await using PosDeviceDbContext _ = context;

            issued.Add((await generator.NextAsync(DocumentType.Sale, CancellationToken.None)).Value);
            issued.Add((await generator.NextAsync(DocumentType.Sale, CancellationToken.None)).Value);
        }

        issued.Should().OnlyHaveUniqueItems();
        issued[^1].Should().Be("SAL-2026-D03-000006");
    }

    [Fact]
    public async Task ConcurrentAllocations_NeverHandOutTheSameNumber()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        await database.EnrolAsync("D03");

        const int parallel = 12;
        Task<DocumentNumber>[] allocations = new Task<DocumentNumber>[parallel];

        for (int i = 0; i < parallel; i++)
        {
            allocations[i] = Task.Run(async () =>
            {
                (DeviceDocumentNumberGenerator generator, PosDeviceDbContext context) =
                    await database.OpenGeneratorAsync();
                await using PosDeviceDbContext _ = context;
                return await generator.NextAsync(DocumentType.Sale, CancellationToken.None);
            });
        }

        DocumentNumber[] numbers = await Task.WhenAll(allocations);

        numbers.Select(n => n.Value).Should().OnlyHaveUniqueItems().And.HaveCount(parallel);
    }

    [Fact]
    public async Task ARolledBackTransaction_ReleasesTheNumberItTook()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        await database.EnrolAsync("D03");

        (DeviceDocumentNumberGenerator generator, PosDeviceDbContext context) = await database.OpenGeneratorAsync();
        await using PosDeviceDbContext _ = context;

        await using (var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None))
        {
            DocumentNumber abandoned = await generator.NextAsync(DocumentType.Sale, CancellationToken.None);
            abandoned.Value.Should().Be("SAL-2026-D03-000001");
            await transaction.RollbackAsync(CancellationToken.None);
        }

        DocumentNumber reused = await generator.NextAsync(DocumentType.Sale, CancellationToken.None);

        reused.Value.Should().Be("SAL-2026-D03-000001", "the allocation joined the caller's transaction");
    }

    [Fact]
    public async Task ACentralDocumentType_IsRefused()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        await database.EnrolAsync("D03");

        (DeviceDocumentNumberGenerator generator, PosDeviceDbContext context) = await database.OpenGeneratorAsync();
        await using PosDeviceDbContext _ = context;

        Func<Task> allocate = () => generator.NextAsync(DocumentType.PurchaseOrder, CancellationToken.None);

        await allocate.Should().ThrowAsync<NotSupportedException>(
            "a device never mints a number the server owns");
    }

    [Fact]
    public async Task AnotherDevicesShortCode_IsRefused()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        await database.EnrolAsync("D03");

        (DeviceDocumentNumberGenerator generator, PosDeviceDbContext context) = await database.OpenGeneratorAsync();
        await using PosDeviceDbContext _ = context;

        Func<Task> allocate = () => generator.NextScopedAsync(DocumentType.Sale, "D07", CancellationToken.None);

        await allocate.Should().ThrowAsync<InvalidOperationException>(
            "minting under another device's code would collide with that device's own sequence");
    }

    [Fact]
    public async Task ItsOwnShortCode_IsAccepted_WhateverTheCasing()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        await database.EnrolAsync("D03");

        (DeviceDocumentNumberGenerator generator, PosDeviceDbContext context) = await database.OpenGeneratorAsync();
        await using PosDeviceDbContext _ = context;

        DocumentNumber number = await generator.NextScopedAsync(DocumentType.Sale, " d03 ", CancellationToken.None);

        number.Value.Should().Be("SAL-2026-D03-000001");
    }

    [Fact]
    public async Task AnUnenrolledDevice_CannotNumberAnything()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();

        (DeviceDocumentNumberGenerator generator, PosDeviceDbContext context) = await database.OpenGeneratorAsync();
        await using PosDeviceDbContext _ = context;

        Func<Task> allocate = () => generator.NextAsync(DocumentType.Sale, CancellationToken.None);

        await allocate.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task TheYearRollingOver_RestartsTheSequence()
    {
        await using TemporaryDeviceDatabase database = await TemporaryDeviceDatabase.CreateAsync();
        await database.EnrolAsync("D03");

        (DeviceDocumentNumberGenerator generator, PosDeviceDbContext context) = await database.OpenGeneratorAsync();
        await using PosDeviceDbContext _ = context;

        await generator.NextAsync(DocumentType.Sale, CancellationToken.None);
        database.Clock.UtcNow = new DateTimeOffset(2027, 1, 1, 0, 30, 0, TimeSpan.Zero);

        DocumentNumber newYear = await generator.NextAsync(DocumentType.Sale, CancellationToken.None);

        newYear.Value.Should().Be("SAL-2027-D03-000001");

        await using PosDeviceDbContext counters = await database.OpenContextAsync();
        (await counters.DocumentCounters.CountAsync(CancellationToken.None))
            .Should().Be(2, "each period keeps its own counter row");
    }
}
