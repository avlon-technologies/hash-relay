using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HashRelay.Server.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public sealed class ErrorModel : PageModel
{
    public int Code { get; private set; } = 500;
    public string Title { get; private set; } = "Something went wrong";
    public string Message { get; private set; } = "An unexpected error occurred. Please try again.";

    public void OnGet(int? code)
    {
        Code = code ?? (Response.StatusCode is >= 400 ? Response.StatusCode : 500);
        (Title, Message) = Code switch
        {
            404 => ("Page not found", "The page you're looking for doesn't exist or has moved."),
            403 => ("Not allowed", "You don't have access to that."),
            401 => ("Sign in required", "Please sign in to continue."),
            429 => ("Slow down", "Too many requests. Please wait a moment and try again."),
            _ => ("Something went wrong", "An unexpected error occurred. Please try again."),
        };
    }
}
