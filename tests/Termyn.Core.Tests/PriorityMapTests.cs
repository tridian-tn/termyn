using Termyn.Core.Model;

namespace Termyn.Core.Tests;

public class PriorityMapTests
{
    [Theory]
    [InlineData(4, Priority.P1)]
    [InlineData(3, Priority.P2)]
    [InlineData(2, Priority.P3)]
    [InlineData(1, Priority.P4)]
    public void FromApi_inverts_priority(int apiPriority, Priority expected)
        => Assert.Equal(expected, PriorityMap.FromApi(apiPriority));

    [Theory]
    [InlineData(Priority.P1, 4)]
    [InlineData(Priority.P2, 3)]
    [InlineData(Priority.P3, 2)]
    [InlineData(Priority.P4, 1)]
    public void ToApi_inverts_priority(Priority priority, int expected)
        => Assert.Equal(expected, PriorityMap.ToApi(priority));

    [Theory]
    [InlineData(Priority.P1)]
    [InlineData(Priority.P2)]
    [InlineData(Priority.P3)]
    [InlineData(Priority.P4)]
    public void Round_trip_is_stable(Priority priority)
        => Assert.Equal(priority, PriorityMap.FromApi(PriorityMap.ToApi(priority)));
}
