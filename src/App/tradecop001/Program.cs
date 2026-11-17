using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Strategies.Adaptive;
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

// Presets supply the tuning defaults; only keys the operator actually wrote
// in configuration are allowed to override them.
static void OverrideExplicitlySet<T>(IConfiguration section, T target, T bound) where T : class
{
    foreach (var property in typeof(T).GetProperties())
    {
        if (!property.CanWrite || section[property.Name] is null)
            continue;
        property.SetValue(target, property.GetValue(bound));
    }
}

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

    // Applying a profile must happen before licence/policy checks: it sets the broker
    // type (mt5/ib) and venue symbol map used by the rest of the pipeline.
    var profile = BrokerProfileCatalog.Apply(brokerConfig, configuration["Broker:Profile"]);
    if (profile is not null)
        Log($"Broker profile: {profile.DisplayName} ({profile.Key}) -> type={brokerConfig.Type}");

    // Research is opt-in. The shipped provider is offline: no network calls, no keys.
    INewsProvider newsProvider = new NullNewsProvider();
    var news = new NewsBlackoutGuard(
        newsProvider,
        int.TryParse(configuration["Research:BlackoutMinutesBefore"], out var minutesBefore) ? minutesBefore : 15,
        int.TryParse(configuration["Research:BlackoutMinutesAfter"], out var minutesAfter) ? minutesAfter : 15)
    {
        Enabled = bool.TryParse(configuration["Research:EnableBlackout"], out var blackoutEnabled) && blackoutEnabled
    };
    Log($"Research: {newsProvider.Name} (news blackout {(news.Enabled ? "on" : "off")})");

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

    var strategyType = configuration["Strategy:Type"] ?? "sma";
    AdaptiveStrategy? adaptiveStrategy = null;
    AppRuntime runtime;

    if (strategyType.Equals("adaptive", StringComparison.OrdinalIgnoreCase))
    {
        var adaptiveSection = configuration.GetSection("Strategy:Adaptive");
        var bound = adaptiveSection.Get<AdaptiveOptions>() ?? new AdaptiveOptions();
        var preset = AdaptivePresets.For(bound.Mode, bound.Risk, bound.Symbol, bound.BaseQuantity);
        OverrideExplicitlySet(adaptiveSection, preset, bound);
        AdaptivePresets.Validate(preset);
        policyGuard.EnsureOrderAllowed(preset.Symbol, preset.BaseQuantity);

        adaptiveStrategy = new AdaptiveStrategy(preset);
        runtime = new AppRuntime(resolver, brokerConfig, adaptiveStrategy, policyGuard, Log, profile?.Symbols, news);
        Log($"Strategy: adaptive (mode={preset.Mode}, risk={preset.Risk})");
    }
    else
    {
        runtime = new AppRuntime(resolver, brokerConfig, strategyOptions, policyGuard, Log, profile?.Symbols, news);
        Log("Strategy: SMA crossover");
    }

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
                app = "tradecop001 morphic AI",
                strategy = adaptiveStrategy is null ? "sma" : "adaptive",
                runtime.State,
                runtime.LastError,
                runtime.StartedAt,
                device = DeviceFingerprint.Compute(),
                policy = policy.PolicyId,
                license = license.LicenseId,
                brokerProfile = profile?.Key ?? brokerConfig.Name,
                research = newsProvider.Name,
                newsBlackout = news.Enabled,
                paused = policyGuard.IsPaused,
                tradingEnabled = policy.TradingEnabled && !policyGuard.IsPaused,
                options = runtime.GetOptions(),
                adaptive = adaptiveStrategy?.GetStatus()
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
            if (adaptiveStrategy is not null)
                return Results.BadRequest("Adaptive strategy is active — use /adaptive/config, /mode or /risk.");
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

        // Adaptive-mode controls: one-call presets plus fine-grained tuning.
        app.MapGet("/adaptive", (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();
            return adaptiveStrategy is null
                ? Results.NotFound(new { error = "Adaptive strategy is not active. Set Strategy:Type=adaptive." })
                : Results.Ok(adaptiveStrategy.GetStatus());
        });

        app.MapPost("/mode", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();
            if (adaptiveStrategy is null) return Results.NotFound(new { error = "Adaptive strategy is not active." });
            var body = await ctx.Request.ReadFromJsonAsync<ModeRequest>();
            if (body?.Mode is null || !Enum.TryParse<TradingMode>(body.Mode, true, out var mode))
                return Results.BadRequest("Unknown mode. Use: balanced, trend, range, scalp, defensive.");
            try
            {
                var next = AdaptivePresets.WithMode(adaptiveStrategy.GetOptions(), mode);
                policyGuard.EnsureOrderAllowed(next.Symbol, next.BaseQuantity);
                adaptiveStrategy.UpdateOptions(next);
                return Results.Ok(adaptiveStrategy.GetStatus());
            }
            catch (Exception ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapPost("/risk", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();
            if (adaptiveStrategy is null) return Results.NotFound(new { error = "Adaptive strategy is not active." });
            var body = await ctx.Request.ReadFromJsonAsync<RiskRequest>();
            if (body?.Risk is null || !Enum.TryParse<RiskProfile>(body.Risk, true, out var risk))
                return Results.BadRequest("Unknown risk. Use: conservative, balanced, aggressive.");
            try
            {
                var next = AdaptivePresets.WithRisk(adaptiveStrategy.GetOptions(), risk);
                policyGuard.EnsureOrderAllowed(next.Symbol, next.BaseQuantity);
                adaptiveStrategy.UpdateOptions(next);
                return Results.Ok(adaptiveStrategy.GetStatus());
            }
            catch (Exception ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapPost("/adaptive/config", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();
            if (adaptiveStrategy is null) return Results.NotFound(new { error = "Adaptive strategy is not active." });
            var options = await ctx.Request.ReadFromJsonAsync<AdaptiveOptions>();
            if (options is null) return Results.BadRequest("Missing options.");
            try
            {
                AdaptivePresets.Validate(options);
                policyGuard.EnsureOrderAllowed(options.Symbol, options.BaseQuantity);
                adaptiveStrategy.UpdateOptions(options);
                return Results.Ok(adaptiveStrategy.GetStatus());
            }
            catch (Exception ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        // Read-only view of opt-in research. With the default provider this simply
        // reports "None (offline)" and an empty event list.
        app.MapGet("/research", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();
            var snapshot = await newsProvider.GetSnapshotAsync();
            return Results.Ok(new
            {
                provider = newsProvider.Name,
                blackoutEnabled = news.Enabled,
                upcomingEvents = snapshot.UpcomingEvents,
                marketSentiment = snapshot.MarketSentiment
            });
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

sealed record ModeRequest(string? Mode);
sealed record RiskRequest(string? Risk);
