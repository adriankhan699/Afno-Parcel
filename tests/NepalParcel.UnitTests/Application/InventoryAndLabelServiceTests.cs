using System.Text;
using Microsoft.Extensions.Options;
using NepalParcel.Application.Auth;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Network;
using NepalParcel.Application.Parcels;
using NepalParcel.Application.Tracking;
using NepalParcel.Contracts.Common;
using NepalParcel.Contracts.Warehouses;
using NepalParcel.Domain.Customers;
using NepalParcel.Domain.Identity;
using NepalParcel.Domain.Parcels;
using NSubstitute;

namespace NepalParcel.UnitTests.Application;

public sealed class InventoryServiceTests
{
    private readonly NetworkSetup _net = new();
    private readonly InventoryService _sut;

    public InventoryServiceTests()
    {
        _sut = new InventoryService(_net.Inventory, _net.Warehouses, _net.Clock, new WaitingQueryValidator());
    }

    private InventoryRow Row(string tracking, ParcelStatusCode status, double hoursAgo) =>
        new(Guid.NewGuid(), tracking, (int)status, ServiceType.Standard, 2.5m, 2, "Kaski", _net.Clock.GetUtcNow().AddHours(-hoursAgo));

    [Fact]
    public async Task WarehouseInventory_ListsWhatIsHeldAndWhatIsInbound_WithWaitingTimeAndUtilisation()
    {
        var hub = _net.Hub2; // capacity 200
        _net.Inventory.GetHeldAsync(hub.Id, "AFN", 1, 20, Arg.Any<CancellationToken>())
            .Returns((new List<InventoryRow> { Row("AFNAAAAAAAAAAB", ParcelStatusCode.ArrivedAtWarehouse, 26.25) }, 57));
        _net.Inventory.GetInboundAsync(hub.Id, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<InventoryRow> { Row("AFNBBBBBBBBBBC", ParcelStatusCode.InTransit, 3) });
        _net.Inventory.CountHeldByWarehouseAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [hub.Id] = 57 });
        _net.Inventory.CountInboundByWarehouseAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [hub.Id] = 1 });

        var inventory = await _sut.GetWarehouseInventoryAsync(hub.Id, " AFN ", 1, 20);

        Assert.Equal("Kathmandu East Hub", inventory.WarehouseName);
        Assert.Equal(57, inventory.ParcelsHeld);
        Assert.Equal(1, inventory.ParcelsInbound);
        Assert.Equal(28.5m, inventory.UtilisationPercent); // 57 of 200
        Assert.Equal(57, inventory.Held.TotalCount);
        var held = Assert.Single(inventory.Held.Items);
        Assert.Equal("ArrivedAtWarehouse", held.Status);
        Assert.Equal(26.3m, held.WaitingHours);
        var inbound = Assert.Single(inventory.Inbound);
        Assert.Equal("InTransit", inbound.Status);
        Assert.Equal(3m, inbound.WaitingHours);
    }

    [Fact]
    public async Task WarehouseInventory_WithoutACapacity_HasNoUtilisation()
    {
        _net.Inventory.GetHeldAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(([], 0));
        _net.Inventory.GetInboundAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<InventoryRow>());

        var inventory = await _sut.GetWarehouseInventoryAsync(_net.Hub.Id, null, 1, 20);

        Assert.Null(inventory.CapacityParcels);
        Assert.Null(inventory.UtilisationPercent);
        Assert.Empty(inventory.Held.Items);
    }

    [Fact]
    public async Task WarehouseInventory_UnknownWarehouse_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetWarehouseInventoryAsync(Guid.NewGuid(), null, 1, 20));
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, PageQuery.MaxPageSize + 1)]
    public async Task WarehouseInventory_BadPaging_FailsValidation(int page, int pageSize)
    {
        await Assert.ThrowsAsync<RequestValidationException>(() => _sut.GetWarehouseInventoryAsync(_net.Hub.Id, null, page, pageSize));
    }

    [Fact]
    public async Task Waiting_SeparatesParcelsWaitingForPickupFromThoseWaitingForDispatch()
    {
        _net.Inventory.GetWaitingAsync(Arg.Any<WaitingFilter>(), 1, 20, Arg.Any<CancellationToken>())
            .Returns((new List<WaitingRow>
            {
                new(Guid.NewGuid(), "AFNAAAAAAAAAAB", (int)ParcelStatusCode.BookingCreated, "Kathmandu Main", ServiceType.Express, 1m, 1, "Kaski", _net.Clock.GetUtcNow().AddHours(-5)),
                new(Guid.NewGuid(), "AFNBBBBBBBBBBC", (int)ParcelStatusCode.PickedUp, "Kathmandu Main", ServiceType.Standard, 2m, 1, "Kaski", _net.Clock.GetUtcNow().AddHours(-2.5)),
                new(Guid.NewGuid(), "AFNCCCCCCCCCCD", (int)ParcelStatusCode.ArrivedAtWarehouse, "Kathmandu Hub", ServiceType.Standard, 2m, 1, "Kaski", _net.Clock.GetUtcNow().AddHours(-1)),
            }, 3));

        var page = await _sut.GetWaitingAsync(new WaitingQuery { Kind = "pickup", BranchId = _net.Kathmandu.Id });

        Assert.Equal(["Pickup", "Dispatch", "Dispatch"], page.Items.Select(i => i.Kind));
        Assert.Equal(5m, page.Items[0].WaitingHours);
        Assert.Equal(2.5m, page.Items[1].WaitingHours);
        Assert.Equal("Kathmandu Hub", page.Items[2].Location);
        await _net.Inventory.Received(1).GetWaitingAsync(
            Arg.Is<WaitingFilter>(f => f.Kind == WaitingKind.Pickup && f.BranchId == _net.Kathmandu.Id && f.WarehouseId == null),
            1,
            20,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Waiting_UnknownKind_FailsValidation()
    {
        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => _sut.GetWaitingAsync(new WaitingQuery { Kind = "Someday" }));

        Assert.Contains("Kind", ex.Errors.Keys);
    }
}

