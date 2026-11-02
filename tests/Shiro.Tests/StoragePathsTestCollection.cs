using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Shiro.Tests
{
    [CollectionDefinition("StoragePathsCollection", DisableParallelization = true)]
    public class StoragePathsTestCollection
    {
    }
}
