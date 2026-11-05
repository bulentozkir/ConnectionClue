using ConnectionClue.App;
using static ConnectionClue.Windows.IntegrationTests.Fx;

namespace ConnectionClue.Windows.IntegrationTests;

/// <summary>
/// The single-instance gate with its real kernel objects, under a private name so that a copy of the app running on the
/// machine that runs the tests is never signalled. A mutex belongs to the thread that took it, so a second launch runs on a
/// thread of its own, as it would in a process of its own.
/// </summary>
public class SingleInstanceGateTests
{
    // These names are what a newer and an older copy of the app find each other by.
    private const string Objects = "Local\\ConnectionClue-";

    private static string NewName() => "test-" + Guid.NewGuid().ToString("N");

    private static T OnOtherThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception e) { failure = e; }
        });
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("The launch threw.", failure);
        return result;
    }

    /// <summary>A later launch: whether it became the running copy, and whether it handed over to one.</summary>
    private static (bool Started, bool HandedOver) Launch(string name, bool quickCheck) => OnOtherThread(() =>
    {
        using var gate = SingleInstanceGate.Acquire(name, quickCheck, out bool handedOver);
        return (gate is not null, handedOver);
    });

    [Fact]
    public void A_launch_while_a_copy_is_running_hands_over_to_it_and_does_not_start()
    {
        string name = NewName();
        using var running = SingleInstanceGate.Acquire(name, quickCheck: false, out bool firstHandedOver);
        Assert.NotNull(running);
        Assert.False(firstHandedOver);
        using var asked = new ManualResetEventSlim();
        bool? quick = null;
        running.ActivationRequested += (_, q) => { quick = q; asked.Set(); };
        running.Listen();

        var (started, handedOver) = Launch(name, quickCheck: false);

        Assert.False(started);
        Assert.True(handedOver);
        Assert.True(asked.Wait(TimeSpan.FromSeconds(10), Ct), "The running copy was never asked to show its window.");
        Assert.False(quick);
    }

    [Fact]
    public void A_quick_check_launch_asks_the_running_copy_for_a_check()
    {
        string name = NewName();
        using var running = SingleInstanceGate.Acquire(name, quickCheck: false, out _);
        Assert.NotNull(running);
        using var asked = new ManualResetEventSlim();
        bool? quick = null;
        running.ActivationRequested += (_, q) => { quick = q; asked.Set(); };
        running.Listen();

        var (started, handedOver) = Launch(name, quickCheck: true);

        Assert.False(started);
        Assert.True(handedOver);
        Assert.True(asked.Wait(TimeSpan.FromSeconds(10), Ct), "The running copy was never asked to run a check.");
        Assert.True(quick);
    }

    [Fact]
    public void A_request_made_before_the_running_copy_listens_is_not_lost()
    {
        // Start-up takes seconds: a launch in that time must still get its window once the copy listens.
        string name = NewName();
        using var running = SingleInstanceGate.Acquire(name, quickCheck: false, out _);
        Assert.NotNull(running);
        Assert.True(Launch(name, quickCheck: false).HandedOver);
        using var asked = new ManualResetEventSlim();
        running.ActivationRequested += (_, _) => asked.Set();

        running.Listen();

        Assert.True(asked.Wait(TimeSpan.FromSeconds(10), Ct), "The early request was dropped.");
    }

    [Fact]
    public void A_copy_that_closed_leaves_the_lock_free_for_the_next_launch()
    {
        string name = NewName();
        SingleInstanceGate.Acquire(name, quickCheck: false, out _)!.Dispose();

        using var next = SingleInstanceGate.Acquire(name, quickCheck: false, out bool handedOver);

        Assert.NotNull(next);
        Assert.False(handedOver);
    }

    [Fact]
    public void A_launch_that_gets_no_answer_from_the_holder_neither_starts_nor_claims_a_hand_over()
    {
        // The holder has the lock but never answers (a hung copy, or one running with other rights).
        string name = NewName();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: true, Objects + name);
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        holder.Start();
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(10), Ct));

            var (started, handedOver) = Launch(name, quickCheck: false);

            Assert.False(started);
            Assert.False(handedOver);
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    [Fact]
    public void A_copy_that_ended_without_releasing_the_lock_does_not_block_the_next_launch()
    {
        string name = NewName();
        Mutex? abandoned = null;
        var crashed = new Thread(() => abandoned = new Mutex(initiallyOwned: true, Objects + name));
        crashed.Start();
        crashed.Join(); // the thread ended while owning the mutex, so Windows marks it abandoned
        using var keepAlive = abandoned;

        using var next = SingleInstanceGate.Acquire(name, quickCheck: false, out bool handedOver);

        Assert.NotNull(next);
        Assert.False(handedOver);
    }

    [Fact]
    public void The_lock_and_the_events_keep_the_names_other_versions_of_the_app_look_for()
    {
        string name = NewName();
        using var running = SingleInstanceGate.Acquire(name, quickCheck: false, out _);
        Assert.NotNull(running);

        using (Mutex.OpenExisting(Objects + name)) { }
        using (EventWaitHandle.OpenExisting(Objects + name + "-Activate")) { }
        using (EventWaitHandle.OpenExisting(Objects + name + "-Check")) { }
    }

    [Fact]
    public void Disposing_twice_is_harmless()
    {
        var gate = SingleInstanceGate.Acquire(NewName(), quickCheck: false, out _);
        Assert.NotNull(gate);

        gate.Dispose();
        gate.Dispose();
    }
}
