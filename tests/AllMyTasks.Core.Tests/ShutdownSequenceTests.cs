using AllMyTasks.Core.Startup;
using Microsoft.Extensions.Logging.Abstractions;

namespace AllMyTasks.Core.Tests;

public class ShutdownSequenceTests
{
    [Fact]
    public void Steps_run_in_order()
    {
        var order = new List<string>();
        var sequence = new ShutdownSequence();
        sequence.Add("a", () => order.Add("a"));
        sequence.Add("b", () => order.Add("b"));

        sequence.Run(NullLogger.Instance);

        Assert.Equal(["a", "b"], order);
    }

    [Fact]
    public void A_failing_step_does_not_stop_the_others_or_throw()
    {
        var ran = new List<string>();
        var sequence = new ShutdownSequence();
        sequence.Add("first", () => ran.Add("first"));
        sequence.Add("broken", () => throw new ObjectDisposedException("x"));
        sequence.Add("last", () => ran.Add("last"));

        sequence.Run(NullLogger.Instance);

        Assert.Equal(["first", "last"], ran);
    }

    [Fact]
    public void Running_twice_runs_each_step_once_and_marks_the_start()
    {
        var count = 0;
        var sequence = new ShutdownSequence();
        sequence.Add("count", () => count++);
        Assert.False(sequence.HasStarted);

        sequence.Run(NullLogger.Instance);
        sequence.Run(NullLogger.Instance);

        Assert.Equal(1, count);
        Assert.True(sequence.HasStarted);
    }
}
