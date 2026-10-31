using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Pages;

/// <summary>Step one of the self-service reset: ask for an email. The response is identical whether or not the address has an account.</summary>
[EnableRateLimiting("auth")]
public sealed class ForgotPasswordModel(PasswordResetService resets, IOptions<HashRelayOptions> options) : PageModel
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Email is required."), EmailAddress(ErrorMessage = "Enter a valid email address."), StringLength(254)]
        public string Email { get; set; } = "";
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public bool Sent { get; private set; }
    public int LifetimeHours => (int)PasswordResetService.Lifetime.TotalHours;

    public IActionResult OnGet()
    {
        if (!options.Value.AllowPasswordReset) return NotFound();
        return User.Identity?.IsAuthenticated == true ? Redirect("/dashboard/settings") : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!options.Value.AllowPasswordReset) return NotFound();
        if (!ModelState.IsValid) return Page();
        await resets.RequestAsync(Input.Email, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        Sent = true;
        return Page();
    }
}
