using CodeSpace.Api.Extensions.Hangfire;

namespace CodeSpace.Api.Extensions;

public static class HangfireExtension
{
    public static void AddCodeSpaceHangfire(this IServiceCollection services, IConfiguration configuration)
    {
        new CodeSpaceHangfireRegistrar().RegisterHangfire(services, configuration);
    }

    public static void UseCodeSpaceHangfire(this IApplicationBuilder app, IConfiguration configuration)
    {
        new CodeSpaceHangfireRegistrar().ApplyHangfire(app, configuration);
    }
}
