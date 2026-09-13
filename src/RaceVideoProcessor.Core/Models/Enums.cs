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

public enum LocalProcessingStatus
{
    VideoNotSelected,
    Ready,
    Processing,
    Completed,
    Failed,
    VideoNotFound,
    Outdated
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
