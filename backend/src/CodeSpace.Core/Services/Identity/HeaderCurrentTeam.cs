using CodeSpace.Core.DependencyInjection;
using Microsoft.AspNetCore.Http;

namespace CodeSpace.Core.Services.Identity;

public sealed class HeaderCurrentTeam : ICurrentTeam, IScopedDependency
{
    /// <summary>Pinned by unit test — operator dashboards / nginx rules reference this string.</summary>
    public const string HeaderName = "X-Team-Id";

    public HeaderCurrentTeam(IHttpContextAccessor accessor)
    {
        var raw = accessor?.HttpContext?.Request.Headers[HeaderName].ToString();
        Id = Guid.TryParse(raw, out var id) ? id : null;
    }

    public Guid? Id { get; }

    public bool IsSet => Id.HasValue;
}
