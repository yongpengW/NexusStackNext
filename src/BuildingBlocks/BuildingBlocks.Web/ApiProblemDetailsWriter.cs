using Microsoft.AspNetCore.Http;

namespace NexusStackNext.BuildingBlocks.Web;

internal sealed class ApiProblemDetailsWriter(TimeProvider clock) : IProblemDetailsWriter
{
    public bool CanWrite(ProblemDetailsContext context) => true;

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        var problem = new ApiProblemDetails(context.ProblemDetails, context.HttpContext, clock);
        context.HttpContext.Response.StatusCode = problem.Code;
        return new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(
            problem, options: null, contentType: "application/problem+json", cancellationToken: context.HttpContext.RequestAborted));
    }
}
