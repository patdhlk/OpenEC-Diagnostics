using OpenEC.Inspector.Session;

namespace OpenEC.Inspector.Tests.Session;

public class MonitorSessionRecordingTests
{
    [Fact]
    public async Task A_session_with_a_record_path_writes_a_reanalyzable_capture()
    {
        var recordPath = Path.Combine(Path.GetTempPath(), $"openec-session-rec-{Guid.NewGuid():N}.pcap");

        try
        {
            var spec = new SourceSpec.File(TestSessions.WriteDemoPcap()) { RecordPath = recordPath };
            await using (var session = new MonitorSession(spec))
            {
                session.Start();
                await session.Completion;

                Assert.Equal(SessionState.Completed, session.State);
                Assert.Equal(103, session.FramesSeen);
                Assert.Equal(recordPath, session.RecordPath);
            }

            await using var replay = new MonitorSession(new SourceSpec.File(recordPath));
            replay.Start();
            await replay.Completion;

            Assert.Equal(SessionState.Completed, replay.State);
            Assert.Equal(103, replay.FramesSeen);
        }
        finally
        {
            if (File.Exists(recordPath)) File.Delete(recordPath);
        }
    }

    [Fact]
    public async Task Without_a_record_path_the_session_reports_none()
    {
        await using var session = await TestSessions.RunFileSessionAsync();

        Assert.Null(session.RecordPath);
    }

    [Fact]
    public async Task An_unwritable_record_path_faults_the_session()
    {
        var spec = new SourceSpec.File(TestSessions.WriteDemoPcap())
        {
            RecordPath = "/nonexistent-dir/recording.pcap",
        };
        await using var session = new MonitorSession(spec);
        session.Start();

        await session.Completion;

        Assert.Equal(SessionState.Faulted, session.State);
        Assert.NotNull(session.Fault);
    }
}
