using LetsClip.Data.Enums;

namespace LetsClip.Data.ViewModels;

public sealed record ExportBudget(
    long TargetBytes,
    double VideoBitsPerSecond,
    BudgetVerdict Verdict
);
