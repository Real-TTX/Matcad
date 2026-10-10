using Matcad.Services;

namespace Matcad.Tests;

public class LoginAttemptTrackerTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void An_account_on_one_client_is_refused_after_too_many_attempts()
    {
        var tracker = new LoginAttemptTracker(new FakeTime());
        for (var i = 0; i < LoginAttemptTracker.MaxPerAccountOnClient; i++)
            Assert.NotNull(tracker.Begin("admin", "alice", "10.0.0.1"));

        Assert.Null(tracker.Begin("admin", "alice", "10.0.0.1"));
        Assert.NotNull(tracker.Begin("admin", "bob", "10.0.0.1")); // another account, same client
    }

    [Fact]
    public void Changing_the_client_address_does_not_lift_the_per_account_limit()
    {
        var tracker = new LoginAttemptTracker(new FakeTime());
        var allowed = 0;
        for (var i = 0; i < 200; i++)
            if (tracker.Begin("admin", "alice", "198.51.100." + i) != null) allowed++; // a new address every time

        Assert.Equal(LoginAttemptTracker.MaxPerAccount, allowed);
    }

    [Fact]
    public void One_client_is_refused_after_too_many_attempts_on_many_accounts()
    {
        var tracker = new LoginAttemptTracker(new FakeTime());
        var allowed = 0;
        for (var i = 0; i < 100; i++)
            if (tracker.Begin("admin", "user" + i, "10.0.0.1") != null) allowed++;

        Assert.Equal(LoginAttemptTracker.MaxPerClient, allowed);
    }

    [Fact]
    public async Task Parallel_guesses_cannot_all_pass_a_check_that_comes_before_the_failures()
    {
        var tracker = new LoginAttemptTracker(new FakeTime());

        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => tracker.Begin("admin", "alice", "10.0.0.1"))));

        Assert.Equal(LoginAttemptTracker.MaxPerAccountOnClient, results.Count(t => t != null));
    }

    [Fact]
    public void The_block_ends_when_the_window_has_passed()
    {
        var time = new FakeTime();
        var tracker = new LoginAttemptTracker(time);
        for (var i = 0; i < 20; i++) tracker.Begin("admin", "alice", "10.0.0.1");
        Assert.Null(tracker.Begin("admin", "alice", "10.0.0.1"));

        time.Now = time.Now.AddMinutes(11);

        Assert.NotNull(tracker.Begin("admin", "alice", "10.0.0.1"));
    }

    [Fact]
    public void A_successful_sign_in_cleans_the_account_and_gives_the_attempt_back_to_the_client()
    {
        var tracker = new LoginAttemptTracker(new FakeTime());
        for (var i = 0; i < LoginAttemptTracker.MaxPerAccountOnClient - 1; i++) tracker.Begin("admin", "alice", "10.0.0.1");
        var last = tracker.Begin("admin", "alice", "10.0.0.1")!;

        tracker.Succeeded(last);

        for (var i = 0; i < LoginAttemptTracker.MaxPerAccountOnClient; i++)
            Assert.NotNull(tracker.Begin("admin", "alice", "10.0.0.1")); // a clean slate
    }

    [Fact]
    public void Logins_are_compared_without_case_and_whitespace_and_scopes_are_separate()
    {
        var tracker = new LoginAttemptTracker(new FakeTime());
        for (var i = 0; i < LoginAttemptTracker.MaxPerAccountOnClient; i++) tracker.Begin("admin", " Alice ", "10.0.0.1");

        Assert.Null(tracker.Begin("admin", "alice", "10.0.0.1"));
        Assert.NotNull(tracker.Begin("portal3", "alice", "10.0.0.1"));
    }
}
