using Garou.Domain.Enums;
using Xunit;

namespace Garou.Tests.Domain;

public sealed class MediaTypeTests
{
    [Fact]
    public void MediaType_HasSpecifiedValues()
    {
        Assert.Equal(0, (int)MediaType.Unknown);
        Assert.Equal(1, (int)MediaType.Image);
        Assert.Equal(2, (int)MediaType.Video);
        Assert.Equal(3, (int)MediaType.Audio);
    }

    [Fact]
    public void MediaType_DoesNotContainDocument()
    {
        Assert.DoesNotContain("Document", Enum.GetNames<MediaType>());
    }
}
