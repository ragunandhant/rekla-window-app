namespace RaceVideoProcessor.Core.Models;

public enum DataSourceMode
{
    Demo,
    RealApi
}

public enum RemoteExtractionStatus
{
    Unknown,
    NotCompleted,
    Completed,
    Failed
}

/// <summary>Processing stage. Stored by name, so values may be added but never renamed.</summary>
public enum LocalProcessingStatus
{
    VideoNotSelected,
    Ready,
    Processing,
    Completed,
    Failed,
    VideoNotFound,
    Outdated,
    Cancelled,

    /// <summary>Processing was OFF for this operation: the selected video is used as it is. Not an error.</summary>
    Skipped
}

/// <summary>Upload stage. <see cref="Disabled"/> is a normal outcome when upload is OFF, not an error.</summary>
public enum UploadStatus
{
    NotStarted,
    Uploading,
    Completed,
    Failed,
    Disabled
}

/// <summary>PATCH assignment of the uploaded video link to the player.</summary>
public enum AssignmentStatus
{
    NotStarted,
    Assigning,
    Completed,
    Failed
}

/// <summary>
/// Which stages an operation runs, fixed from the Processing and Upload toggles
/// when it starts. Stored with the entry so a retry or a restart repeats the same
/// kind of operation: a direct upload never turns into processing, or back.
/// </summary>
public enum WorkflowMode
{
    /// <summary>Process → upload the processed file → assign.</summary>
    ProcessAndUpload,

    /// <summary>Process and keep the processed file. Nothing remote.</summary>
    ProcessOnly,

    /// <summary>No FFmpeg: upload the selected file itself → assign.</summary>
    DirectUpload,

    /// <summary>Both stages OFF: the selection is kept. Nothing runs.</summary>
    SelectionOnly
}

public static class WorkflowModes
{
    public static WorkflowMode From(bool processingEnabled, bool uploadEnabled) => (processingEnabled, uploadEnabled) switch
    {
        (true, true) => WorkflowMode.ProcessAndUpload,
        (true, false) => WorkflowMode.ProcessOnly,
        (false, true) => WorkflowMode.DirectUpload,
        _ => WorkflowMode.SelectionOnly
    };

    public static bool Processes(this WorkflowMode mode) => mode is WorkflowMode.ProcessAndUpload or WorkflowMode.ProcessOnly;
    public static bool Uploads(this WorkflowMode mode) => mode is WorkflowMode.ProcessAndUpload or WorkflowMode.DirectUpload;
}

/// <summary>The single status derived from the three stage statuses.</summary>
public enum OverallStatus
{
    Ready,
    Processing,
    ProcessingCompleted,
    ProcessingFailed,
    Cancelled,
    Uploading,
    UploadCompleted,
    UploadFailed,
    Assigning,
    AssignmentFailed,
    Completed,
    AuthenticationFailed,

    /// <summary>Processed; upload was OFF. Not an error.</summary>
    UploadDisabled,

    /// <summary>Processing OFF, waiting for (or between) direct-upload stages.</summary>
    ReadyForDirectUpload,

    /// <summary>Processing and upload both OFF: the video is only selected. Not an error.</summary>
    ProcessingDisabled
}

public enum EncoderPreference
{
    Auto,
    NvidiaNvenc,
    CpuX264
}

public enum EncodingQuality
{
    High,
    VeryHigh
}
