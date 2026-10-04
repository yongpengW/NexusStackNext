using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.BuildingBlocks.Web;

internal sealed class ApiProblemDetailsWriter(TimeProvider clock) : IProblemDetailsWriter
{
    public bool CanWrite(ProblemDetailsContext context) => true;

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        if (context.HttpContext.Features.Get<IExceptionHandlerFeature>()?.Error is CommittedFactCapacityBusyException)
        {
            context.ProblemDetails.Extensions["errorCode"] = CommittedFactCapacityBusyException.Reason.Code;
        }
        var problem = new ApiProblemDetails(context.ProblemDetails, context.HttpContext, clock);
        context.HttpContext.Response.StatusCode = problem.Code;
        return new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(
            problem, options: null, contentType: "application/problem+json", cancellationToken: context.HttpContext.RequestAborted));
    }
}
