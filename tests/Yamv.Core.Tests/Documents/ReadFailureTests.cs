using Yamv.Core.Documents;

namespace Yamv.Core.Tests.Documents;

public sealed class ReadFailureTests
{
    [Fact]
    public void ReasonFor_AccessDenied_IsAccessIsDenied() =>
        Assert.Equal("Access is denied.", ReadFailure.ReasonFor(new UnauthorizedAccessException("The file is not yours.")));

    [Theory]
    [InlineData(unchecked((int)0x80070020))]
    [InlineData(unchecked((int)0x80070021))]
    public void ReasonFor_SharingOrLockViolation_IsInUseByAnotherProgram(int hresult) =>
        Assert.Equal("It is in use by another program.", ReadFailure.ReasonFor(new IOException("The file is locked.", hresult)));

    [Fact]
    public void ReasonFor_OtherIoFailure_IsTheGenericReason() =>
        Assert.Equal("It could not be read.", ReadFailure.ReasonFor(new IOException("The disk went away.")));
}