public sealed class ParcelLabelServiceTests
{
    private readonly IParcelRepository _parcels = Substitute.For<IParcelRepository>();
    private readonly ICustomerRepository _customers = Substitute.For<ICustomerRepository>();
    private readonly IBranchRepository _branches = Substitute.For<IBranchRepository>();
    private readonly ILabelGenerator _generator = Substitute.For<ILabelGenerator>();
    private readonly NetworkSetup _net = new();
    private readonly ParcelLabelService _sut;
    private readonly Parcel _parcel;

    public ParcelLabelServiceTests()
    {
        _parcel = TestData.NewParcel();
        _parcels.GetByIdAsync(_parcel.Id, Arg.Any<CancellationToken>()).Returns(_parcel);
        _generator.CreateBarcodeSvg(Arg.Any<string>()).Returns(call => $"<svg>barcode:{call.Arg<string>()}</svg>");
        _generator.CreateQrSvg(Arg.Any<string>()).Returns(call => $"<svg>qr:{call.Arg<string>()}</svg>");

        _sut = new ParcelLabelService(
            _parcels,
            _customers,
            _branches,
            _generator,
            _net.CurrentUser,
            Options.Create(new TrackingOptions { PublicBaseUrl = "https://afno.example/" }));
    }

    private static string Decode(string dataUri)
    {
        const string prefix = "data:image/svg+xml;base64,";
        Assert.StartsWith(prefix, dataUri, StringComparison.Ordinal);
        return Encoding.UTF8.GetString(Convert.FromBase64String(dataUri[prefix.Length..]));
    }

    [Fact]
    public async Task Staff_GetALabelWithTheBarcodeOfTheTrackingNumber_AndAQrCodeOfTheTrackingLink()
    {
        _net.SignInAs(Roles.WarehouseStaff);

        var label = await _sut.GetLabelAsync(_parcel.Id);

        Assert.Equal(_parcel.TrackingNumber, label.TrackingNumber);
        Assert.Equal($"https://afno.example/track/{_parcel.TrackingNumber}", label.QrContent);
        Assert.Equal($"<svg>barcode:{_parcel.TrackingNumber}</svg>", Decode(label.BarcodeDataUri));
        Assert.Equal($"<svg>qr:{label.QrContent}</svg>", Decode(label.QrDataUri));
        Assert.Equal("Standard", label.ServiceType);
        Assert.Equal(_parcel.ReceiverName, label.ReceiverName);
        Assert.Equal(_parcel.Quantity, label.Quantity);
    }

    [Fact]
    public async Task TheLabel_DoesNotCarryThePhoneNumbersOrTheStreetAddress()
    {
        _net.SignInAs(Roles.BookingStaff);

        var label = await _sut.GetLabelAsync(_parcel.Id);

        var text = System.Text.Json.JsonSerializer.Serialize(label);
        Assert.DoesNotContain(_parcel.ReceiverPhone, text, StringComparison.Ordinal);
        Assert.DoesNotContain(_parcel.SenderPhone, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSender_CanPrintTheirOwnLabel_ButNobodyElseCan()
    {
        _net.SignInAs(Roles.Customer);
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var ownerCustomer = Customer.Create("Ram Sharma", "9841234567", userId: owner);
        var parcel = Parcel.Book(TestData.ParcelDetails() with { SenderCustomerId = ownerCustomer.Id }, TestData.Now);
        _parcels.GetByIdAsync(parcel.Id, Arg.Any<CancellationToken>()).Returns(parcel);
        _customers.GetByUserIdAsync(owner, Arg.Any<CancellationToken>()).Returns(ownerCustomer);
        _customers.GetByUserIdAsync(stranger, Arg.Any<CancellationToken>()).Returns(Customer.Create("Someone Else", "9801234567", userId: stranger));

        _net.CurrentUser.UserId.Returns(owner);
        var label = await _sut.GetLabelAsync(parcel.Id);
        Assert.Equal(parcel.TrackingNumber, label.TrackingNumber);

        _net.CurrentUser.UserId.Returns(stranger);
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetLabelAsync(parcel.Id));
    }

    [Fact]
    public async Task UnknownParcel_IsNotFound()
    {
        _net.SignInAs(Roles.SuperAdmin);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetLabelAsync(Guid.NewGuid()));
    }
}
