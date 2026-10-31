using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using HashRelay.Server.Auth;
using HashRelay.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace HashRelay.Server.Pages;

[EnableRateLimiting("auth")]
public sealed class SignupModel(AccountService accounts, CurrentTenant current, IOptions<HashRelayOptions> options) : PageModel
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Tell us what to call you."), StringLength(80, MinimumLength = 1)]
        public string Name { get; set; } = "";
        [Required(ErrorMessage = "Email is required."), EmailAddress(ErrorMessage = "Enter a valid email address."), StringLength(254)]
        public string Email { get; set; } = "";
        [Required(ErrorMessage = "Choose a password."), DataType(DataType.Password)]
        public string Password { get; set; } = "";
        [Required(ErrorMessage = "Name your workspace."), StringLength(80, MinimumLength = 2, ErrorMessage = "Workspace name must be 2–80 characters.")]
        public string Workspace { get; set; } = "";
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public bool AllowSignup => options.Value.AllowSignup;

    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? Redirect("/dashboard") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!AllowSignup) return Page();
        if (AccountService.ValidatePassword(Input.Password) is { } pw) ModelState.AddModelError("Input.Password", pw);
        if (!ModelState.IsValid) return Page();
        try
        {
            var (account, tenant) = await accounts.SignUpAsync(Input.Email, Input.Name, Input.Password, Input.Workspace, ct);
            await SignInHelper.SignInAsync(HttpContext, account);
            current.Switch(tenant.Id);
            return Redirect("/dashboard?welcome=1");
        }
        catch (ApiException ex)
        {
            ModelState.AddModelError(ex.Details?.Keys.FirstOrDefault() is { } f ? $"Input.{char.ToUpper(f[0])}{f[1..]}" : string.Empty, ex.Message);
            return Page();
        }
    }
}

public static class SignInHelper
{
    public static Task SignInAsync(HttpContext ctx, Account account)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
            new Claim(ClaimTypes.Name, account.DisplayName),
            new Claim(ClaimTypes.Email, account.Email),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, AllowRefresh = true });
    }
}
