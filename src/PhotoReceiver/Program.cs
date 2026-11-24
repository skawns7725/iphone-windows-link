using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http.Features;
using PhotoReceiver;

using var key = RSA.Create(2048);
var certificateRequest = new CertificateRequest(
    "CN=PhotoReceiver", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
    new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
using var generatedCertificate = certificateRequest.CreateSelfSigned(
    DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
// Reload the key into the platform certificate store format required by Windows Schannel.
var pfx = generatedCertificate.Export(X509ContentType.Pfx);
using var certificate = new X509Certificate2(pfx);
CryptographicOperations.ZeroMemory(pfx);

var device = new DeviceInfo(
    Environment.MachineName, OperatingSystem.IsWindows() ? "Windows" : "PhotoReceiver",
    "desktop", certificate.GetCertHashString(HashAlgorithmName.SHA256));
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var destination = builder.Configuration["ReceiveDirectory"] ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PhonePhotos");
Directory.CreateDirectory(destination);

builder.WebHost.ConfigureKestrel(options =>
    options.Listen(IPAddress.Any, DiscoveryProtocol.Port, endpoint => endpoint.UseHttps(certificate)));
builder.Services.AddSingleton(device);
builder.Services.AddSingleton(_ => new UploadSessions(destination));
builder.Services.AddHostedService<DiscoveryService>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    await next(context);
    app.Logger.LogInformation("{Method} {Path} → {StatusCode}", context.Request.Method,
        context.Request.Path, context.Response.StatusCode);
});
var api = app.MapGroup("/api/localsend/v2");
api.MapGet("/info", () => device);
api.MapPost("/register", (DeviceInfo sender) => Results.Ok(device));
api.MapPost("/prepare-upload", (PrepareRequest request, HttpContext context, UploadSessions sessions) =>
{
    if (context.Connection.RemoteIpAddress is not { } sender ||
        !UploadSessions.HasValidMetadata(request))
        return Results.BadRequest();

    return request.Files!.Count == 0
        ? Results.NoContent()
        : Results.Ok(sessions.Prepare(request.Files, sender));
});
api.MapPost("/upload", async (string sessionId, string fileId, string token,
    HttpContext context, UploadSessions sessions) =>
{
    // UploadSessions enforces the advertised length while streaming.
    if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        limit.MaxRequestBodySize = null;
    return await sessions.UploadAsync(sessionId, fileId, token, context);
});
api.MapPost("/cancel", (string sessionId, HttpContext context, UploadSessions sessions) =>
    sessions.Cancel(sessionId, context.Connection.RemoteIpAddress) ? Results.Ok() : Results.StatusCode(403));

Console.WriteLine($"{device.Alias} | {destination}");
await app.RunAsync();
