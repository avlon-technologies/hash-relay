using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace HashRelay.Server.Pages;

[EnableRateLimiting("auth")]
public sealed class LoginModel(AccountService accounts) : PageModel
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Email is required."), EmailAddress(ErrorMessage = "Enter a valid email address.")]
        public string Email { get; set; } = "";
        [Required(ErrorMessage = "Password is required."), DataType(DataType.Password)]
        public string Password { get; set; } = "";
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? Redirect(SafeReturn()) : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();
        var account = await accounts.SignInAsync(Input.Email, Input.Password, ct);
        if (account is null)
        {
            ModelState.AddModelError(string.Empty, "That email and password don't match.");
            return Page();
        }
        await SignInHelper.SignInAsync(HttpContext, account);
        return Redirect(SafeReturn());
    }

    private string SafeReturn() => !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/dashboard";
}
