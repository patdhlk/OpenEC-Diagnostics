using System.Collections.Specialized;
using OpenEC.Inspector.Session;
using OpenEC.Inspector.ViewModels;
using OpenEC.Monitor;
using OpenEC.Monitor.Capture;
using OpenEC.Monitor.Observation;

namespace OpenEC.Inspector.Tests.ViewModels;

public class EventsViewModelTests
{
    private static async Task<List<RawFrame>> DemoFramesAsync()
    {
        var frames = new List<RawFrame>();
        await using var source = new PcapFileSource(TestSessions.WriteDemoPcap());
        await foreach (var f in source.CaptureAsync()) frames.Add(f);
        return frames;
    }

    private static async Task WaitForFramesAsync(MonitorSession session, long count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.FramesSeen < count)
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {count} frames");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void Formatter_categorizes_and_describes_every_event_kind()
    {
        var ts = DateTimeOffset.UnixEpoch;

        var state = new MonitorEvent.SlaveStateChanged(ts, 1004, SlaveAlState.Op, SlaveAlState.SafeOp, true);
        Assert.Equal("State", EventFormatter.Category(state));
        Assert.Equal("Slave 1004: Op → SafeOp (error)", EventFormatter.Describe(state));

        var wkc = new MonitorEvent.WkcMismatchDetected(ts, OpenEC.Monitor.Protocol.EtherCatCommand.Lrw,
            0x01000000, 6, 5);
        Assert.Equal("WKC", EventFormatter.Category(wkc));
        Assert.Equal("Lrw @0x01000000: WKC 5 (expected 6)", EventFormatter.Describe(wkc));

        var emergency = new MonitorEvent.EmergencyReceived(ts, 1004, 0x8130, 0x81);
        Assert.Equal("Emergency", EventFormatter.Category(emergency));
        Assert.Equal("Slave 1004: CoE emergency 0x8130 (register 0x81)", EventFormatter.Describe(emergency));
    }

    /// <summary>Health has its own filter, or a BusHealthChanged is only reachable as "Other".</summary>
    [Fact]
    public async Task The_messages_panel_offers_a_health_filter()
    {
        var events = new EventsViewModel(await TestSessions.BringupAsync());

        Assert.Contains(events.Categories, c => c.Name == "Health");
    }

