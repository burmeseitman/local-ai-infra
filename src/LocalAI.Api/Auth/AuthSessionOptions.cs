using System.ComponentModel.DataAnnotations;

namespace LocalAI.Api.Auth;

/// <summary>Session cookie settings. Bound from the "Session" configuration section.</summary>
public sealed class AuthSessionOptions
{
    public const string SectionName = "Session";

    [Required]
    [RegularExpression("^[A-Za-z0-9._-]{1,64}$")]
    public string CookieName { get; set; } = "localai.session";

    /// <summary>Sliding lifetime: the session expires after this long without use.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "90.00:00:00")]
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Marks the cookie Secure (HTTPS only). Keep false for plain-HTTP localhost; set true
    /// whenever the dashboard is served over TLS.
    /// </summary>
    public bool SecureCookie { get; set; }

    /// <summary>
    /// Where ASP.NET Core Data Protection keys (which sign session cookies) are stored.
    /// Persisted so sessions survive restarts. Relative paths resolve against the content root.
    /// </summary>
    [Required]
    public string KeysDirectory { get; set; } = "data/keys";
}
