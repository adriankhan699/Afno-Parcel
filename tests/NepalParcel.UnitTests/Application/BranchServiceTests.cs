using Microsoft.Extensions.Logging.Abstractions;
using NepalParcel.Application.Branches;
using NepalParcel.Application.Common.Exceptions;
using NepalParcel.Application.Common.Interfaces;
using NepalParcel.Application.Common.Validation;
using NepalParcel.Contracts.Common;
using NepalParcel.Domain.Branches;
using NSubstitute;

namespace NepalParcel.UnitTests.Application;

public sealed class BranchServiceTests
{
    private readonly IBranchRepository _repository = Substitute.For<IBranchRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly BranchService _sut;

    public BranchServiceTests()
    {
        _sut = new BranchService(
            _repository,
            _unitOfWork,
            new CreateBranchRequestValidator(),
            new PageQueryValidator(),
            NullLogger<BranchService>.Instance);
    }

    [Fact]
    public async Task CreateAsync_ValidRequest_AddsAndSaves()
    {
        var response = await _sut.CreateAsync(TestData.ValidCreateBranchRequest("ktm02"));

        Assert.Equal("KTM02", response.Code);
        Assert.Equal("Bagmati", response.Address.Province);
        _repository.Received(1).Add(Arg.Is<Branch>(b => b.Code == "KTM02" && b.Type == BranchType.Branch));
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_DuplicateCode_ThrowsConflict_AndDoesNotSave()
    {
        _repository.CodeExistsAsync("KTM01", Arg.Any<CancellationToken>()).Returns(true);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.CreateAsync(TestData.ValidCreateBranchRequest("ktm01")));

        _repository.DidNotReceive().Add(Arg.Any<Branch>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_InvalidRequest_ThrowsValidationWithErrors()
    {
        var request = TestData.ValidCreateBranchRequest() with { PhoneNumber = "abc" };

        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => _sut.CreateAsync(request));

        Assert.True(ex.Errors.ContainsKey("PhoneNumber"));
    }

    [Fact]
    public async Task GetByIdAsync_Missing_ThrowsNotFound()
    {
        _repository.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Branch?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.GetByIdAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetPageAsync_MapsItemsAndPaging()
    {
        var branch = Branch.Create("BRT01", "Biratnagar", BranchType.Branch, TestData.KathmanduAddress(), "9852000000");
        _repository.GetPageAsync(2, 10, Arg.Any<CancellationToken>()).Returns(([branch], 11));

        var page = await _sut.GetPageAsync(new PageQuery { Page = 2, PageSize = 10 });

        Assert.Single(page.Items);
        Assert.Equal(11, page.TotalCount);
        Assert.Equal(2, page.TotalPages);
        Assert.False(page.HasNextPage);
    }

    [Fact]
    public async Task GetPageAsync_PageSizeTooLarge_ThrowsValidation()
    {
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            _sut.GetPageAsync(new PageQuery { Page = 1, PageSize = PageQuery.MaxPageSize + 1 }));
    }
}
