using AIEnabledRma.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace AIEnabledRma.Web.Controllers;

public sealed class HomeController : Controller
{
    [HttpGet]
    public IActionResult Index() => View();

    [HttpGet]
    public IActionResult Error() =>
        View(new ErrorViewModel
        {
            RequestId = HttpContext.TraceIdentifier,
        });
}
