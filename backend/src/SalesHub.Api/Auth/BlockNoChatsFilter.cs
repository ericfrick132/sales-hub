using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SalesHub.Api.Controllers;
using SalesHub.Infrastructure.Persistence;

namespace SalesHub.Api.Auth;

/// <summary>
/// 403 en los chats para un usuario con <see cref="RuntimeFlagExtensions.NoChatsKey"/> prendido
/// (se lee de la base en cada request: sacar o devolver el acceso aplica al instante).
/// </summary>
public class BlockNoChatsFilter : IAsyncActionFilter
{
    private readonly ApplicationDbContext _db;

    public BlockNoChatsFilter(ApplicationDbContext db) { _db = db; }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated == true
            && await _db.HasNoChatsAsync(CurrentUser.Id(user), context.HttpContext.RequestAborted))
        {
            context.Result = new ObjectResult(new { error = "Sin acceso a los chats" }) { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }
        await next();
    }
}
