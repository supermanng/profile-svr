namespace ProfileSvr.Domain;

/// <summary>Latest identity-verification outcome for a profile (same lifecycle as vliquidity).</summary>
public enum KycVerificationStatus
{
    NotStarted = 0,
    /// <summary>KYC initiated (identity looked up, liveness session issued); awaiting completion.</summary>
    Pending = 1,
    /// <summary>Liveness + face match passed and the identity is verified.</summary>
    Approved = 2,
    /// <summary>The provider asked the applicant to resubmit (recoverable).</summary>
    Retry = 3,
    /// <summary>The provider permanently rejected the applicant.</summary>
    Rejected = 4,
    /// <summary>The liveness check did not pass.</summary>
    LivenessFailed = 5,
    /// <summary>The selfie did not match the BVN/NIN photo.</summary>
    FaceMismatch = 6,
    /// <summary>Identity verified, but downstream account provisioning failed (retried at login/refresh).</summary>
    ProvisioningFailed = 7,
    /// <summary>Could not complete (missing image, photo or identifier).</summary>
    Failed = 8
}
