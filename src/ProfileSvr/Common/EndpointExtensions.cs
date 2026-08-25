using System.Reflection;

namespace ProfileSvr.Common;

public static class EndpointExtensions
{
    /// <summary>
    /// Discovers every IEndpoint implementation in the assembly and maps its route.
    /// </summary>
    public static IEndpointRouteBuilder MapEndpoints(this IEndpointRouteBuilder app)
    {
        var endpointTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } &&
                        t.IsAssignableTo(typeof(IEndpoint)));

        foreach (var type in endpointTypes)
        {
            var map = type.GetMethod(nameof(IEndpoint.Map), BindingFlags.Public | BindingFlags.Static);
            map?.Invoke(null, [app]);
        }

        return app;
    }
}
