using System.Diagnostics;
using FileBrowser.FileSystem;
using Microsoft.AspNetCore.Http.Features;

// creates application builder
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// configure FileBrowser options
var options = builder.Configuration.GetSection("FileBrowser")
    .Get<FileBrowserOptions>() ?? new();
if (options.MaximumSearchResults < 1 || options.MaximumUploadBytes < 1)
    throw new InvalidOperationException("FileBrowser limits must be greater than zero.");

// registers FileBrowserOptions with .NET dependency injection container
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(sp => new FileSystemService(
    options.HomeDirectory,
    sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath));
// Configures .NET parsing behavior
builder.Services.Configure<FormOptions>(x =>
    x.MultipartBodyLengthLimit = options.MaximumUploadBytes);
builder.WebHost.ConfigureKestrel(x =>
    x.Limits.MaxRequestBodySize = options.MaximumUploadBytes + 1_048_576);

// Constructs web application
var app = builder.Build();
app.Logger.LogInformation("File browser root: {Root}",
    app.Services.GetRequiredService<FileSystemService>().RootPath);

// adds middleware to HTTP request pipeline for logging
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        context.Response.StatusCode = 499;
    }
    catch (Exception exception)
    {
        if (context.Response.HasStarted) throw;

        var error = exception switch
        {
            InvalidPathException e => (400, "InvalidPath", e.Message),
            DestinationExistsException e => (409, "DestinationExists", e.Message),
            DirectoryNotFoundException e => (404, "DirectoryNotFound", e.Message),
            FileNotFoundException e => (404, "FileNotFound", e.Message),
            BadHttpRequestException e => (e.StatusCode, "InvalidRequest", e.Message),
            InvalidDataException e when e.Message.Contains("length limit",
                StringComparison.OrdinalIgnoreCase) =>
                (413, "UploadTooLarge", "The upload exceeds the configured size limit."),
            InvalidDataException e => (400, "InvalidRequest", e.Message),
            UnauthorizedAccessException =>
                (403, "AccessDenied", "The server cannot access the requested item."),
            IOException e => (409, "FileSystemConflict", e.Message),
            _ => (500, "UnexpectedError", "An unexpected server error occurred.")
        };

        if (error.Item1 == 500) app.Logger.LogError(exception, "Unhandled request error");
        context.Response.Clear();
        context.Response.StatusCode = error.Item1;
        await context.Response.WriteAsJsonAsync(
            new { code = error.Item2, message = error.Item3 });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = x => x.Context.Response.Headers["Cache-Control"] = "no-cache"
});

// create api route group
var files = app.MapGroup("/api/files");
files.AddEndpointFilter(async (context, next) =>
{
    var timer = Stopwatch.StartNew();
    var result = await next(context);
    var http = context.HttpContext;
    http.Response.Headers["Server-Timing"] =
        $"filesystem;dur={timer.Elapsed.TotalMilliseconds:F1}";
    app.Logger.LogInformation("{Method} {Path} completed in {ElapsedMs} ms",
        http.Request.Method, http.Request.Path, timer.ElapsedMilliseconds);
    return result;
});

files.MapGet("/browse", (string? path, FileSystemService fs) => fs.Browse(path));

files.MapGet("/search", (string? path, string? query, FileSystemService fs,
    CancellationToken cancellation) =>
    fs.Search(path, query ?? "", options.MaximumSearchResults, cancellation));

files.MapGet("/download", (string path, FileSystemService fs) => Results.File(
    fs.OpenDownload(path), "application/octet-stream", Path.GetFileName(path),
    enableRangeProcessing: true));

files.MapPost("/upload", async (string? path, bool? overwrite, HttpRequest request,
    FileSystemService fs, CancellationToken cancellation) =>
{
    if (!request.HasFormContentType)
        throw new BadHttpRequestException("Uploads must use multipart/form-data.");
    var form = await request.ReadFormAsync(cancellation);
    await fs.UploadAsync(path, form.Files, overwrite ?? false,
        options.MaximumUploadBytes, cancellation);
    return Results.NoContent();
});

files.MapPost("/folders", (CreateFolderRequest request, FileSystemService fs) =>
{
    fs.CreateFolder(request.ParentPath, request.Name);
    return Results.NoContent();
});

files.MapDelete("/", (string path, bool? recursive, FileSystemService fs) =>
{
    fs.Delete(path, recursive ?? false);
    return Results.NoContent();
});

files.MapPost("/move", (TransferRequest request, FileSystemService fs) =>
{
    fs.Move(request);
    return Results.NoContent();
});

files.MapPost("/copy", async (TransferRequest request, FileSystemService fs,
    CancellationToken cancellation) =>
{
    await fs.CopyAsync(request, cancellation);
    return Results.NoContent();
});

app.MapFallbackToFile("index.html");
app.Run();
