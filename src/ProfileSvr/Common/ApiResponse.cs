using FluentValidation.Results;

namespace ProfileSvr.Common;

/// <summary>Uniform envelope for every API response.</summary>
public record ApiResponse<T>(bool IsSuccess, T? Data, string? Message);

public static class ApiResults
{
    private const string DefaultSuccess = "Operation successful.";

    public static IResult Ok<T>(T data, string? message = null) =>
        Results.Ok(new ApiResponse<T>(true, data, message ?? DefaultSuccess));

    public static IResult Created<T>(string uri, T data, string? message = null) =>
        Results.Created(uri, new ApiResponse<T>(true, data, message ?? DefaultSuccess));

    public static IResult Accepted<T>(T data, string? message = null) =>
        Results.Accepted(value: new ApiResponse<T>(true, data, message ?? DefaultSuccess));

    public static IResult Fail(int statusCode, string message) =>
        Results.Json(new ApiResponse<object>(false, null, message), statusCode: statusCode);

    public static IResult NotFound(string message) => Fail(StatusCodes.Status404NotFound, message);

    public static IResult Conflict(string message) => Fail(StatusCodes.Status409Conflict, message);

    public static IResult ValidationFailed(ValidationResult validation) =>
        Fail(StatusCodes.Status400BadRequest,
            string.Join(" ", validation.Errors.Select(e => e.ErrorMessage)));
}
