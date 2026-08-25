namespace ProfileSvr.Common;

/// <summary>
/// Implemented by each vertical slice to register its own route.
/// </summary>
public interface IEndpoint
{
    static abstract void Map(IEndpointRouteBuilder app);
}
