using Xunit;

namespace Shiro.Tests.TestInfrastructure
{
    [CollectionDefinition("StaticSeams", DisableParallelization = true)]
    public sealed class StaticSeamsCollection
    {
    }

    [CollectionDefinition("SerialGlobalState", DisableParallelization = true)]
    public sealed class SerialGlobalStateCollection
    {
    }

    [CollectionDefinition("SequentialStateTests", DisableParallelization = true)]
    public sealed class SequentialStateTestsCollection
    {
    }
}
