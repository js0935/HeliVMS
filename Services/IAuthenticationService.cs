using HeliVMS.Models;

namespace HeliVMS.Services;

public interface IAuthenticationService
{
    User? CurrentUser { get; }
    bool IsLoggedIn { get; }
    event Action<User>? LoginSucceeded;
    event Action? LoggedOut;
    event Action? SessionExpired;
    bool Login(string username, string password);
    void Logout();
    bool ChangePassword(string oldPassword, string newPassword);

    (bool Success, bool RequiresTwoFactor) LoginWithTwoFactorSupport(string username, string password);
    bool CompleteTwoFactorLogin(string code);
    bool RequiresTwoFactor(string username);
    bool VerifyTwoFactor(string username, string code);
    void SetupTwoFactor(string userId);
    bool DisableTwoFactor(string userId, string password);
    string GetTwoFactorQrCodeUri(string userId);

    bool IsAccountLocked(string username);
    int GetRemainingLockoutMinutes(string username);
    void ResetSessionTimer();
    void StopSessionTimer();
}