    [Fact]
    public async Task Refresh_fills_rows_from_the_event_log()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());
        var vm = new EventsViewModel(session);

        vm.Refresh();

        Assert.True(vm.Rows.Count >= 4);
        Assert.Contains(vm.Rows, r => r.Category == "WKC");
        Assert.Contains(vm.Rows, r => r.Category == "Emergency");
        Assert.Contains(vm.Rows, r => r.Category == "SoE");
    }

    [Fact]
    public async Task Disabling_categories_filters_the_rows()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());
        var vm = new EventsViewModel(session);
        vm.Refresh();

        foreach (var category in vm.Categories)
            category.IsEnabled = category.Name == "WKC";

        var row = Assert.Single(vm.Rows);
        Assert.Equal("WKC", row.Category);

        foreach (var category in vm.Categories)
            category.IsEnabled = true;
        Assert.True(vm.Rows.Count >= 4);
    }

    [Fact]
    public async Task Unchanged_snapshot_does_not_rebuild_rows()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());
        var vm = new EventsViewModel(session);
        vm.Refresh();
        var changes = 0;
        vm.Rows.CollectionChanged += (_, _) => changes++;

        vm.Refresh();

        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task New_events_are_appended_without_rebuilding_existing_rows()
    {
        var frames = await DemoFramesAsync();
        var source = new PushCaptureSource();
        await using var session = new MonitorSession(
            EtherCatMonitor.FromSource(source), "push", TestSessions.LoadFixtureEni());
        session.Start();

        // First 61 frames cover the state change (cycle 16) and WKC mismatch (cycle 25).
        foreach (var f in frames.Take(61)) source.Push(f);
        await WaitForFramesAsync(session, 61);
        var vm = new EventsViewModel(session);
        vm.Refresh();
        Assert.True(vm.Rows.Count >= 2);
        var firstRow = vm.Rows[0];
        var countBefore = vm.Rows.Count;

        var actions = new List<NotifyCollectionChangedAction>();
        vm.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);

        foreach (var f in frames.Skip(61)) source.Push(f);
        source.Complete();
        await WaitForFramesAsync(session, frames.Count);
        vm.Refresh();

        Assert.True(vm.Rows.Count > countBefore);
        Assert.Same(firstRow, vm.Rows[0]); // existing rows untouched
        Assert.All(actions, a => Assert.Equal(NotifyCollectionChangedAction.Add, a));
        Assert.Contains(vm.Rows, r => r.Category == "Emergency");
        Assert.Contains(vm.Rows, r => r.Category == "SoE");
    }

    [Fact]
    public async Task Appending_beyond_the_cap_trims_the_oldest_rows()
    {
        var frames = await DemoFramesAsync();
        var source = new PushCaptureSource();
        await using var session = new MonitorSession(
            EtherCatMonitor.FromSource(source), "push", TestSessions.LoadFixtureEni());
        session.Start();

        foreach (var f in frames.Take(61)) source.Push(f);
        await WaitForFramesAsync(session, 61);
        var vm = new EventsViewModel(session, maxRows: 3);
        vm.Refresh();
        var before = vm.Rows.Count;
        Assert.InRange(before, 1, 3);

        foreach (var f in frames.Skip(61)) source.Push(f);
        source.Complete();
        await WaitForFramesAsync(session, frames.Count);
        vm.Refresh();

        Assert.True(vm.Rows.Count <= 3);
        Assert.Contains(vm.Rows, r => r.Category == "SoE"); // newest survived the trim
    }

    [Fact]
    public async Task More_new_events_than_the_cap_trigger_a_full_rebuild()
    {
        var frames = await DemoFramesAsync();
        var source = new PushCaptureSource();
        await using var session = new MonitorSession(
            EtherCatMonitor.FromSource(source), "push", TestSessions.LoadFixtureEni());
        session.Start();

        // maxRows 2: after the second batch, SnapshotEvents(2) no longer contains the
        // old tail (emergency + SoE alone fill the window), so append is impossible.
        foreach (var f in frames.Take(61)) source.Push(f);
        await WaitForFramesAsync(session, 61);
        var vm = new EventsViewModel(session, maxRows: 2);
        vm.Refresh();

        var actions = new List<NotifyCollectionChangedAction>();
        vm.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);

        foreach (var f in frames.Skip(61)) source.Push(f);
        source.Complete();
        await WaitForFramesAsync(session, frames.Count);
        vm.Refresh();

        Assert.Contains(NotifyCollectionChangedAction.Reset, actions); // Rows.Clear() ran
        Assert.True(vm.Rows.Count <= 2);
        Assert.Contains(vm.Rows, r => r.Category == "SoE");
    }

    [Fact]
    public async Task A_collapsed_panel_skips_refresh_and_catches_up_on_expand()
    {
        await using var session = await TestSessions.RunFileSessionAsync(TestSessions.LoadFixtureEni());
        var vm = new EventsViewModel(session) { IsCollapsed = true };

        vm.Refresh();
        Assert.Empty(vm.Rows);

        vm.IsCollapsed = false; // expanding refreshes immediately

        Assert.True(vm.Rows.Count >= 4);
    }

    /// <summary>Every category the formatter can emit must have a toggle. A category without one is
    /// permanently visible, which is how learning's own events broke the filter when they arrived.</summary>
    [Fact]
    public async Task Every_formatter_category_has_a_filter_toggle()
    {
        await using var session = await TestSessions.RunFileSessionAsync();
        var vm = new EventsViewModel(session);

        foreach (var expected in new[]
                 { "State", "State request", "WKC", "Emergency", "SoE", "Config", "Learning", "Other" })
            Assert.Contains(expected, vm.Categories.Select(c => c.Name));
    }
}
