using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Strategies.MovingAverage;
using TradeCop001;
using TradeCop001.Licensing;
using TradingBrokers.Crypto;
using TradingBrokers.IB;
using TradingBrokers.MT5;
using TradingBrokers.Paper;
using TradingCore.Abstractions;
using TradingCore.Configuration;

if (args.Length > 0 && args[0].Equals("--fingerprint", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine(DeviceFingerprint.Compute());
    return;
}

Console.WriteLine("tradecop001 — broker-agnostic trading tool");

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

static void Log(string message) => Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] {message}");

static string ResolvePath(string configured, string baseDir)
    => Path.IsPathRooted(configured) ? configured : Path.Combine(baseDir, configured);

var baseDir = AppContext.BaseDirectory;
var publicKey = LicensingPublicKey.Base64;
var requireLicense = !string.Equals(configuration["Licensing:RequireLicense"], "false", StringComparison.OrdinalIgnoreCase);
var licensePath = ResolvePath(configuration["Licensing:LicensePath"] ?? "license.json", baseDir);
var policyPath = ResolvePath(configuration["Licensing:PolicyPath"] ?? "policy.json", baseDir);

try
{
    var license = new LicenseGuard(publicKey, licensePath, requireLicense).LoadAndValidate();
    Log($"License OK: {license.LicenseId} — {license.Licensee} (expires {license.ExpiresAt:u})");

    var policyGuard = new PolicyGuard(publicKey, policyPath);
    var policy = policyGuard.LoadAndValidate();
    Log($"Policy OK: {policy.PolicyId} (expires {policy.ExpiresAt:u})");

    var brokerConfig = configuration.GetSection("Broker").Get<BrokerConfig>() ?? new BrokerConfig();
    var strategyOptions = configuration.GetSection("Strategy").Get<SmaCrossoverOptions>() ?? new SmaCrossoverOptions();

    if (requireLicense)
        LicenseGuard.EnsureFeature(license, LicenseGuard.FeatureForBroker(brokerConfig.Type));
    policyGuard.EnsureBrokerAllowed(brokerConfig.Type);

    var factories = new IBrokerAdapterFactory[]
    {
        new PaperBrokerFactory(),
        new CryptoBrokerFactory(),
        new IBBrokerFactory(),
        new MT5BrokerFactory()
    };

    var resolver = new BrokerResolver(factories);
    var runtime = new AppRuntime(resolver, brokerConfig, strategyOptions, policyGuard, Log);
    Log($"Registered brokers: {string.Join(", ", resolver.AvailableTypes)}");
    Log($"Selected broker: {brokerConfig.Type}");

    var controlHost = configuration["Control:Host"] ?? "127.0.0.1";
    var controlPort = int.TryParse(configuration["Control:Port"], out var port) ? port : 5080;
    var loopback = controlHost is "127.0.0.1" or "localhost" or "::1";
    var controlEnabled = !string.IsNullOrWhiteSpace(policy.ControlTokenHash);

    if (!loopback && !policy.AllowRemoteControl)
        throw new InvalidOperationException(
            "Control:Host is non-loopback but the signed policy does not allow remote control. " +
            "Reissue the policy with --allow-remote and include the control.remote license feature.");

    if (requireLicense)
    {
        if (!loopback)
            LicenseGuard.EnsureFeature(license, LicenseFeatures.ControlRemote);
        else if (controlEnabled)
            LicenseGuard.EnsureFeature(license, LicenseFeatures.ControlLocal);
    }

    WebApplication? app = null;
    if (controlEnabled)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args });
        builder.WebHost.UseUrls($"http://{controlHost}:{controlPort}");
        app = builder.Build();

        bool Authorized(HttpContext ctx)
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? header["Bearer ".Length..].Trim()
                : header;
            return policyGuard.TokenMatches(token);
        }

        app.MapGet("/status", (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();
            return Results.Ok(new
            {
                app = "tradecop001",
                runtime.State,
                runtime.LastError,
                runtime.StartedAt,
                device = DeviceFingerprint.Compute(),
                policy = policy.PolicyId,
                license = license.LicenseId,
                paused = policyGuard.IsPaused,
                tradingEnabled = policy.TradingEnabled && !policyGuard.IsPaused,
                options = runtime.GetOptions()
            });
        });

        app.MapPost("/start", (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();
            runtime.Start();
            return Results.Ok(new { state = runtime.State });
        });

        app.MapPost("/stop", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();
            await runtime.StopAsync();
            return Results.Ok(new { state = runtime.State });
        });

        app.MapPost("/pause", (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();
            policyGuard.Pause();
            return Results.Ok(new { paused = true });
        });

        app.MapPost("/resume", (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();
            policyGuard.Resume();
            return Results.Ok(new { paused = false });
        });

        app.MapPost("/config", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();
            var options = await ctx.Request.ReadFromJsonAsync<SmaCrossoverOptions>();
            if (options is null) return Results.BadRequest("Missing options.");
            try
            {
                runtime.UpdateOptions(options);
                return Results.Ok(new { updated = true, options = runtime.GetOptions() });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        Log($"Control API listening on http://{controlHost}:{controlPort} (token required)");
    }
    else
    {
        Log("Control API disabled (no control token hash in policy).");
    }

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
        if (app is not null)
            _ = app.StopAsync();
    };

    runtime.Start();

    try
    {
        if (app is not null)
            await app.RunAsync();
        else
            await Task.Delay(Timeout.Infinite, shutdown.Token);
    }
    catch (OperationCanceledException)
    {
        // normal shutdown
    }
    finally
    {
        await runtime.StopAsync();
        if (app is not null)
            await app.DisposeAsync();
        Log("tradecop001 stopped.");
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Startup refused: {ex.Message}");
    Environment.ExitCode = 1;
}
