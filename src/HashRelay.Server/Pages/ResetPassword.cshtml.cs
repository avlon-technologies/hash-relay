using System.ComponentModel.DataAnnotations;
using HashRelay.Server.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace HashRelay.Server.Pages;

/// <summary>Step two of the self-service reset: the emailed link lands here; a valid token unlocks a new-password form.</summary>
[EnableRateLimiting("auth")]
public sealed class ResetPasswordModel(PasswordResetService resets) : PageModel
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Choose a password."), DataType(DataType.Password)]
        public string Password { get; set; } = "";
        [Required(ErrorMessage = "Confirm the password."), DataType(DataType.Password)]
        public string Confirm { get; set; } = "";
    }

    [BindProperty(SupportsGet = true)] public string? Token { get; set; }
    [BindProperty] public InputModel Input { get; set; } = new();
    public bool TokenValid { get; private set; }
    public string Email { get; private set; } = "";
    public int LifetimeHours => (int)PasswordResetService.Lifetime.TotalHours;

    public async Task OnGetAsync(CancellationToken ct)
    {
        var account = await resets.PeekAsync(Token, ct);
        TokenValid = account is not null;
        Email = account?.Email ?? "";
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var account = await resets.PeekAsync(Token, ct);
        TokenValid = account is not null;
        Email = account?.Email ?? "";
        if (!TokenValid) return Page();

        if (AccountService.ValidatePassword(Input.Password) is { } pw) ModelState.AddModelError("Input.Password", pw);
        if (Input.Password != Input.Confirm) ModelState.AddModelError("Input.Confirm", "The passwords do not match.");
        if (!ModelState.IsValid) return Page();

        try
        {
            account = await resets.CompleteAsync(Token, Input.Password, ct);
        }
        catch (ApiException ex)
        {
            ModelState.AddModelError("Input.Password", ex.Message);
            return Page();
        }
        if (account is null)
        {
            TokenValid = false;
            return Page();
        }

        // Sign the person straight in: they have just proven control of the mailbox, which is stronger than the old password.
        await SignInHelper.SignInAsync(HttpContext, account);
        TempData["flash.kind"] = "success";
        TempData["flash.message"] = "Your password has been updated.";
        return Redirect("/dashboard/settings");
    }
}
