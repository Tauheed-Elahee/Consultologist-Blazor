# SSE Result Wrapper Notes

## Explain The Wrapper

The wrapper is `CorsResultActionResult` in `Api/ConsultGenerationJobs.cs`.

Its job is to let the Azure Functions endpoint return an ASP.NET Core `IActionResult`, while still using the .NET 10 SSE API internally:

```csharp
private sealed class CorsResultActionResult : IActionResult
{
    private readonly IResult _result;

    public CorsResultActionResult(IResult result)
    {
        _result = result;
    }

    public Task ExecuteResultAsync(ActionContext context)
    {
        FunctionCors.Apply(context.HttpContext.Request, context.HttpContext.Response);
        context.HttpContext.Response.Headers.CacheControl = "no-cache";
        return _result.ExecuteAsync(context.HttpContext);
    }
}
```

The endpoint creates the typed SSE result here:

```csharp
return new CorsResultActionResult(
    TypedResults.ServerSentEvents(events));
```

Why it exists:

- `TypedResults.ServerSentEvents(...)` returns an ASP.NET Core minimal API `IResult`.
- Azure Functions ASP.NET Core integration is already known to work with `IActionResult` in this repo.
- The wrapper adapts the `IResult` into an `IActionResult` by implementing `ExecuteResultAsync`.
- Inside `ExecuteResultAsync`, it applies the existing CORS headers, sets `Cache-Control: no-cache`, then delegates to the real SSE result with `_result.ExecuteAsync(...)`.

So the wrapper does not format SSE itself. It only bridges the return type expected by Azure Functions to the .NET 10 SSE result that actually writes the `text/event-stream` response.

## Can This Be Structured Without Wrappers?

Yes, but each option gives up something.

The cleanest no-wrapper shape would be:

```csharp
public async Task<IResult> GetEventsAsync(...)
{
    ...
    return TypedResults.ServerSentEvents(events);
}
```

or:

```csharp
public IResult GetEventsAsync(...)
    => TypedResults.ServerSentEvents(events);
```

That removes the adapter entirely. The issue is the one called out in the plan: Azure Functions isolated ASP.NET Core integration is well exercised with `IActionResult`, while direct `IResult` return support is less certain across hosting/runtime paths. If you are willing to validate it in deployed Functions, this is the simplest structure.

Other options:

1. **Use `IActionResult` only, without `TypedResults`**

   Write an `IActionResult` that calls `SseFormatter.WriteAsync(...)` directly. That avoids adapting `IResult`, but you are still creating a custom result type and you stop using `TypedResults.ServerSentEvents`.

2. **Write to `HttpResponse` directly**

   The function can set headers and call `SseFormatter.WriteAsync(events, response.Body, ct)`, then return `EmptyResult`. This avoids an `IResult` wrapper, but it is closer to manual response handling.

3. **Move CORS out of the result**

   If CORS is handled globally by Functions/platform config, the adapter becomes only:

   ```csharp
   private sealed class ResultActionResult(IResult result) : IActionResult
   {
       public Task ExecuteResultAsync(ActionContext context)
           => result.ExecuteAsync(context.HttpContext);
   }
   ```

   Still a wrapper, but minimal.

Given the current constraints, the wrapper is the smallest reliable bridge. If we want to remove it, first test direct `IResult` return locally and in the deployed Function App. If both pass the strict smoke suite and the valid SSE smoke, then the endpoint can return `TypedResults.ServerSentEvents(events)` directly.

## What Are `IActionResult` And `IResult`?

`IActionResult` and `IResult` are two ASP.NET Core abstractions for "something that knows how to write an HTTP response."

`IActionResult` is the older MVC/controller-style result interface:

```csharp
public interface IActionResult
{
    Task ExecuteResultAsync(ActionContext context);
}
```

It is commonly used by controllers and Azure Functions with ASP.NET Core integration:

```csharp
return new OkObjectResult(payload);
return new NotFoundObjectResult(error);
return new ContentResult { Content = "ok" };
```

`IResult` is the newer minimal API result interface:

```csharp
public interface IResult
{
    Task ExecuteAsync(HttpContext httpContext);
}
```

It is commonly produced by minimal API helpers:

```csharp
return Results.Ok(payload);
return TypedResults.NotFound();
return TypedResults.ServerSentEvents(events);
```

In this migration, the important bit is:

```csharp
TypedResults.ServerSentEvents(events)
```

returns an `IResult`, not an `IActionResult`.

But the Azure Function endpoint is shaped as:

```csharp
public async Task<IActionResult> GetEventsAsync(...)
```

So the wrapper adapts this:

```csharp
IResult
```

into this:

```csharp
IActionResult
```

by calling the `IResult`'s `ExecuteAsync(HttpContext)` from inside `IActionResult.ExecuteResultAsync(...)`.

Short version:

- `IActionResult`: MVC-style HTTP response result.
- `IResult`: minimal API-style HTTP response result.
- Both write HTTP responses.
- They use different execution methods and context types.
- The wrapper lets a minimal API SSE result be returned from an Azure Function path that expects MVC-style results.

## MVC-Style Versus Minimal API-Style

In ASP.NET Core, "MVC-style" usually means the controller/action model. "Minimal API-style" means the newer endpoint model built around route handlers and `Results`/`TypedResults`.

### MVC / Controller Style

MVC came first in ASP.NET Core because it carried forward the established web app pattern from ASP.NET MVC and Web API:

```csharp
[ApiController]
[Route("api/jobs")]
public class JobsController : ControllerBase
{
    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        return Ok(new { id });
    }
}
```

Characteristics:

- Uses controllers and action methods.
- Uses attributes like `[HttpGet]`, `[Route]`, `[FromBody]`.
- Return types are often `IActionResult`, `ActionResult<T>`, or concrete MVC result types.
- Has a rich MVC pipeline: model binding, filters, validation, formatters, controller conventions.
- Good for larger APIs with lots of structure and cross-cutting behaviors.

### Minimal API Style

Minimal APIs were added later to make simple APIs less ceremony-heavy:

```csharp
app.MapGet("/api/jobs/{id}", (string id) =>
{
    return TypedResults.Ok(new { id });
});
```

Characteristics:

- Uses route handlers directly in `Program.cs` or endpoint extension methods.
- Return types are often `IResult`, `Results<...>`, or typed result classes.
- Less controller structure.
- Lower ceremony and very explicit.
- Good for small APIs, microservices, focused endpoints, and modern streaming primitives.

### Why One Is Older

MVC-style predates minimal APIs because ASP.NET historically centered around the Model-View-Controller architecture. Even for JSON APIs, ASP.NET Web API and then ASP.NET Core MVC used controller classes as the main organizing unit.

Minimal APIs came later because many APIs do not need the full MVC/controller machinery. Microsoft added them to provide a simpler, lighter endpoint model, especially as .NET moved toward smaller services, containers, and more explicit HTTP APIs.

### In This Repo

Azure Functions with ASP.NET Core integration has proven examples using MVC-style `IActionResult`:

```csharp
public static IActionResult Run(HttpRequest req)
{
    return new OkObjectResult("ok");
}
```

But the .NET 10 SSE helper is from the minimal API result family:

```csharp
TypedResults.ServerSentEvents(events)
```

That returns `IResult`.

So the wrapper bridges the older/controller-compatible Azure Functions return shape with the newer minimal API SSE helper.
