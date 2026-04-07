namespace KcdMp.Shared.Protocol;

public enum ProjectionApplyStatus : byte
{
    Started = 1,
    Applied = 2,
    PartiallyApplied = 3,
    NotApplied = 4,
    Failed = 5,
}

