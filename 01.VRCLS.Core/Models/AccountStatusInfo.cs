using System;

namespace VRCLS.Core.Models
{
    public sealed record AccountStatusInfo
    {
        public BackendAuthMode AuthMode { get; init; } = BackendAuthMode.None;

        public bool IsAnonymousMode { get; init; }

        public string UserLabel { get; init; } = string.Empty;

        public bool IsActive { get; init; } = true;

        public bool IsExpired { get; init; }

        public DateTimeOffset? ExpirationUtc { get; init; }

        public int? RemainingSeconds { get; init; }

        public int? DailyLimit { get; init; }

        public int? RemainingDaily { get; init; }

        public string? PlanName { get; init; }

        public decimal? PlanPrice { get; init; }

        public string? PlanRef { get; init; }

        public string? PricingMode { get; init; }

        public int? AddonResourceBalance { get; init; }

        public int? AddonResourceTotal { get; init; }

        public string? Email { get; init; }

        public bool EmailVerified { get; init; }

        public string? UserUuid { get; init; }
    }
}
