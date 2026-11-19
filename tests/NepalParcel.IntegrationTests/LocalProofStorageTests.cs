using Microsoft.Extensions.Options;
using NepalParcel.Application.Delivery;
using NepalParcel.Infrastructure.Storage;

namespace NepalParcel.IntegrationTests;

public sealed class LocalProofStorageTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "afno-storage-test-" + Guid.NewGuid().ToString("N"));
    private readonly LocalProofStorage _sut;

    public LocalProofStorageTests()
    {
        _sut = new LocalProofStorage(Options.Create(new DeliveryOptions { ProofStoragePath = _folder }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task AFile_CanBeSaved_ReadBack_AndDeleted()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };

        var key = await _sut.SaveAsync(bytes, "png");
        await using (var stream = await _sut.OpenAsync(key))
        {
            Assert.NotNull(stream);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
        }

        await _sut.DeleteAsync(key);
        Assert.Null(await _sut.OpenAsync(key));
    }

    [Fact]
    public async Task EveryFile_GetsANewRandomName_InsideTheStorageFolder()
    {
        var first = await _sut.SaveAsync(new byte[] { 1 }, "jpg");
        var second = await _sut.SaveAsync(new byte[] { 1 }, "jpg");

        Assert.NotEqual(first, second);
        Assert.Matches(@"^\d{4}/\d{2}/[0-9a-f]{32}\.jpg$", first);
        Assert.True(File.Exists(Path.Combine(_folder, first.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Theory]
    [InlineData("exe")]
    [InlineData("svg")]
    [InlineData("html")]
    [InlineData("PNG")]
    [InlineData("")]
    [InlineData("png/../x")]
    public async Task OnlyPicturesOfTheKnownKindsCanBeStored(string extension)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.SaveAsync(new byte[] { 1 }, extension));
    }

    [Theory]
    [InlineData("../secret.png")]
    [InlineData("2026/11/../../../secret.png")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("2026/11/abc.png")]
    [InlineData("2026/11/0123456789abcdef0123456789abcdef.exe")]
    [InlineData("2026/11/0123456789abcdef0123456789abcdef.png/../../x")]
    [InlineData("")]
    public async Task AKeyThatIsNotOneWeIssued_IsRefused_ForReadingAndDeleting(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.OpenAsync(key));
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.DeleteAsync(key));
    }

    [Fact]
    public async Task AWellFormedKeyThatDoesNotExist_IsSimplyNotFound()
    {
        Assert.Null(await _sut.OpenAsync("2026/11/0123456789abcdef0123456789abcdef.png"));
        await _sut.DeleteAsync("2026/11/0123456789abcdef0123456789abcdef.png");
    }
}
