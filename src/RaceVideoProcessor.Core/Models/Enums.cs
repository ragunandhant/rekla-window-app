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
    Cancelled
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
    AuthenticationFailed
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
