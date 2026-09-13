using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;
using RaceVideoProcessor.Core.Services;

namespace RaceVideoProcessor.Tests;

/// <summary>The process → upload → assign pipeline, with fakes for FFmpeg and the backend.</summary>
public sealed class EntryWorkflowServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rvp-workflow", Guid.NewGuid().ToString("N"));
    private readonly InMemoryRepository _repository = new();
    private readonly FakeVideo _video = new();
    private readonly FakePublisher _publisher = new();
    private readonly TestLog _log = new();

    public EntryWorkflowServiceTests()
    {
        Directory.CreateDirectory(_root);
        _repository.Races.Add(new Race { RaceId = "AAA", RaceName = "Race A", RaceDate = new DateOnly(2026, 9, 13) });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private EntryWorkflowService Service() => new(_video, _publisher, _repository, _log);

    private (WorkflowJob Job, EntryLocalState State) NewJob(bool uploadEnabled, RaceScope? scope = null)
    {
        scope ??= TestScopes.RaceA200;
        var input = Path.Combine(_root, "cart100.mp4");
        File.WriteAllBytes(input, [1, 2, 3]);
        var entry = TestEntries.Create(entryId: "marker-100", card: "100");
        var job = new WorkflowJob(scope, entry, input, Path.Combine(_root, "Processed", "cart100.mp4"),
            new OverlayData("n", "l", "100", null, null, "00:17.88"), "HASH", false, uploadEnabled);
        var state = TestScopes.State(scope, entry.EntryId);
        state.LocalVideoPath = input;
        state.ProcessingStatus = LocalProcessingStatus.Ready;
        return (job, state);
    }

    // ---- Test 4: full workflow ----------------------------------------------------

    [Fact]
    public async Task UploadOnProcessesUploadsAndAssignsInOrder()
    {
        var (job, state) = NewJob(uploadEnabled: true);
        var stages = new List<WorkflowStage>();

        var result = await Service().RunAsync(job, state, new SyncProgress<WorkflowProgress>(p =>
        {
            if (stages.Count == 0 || stages[^1] != p.Stage) stages.Add(p.Stage);
        }), CancellationToken.None);

        Assert.Equal(WorkflowOutcome.Completed, result.Outcome);
        Assert.Equal([WorkflowStage.Processing, WorkflowStage.Uploading, WorkflowStage.Assigning], stages);
        Assert.Equal(LocalProcessingStatus.Completed, result.State.ProcessingStatus);
        Assert.Equal(UploadStatus.Completed, result.State.UploadStatus);
        Assert.Equal(AssignmentStatus.Completed, result.State.AssignmentStatus);
        Assert.Equal(OverallStatus.Completed, result.State.Overall);
        Assert.Equal(FakePublisher.Link, result.State.UploadedVideoLink);

        // Uploaded the processed output, and assigned with the right identifiers.
        Assert.Equal(job.OutputPath, _publisher.Uploaded.Single());
        var assignment = _publisher.Assigned.Single();
        Assert.Equal(TestScopes.RaceA200, assignment.Scope);
        Assert.Equal("player-marker-100", assignment.PlayerId);
        Assert.Equal("marker-100", assignment.Marker);
        Assert.Equal(FakePublisher.Link, assignment.VideoLink);

        // Every stage was persisted as it happened, and ends completed in the store.
        var stored = _repository.States[InMemoryRepository.Key(TestScopes.RaceA200, "marker-100")];
        Assert.Equal(OverallStatus.Completed, stored.Overall);
        Assert.Contains(_repository.Writes, w => w.ProcessingStatus == LocalProcessingStatus.Processing);
        Assert.Contains(_repository.Writes, w => w.UploadStatus == UploadStatus.Uploading);
        Assert.Contains(_repository.Writes, w => w.AssignmentStatus == AssignmentStatus.Assigning);
    }

    [Fact]
    public async Task UploadProgressIsReportedFromThePublisher()
    {
        var (job, state) = NewJob(uploadEnabled: true);
        var percents = new List<double?>();

        await Service().RunAsync(job, state, new SyncProgress<WorkflowProgress>(p =>
        {
            if (p.Stage == WorkflowStage.Uploading) percents.Add(p.Percent);
        }), CancellationToken.None);

        Assert.Contains(50.0, percents);
        Assert.Contains(100.0, percents);
    }

    // ---- Test 5: upload OFF -------------------------------------------------------

    [Fact]
    public async Task UploadOffStopsAfterProcessingWithoutUploadingOrAssigning()
    {
        var (job, state) = NewJob(uploadEnabled: false);

        var result = await Service().RunAsync(job, state, null, CancellationToken.None);

        Assert.Equal(WorkflowOutcome.UploadDisabled, result.Outcome);
        Assert.Equal(LocalProcessingStatus.Completed, result.State.ProcessingStatus);
        Assert.Equal(UploadStatus.Disabled, result.State.UploadStatus);
        Assert.Equal(AssignmentStatus.NotStarted, result.State.AssignmentStatus);
        Assert.Null(result.State.ErrorMessage);
        Assert.Empty(_publisher.Uploaded);
        Assert.Empty(_publisher.Assigned);
    }

    // ---- Cancellation and failure ---------------------------------------------------

    [Fact]
    public async Task CancellingProcessingMarksItCancelledAndNeverUploads()
    {
        var (job, state) = NewJob(uploadEnabled: true);
        using var cts = new CancellationTokenSource();
        _video.OnProcess = async ct =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        };

        var result = await Service().RunAsync(job, state, null, cts.Token);

        Assert.Equal(WorkflowOutcome.ProcessingCancelled, result.Outcome);
        Assert.Equal(LocalProcessingStatus.Cancelled, result.State.ProcessingStatus);
        Assert.Equal(OverallStatus.Cancelled, result.State.Overall);
        Assert.Empty(_publisher.Uploaded);
        Assert.Empty(_publisher.Assigned);
        Assert.Equal(LocalProcessingStatus.Cancelled,
            _repository.States[InMemoryRepository.Key(TestScopes.RaceA200, "marker-100")].ProcessingStatus);
    }

    [Fact]
    public async Task InvalidOutputIsProcessingFailedAndIsNeverUploaded()
    {
        var (job, state) = NewJob(uploadEnabled: true);
        _video.OnProcess = _ => Task.FromResult(new ProcessingResult(false, job.OutputPath, "Output validation failed: empty", null, null, false));

        var result = await Service().RunAsync(job, state, null, CancellationToken.None);

        Assert.Equal(WorkflowOutcome.ProcessingFailed, result.Outcome);
        Assert.Equal(LocalProcessingStatus.Failed, result.State.ProcessingStatus);
        Assert.StartsWith("Processing Failed", result.State.ErrorMessage);
        Assert.Empty(_publisher.Uploaded);
    }

    [Fact]
    public async Task UploadFailureKeepsTheProcessedFileAndARetryUploadsWithoutReprocessing()
    {
        var (job, state) = NewJob(uploadEnabled: true);
        _publisher.FailUploads = 1;

        var first = await Service().RunAsync(job, state, null, CancellationToken.None);
        Assert.Equal(WorkflowOutcome.UploadFailed, first.Outcome);
        Assert.Equal(LocalProcessingStatus.Completed, first.State.ProcessingStatus);
        Assert.Equal(UploadStatus.Failed, first.State.UploadStatus);
        Assert.Empty(_publisher.Assigned);

        var retry = await Service().RunAsync(job, first.State, null, CancellationToken.None);

        Assert.Equal(WorkflowOutcome.Completed, retry.Outcome);
        Assert.Equal(1, _video.Calls);
        Assert.Equal(2, _publisher.UploadAttempts);
    }

    // ---- PATCH failure: keep the link, retry only the PATCH ---------------------------

    [Fact]
    public async Task AssignmentFailureKeepsTheLinkAndARetryPatchesWithoutUploadingAgain()
    {
        var (job, state) = NewJob(uploadEnabled: true);
        _publisher.FailAssignments = 1;

        var first = await Service().RunAsync(job, state, null, CancellationToken.None);

        Assert.Equal(WorkflowOutcome.AssignmentFailed, first.Outcome);
        Assert.Equal(UploadStatus.Completed, first.State.UploadStatus);
        Assert.Equal(FakePublisher.Link, first.State.UploadedVideoLink);
        Assert.Equal(AssignmentStatus.Failed, first.State.AssignmentStatus);
        Assert.Equal(OverallStatus.AssignmentFailed, first.State.Overall);

        var retry = await Service().RunAsync(job, first.State, null, CancellationToken.None);

        Assert.Equal(WorkflowOutcome.Completed, retry.Outcome);
        Assert.Equal(1, _video.Calls);
        Assert.Equal(1, _publisher.UploadAttempts);
        Assert.Equal(2, _publisher.AssignAttempts);
        Assert.All(_publisher.Assigned, a => Assert.Equal(FakePublisher.Link, a.VideoLink));
    }

    [Fact]
    public async Task AnAuthenticationFailureIsReportedAsSuch()
    {
        var (job, state) = NewJob(uploadEnabled: true);
        _publisher.AuthFailUploads = true;

        var result = await Service().RunAsync(job, state, null, CancellationToken.None);

        Assert.Equal(WorkflowOutcome.AuthenticationFailed, result.Outcome);
        Assert.True(result.State.LastFailureWasAuthentication);
        Assert.Equal(OverallStatus.AuthenticationFailed, result.State.Overall);
        Assert.StartsWith("Authentication Failed", result.State.ErrorMessage);
    }

    [Fact]
    public async Task AnEntryWithoutAMarkerFailsAssignmentWithoutCallingTheBackend()
    {
        var (job, state) = NewJob(uploadEnabled: true);
        job = job with { Entry = job.Entry with { Marker = null } };

        var result = await Service().RunAsync(job, state, null, CancellationToken.None);

        Assert.Equal(WorkflowOutcome.AssignmentFailed, result.Outcome);
        Assert.Contains("marker", result.State.ErrorMessage);
        Assert.Empty(_publisher.Assigned);
    }

    [Fact]
    public async Task AJobRefusesStateFromAnotherRace()
    {
        var (job, _) = NewJob(uploadEnabled: true);
        var foreign = TestScopes.State(TestScopes.RaceB200, "marker-100");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().RunAsync(job, foreign, null, CancellationToken.None));
    }

    // ---- Crash recovery ------------------------------------------------------------------

    [Fact]
    public void AfterACrashAnInterruptedUploadResumesAndInterruptedProcessingIsFailed()
    {
        var uploading = TestScopes.State(TestScopes.RaceA200, "a");
        uploading.ProcessingStatus = LocalProcessingStatus.Completed;
        uploading.UploadStatus = UploadStatus.Uploading;

        var processing = TestScopes.State(TestScopes.RaceA200, "b");
        processing.ProcessingStatus = LocalProcessingStatus.Processing;

        var assigning = TestScopes.State(TestScopes.RaceA200, "c");
        assigning.ProcessingStatus = LocalProcessingStatus.Completed;
        assigning.UploadStatus = UploadStatus.Completed;
        assigning.UploadedVideoLink = "https://l";
        assigning.AssignmentStatus = AssignmentStatus.Assigning;

        Assert.True(WorkflowRecovery.RecoverInterrupted(uploading));
        Assert.True(WorkflowRecovery.RecoverInterrupted(processing));
        Assert.True(WorkflowRecovery.RecoverInterrupted(assigning));

        Assert.Equal(UploadStatus.NotStarted, uploading.UploadStatus);
        Assert.True(WorkflowRecovery.NeedsAutomaticResume(uploading, uploadEnabled: true));
        Assert.False(WorkflowRecovery.NeedsAutomaticResume(uploading, uploadEnabled: false));

        Assert.Equal(LocalProcessingStatus.Failed, processing.ProcessingStatus);
        Assert.False(WorkflowRecovery.NeedsAutomaticResume(processing, uploadEnabled: true));

        Assert.Equal(AssignmentStatus.NotStarted, assigning.AssignmentStatus);
        Assert.Equal("https://l", assigning.UploadedVideoLink);
        Assert.True(WorkflowRecovery.NeedsAutomaticResume(assigning, uploadEnabled: true));
    }

    [Fact]
    public async Task ResumingAfterACrashDuringAssignmentPatchesWithTheStoredLinkOnly()
    {
        var (job, state) = NewJob(uploadEnabled: true);
        File.WriteAllBytes(Directory.CreateDirectory(Path.GetDirectoryName(job.OutputPath)!).FullName + "/cart100.mp4", [9]);
        state.ProcessingStatus = LocalProcessingStatus.Completed;
        state.OutputPath = job.OutputPath;
        state.UploadStatus = UploadStatus.Completed;
        state.UploadedVideoLink = "https://media.test/stored.mp4";
        state.AssignmentStatus = AssignmentStatus.Assigning;
        WorkflowRecovery.RecoverInterrupted(state);

        var result = await Service().RunAsync(job, state, null, CancellationToken.None);

        Assert.Equal(WorkflowOutcome.Completed, result.Outcome);
        Assert.Equal(0, _video.Calls);
        Assert.Equal(0, _publisher.UploadAttempts);
        Assert.Equal("https://media.test/stored.mp4", _publisher.Assigned.Single().VideoLink);
    }

    [Fact]
    public void FailedStagesAreNotResumedAutomatically()
    {
        var state = TestScopes.State(TestScopes.RaceA200, "a");
        state.ProcessingStatus = LocalProcessingStatus.Completed;
        state.UploadStatus = UploadStatus.Completed;
        state.AssignmentStatus = AssignmentStatus.Failed;
        Assert.False(WorkflowRecovery.NeedsAutomaticResume(state, uploadEnabled: true));

        state.UploadStatus = UploadStatus.Disabled;
        state.AssignmentStatus = AssignmentStatus.NotStarted;
        Assert.False(WorkflowRecovery.NeedsAutomaticResume(state, uploadEnabled: true));
    }

    // ---- Fakes ---------------------------------------------------------------------------

    private sealed class FakeVideo : IVideoProcessingService
    {
        public int Calls { get; private set; }
        public Func<CancellationToken, Task<ProcessingResult>>? OnProcess { get; set; }

        public async Task<ProcessingResult> ProcessAsync(ProcessingRequest request, IProgress<ProcessingProgress>? progress, CancellationToken cancellationToken)
        {
            Calls++;
            if (OnProcess is not null)
                return await OnProcess(cancellationToken);

            progress?.Report(new ProcessingProgress(50, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
            Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
            await File.WriteAllBytesAsync(request.OutputPath, [4, 5, 6], cancellationToken);
            return new ProcessingResult(true, request.OutputPath, null, null, null, false);
        }

        public Task<PreviewResult> GeneratePreviewAsync(string inputPath, OverlayData overlay, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FakePublisher : IMediaPublisher
    {
        public const string Link = "https://media.namadhurekla.com/uploads/cart100.mp4";

        public string Name => "fake";
        public int FailUploads { get; set; }
        public int FailAssignments { get; set; }
        public bool AuthFailUploads { get; set; }
        public int UploadAttempts { get; private set; }
        public int AssignAttempts { get; private set; }
        public List<string> Uploaded { get; } = [];
        public List<AssignmentRequest> Assigned { get; } = [];

        public Task<string> UploadAsync(string filePath, IProgress<UploadProgress>? progress, CancellationToken cancellationToken)
        {
            UploadAttempts++;
            if (AuthFailUploads)
                throw new BackendException(BackendFailureKind.Authentication, "login failed (HTTP 401).", 401);
            if (FailUploads-- > 0)
                throw new BackendException(BackendFailureKind.Network, "connection reset");

            progress?.Report(new UploadProgress(0, 200));
            progress?.Report(new UploadProgress(100, 200));
            progress?.Report(new UploadProgress(200, 200));
            Uploaded.Add(filePath);
            return Task.FromResult(Link);
        }

        public Task AssignAsync(AssignmentRequest request, CancellationToken cancellationToken)
        {
            AssignAttempts++;
            Assigned.Add(request);
            if (FailAssignments-- > 0)
                throw new BackendException(BackendFailureKind.Http, "Assignment failed (HTTP 500).", 500);
            return Task.CompletedTask;
        }
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
