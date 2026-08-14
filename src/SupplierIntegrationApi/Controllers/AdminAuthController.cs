using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SupplierIntegrationApi.Controllers;

[ApiController]
[Route("api/admin/auth-check")]
[Authorize(Roles = "Admin")]
public sealed class AdminAuthController : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult Check() => Ok(new { authenticated = true });
}
