namespace MaterialLedger.Data.Models;

public class AppUser
{
    public int AppUserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public AppUserRole Role { get; set; }
    public bool IsActive { get; set; }
}
