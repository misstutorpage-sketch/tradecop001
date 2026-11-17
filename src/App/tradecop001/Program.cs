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
using TradingCore.Engine;

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

    // Profile defaults, optional account-type suffix, then explicit overrides.
    var symbolMap = BrokerProfileCatalog.ResolveSymbols(profile, brokerConfig);
    if (symbolMap.Count > 0)
    {
        var examples = string.Join(", ", symbolMap.Take(4).Select(p => $"{p.Key}->{p.Value}"));
        Log($"Symbol map: {symbolMap.Count} canonical symbols ({examples}{(symbolMap.Count > 4 ? ", ..." : "")})");
    }

    decimal ApplyVenueMinimum(string symbol, decimal quantity)
    {
        var minimum = BrokerProfileCatalog.MinVolume(profile, symbol);
        return minimum is > 0m && quantity < minimum ? minimum.Value : quantity;
    }

    // Closed trades are kept locally so the morphic engine can be reviewed and
    // tuned from real fills. No network calls; JSONL beside the executable.
    var learningEnabled = !string.Equals(
        configuration["Learning:Enabled"], "false", StringComparison.OrdinalIgnoreCase);
    var ledgerPath = ResolvePath(configuration["Learning:LedgerPath"] ?? "trades.jsonl", baseDir);
    var ledger = new TradeLedger(ledgerPath, learningEnabled);
    Log($"Learning ledger: {(learningEnabled ? ledgerPath : "disabled")} ({ledger.Count} stored trades)");

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

        // Conservative halves the configured size; that can fall below an MT5
        // venue minimum (0.01 lots). Raise to the known minimum before the
        // policy check so the first demo order is accepted.
        var minVolume = BrokerProfileCatalog.MinVolume(profile, preset.Symbol);
        if (minVolume is > 0m && preset.BaseQuantity < minVolume)
        {
            Log($"Quantity {preset.BaseQuantity} is below the venue minimum; using {minVolume} for {preset.Symbol}.");
            preset.BaseQuantity = minVolume.Value;
        }

        AdaptivePresets.Validate(preset);
        policyGuard.EnsureOrderAllowed(preset.Symbol, preset.BaseQuantity);

        adaptiveStrategy = new AdaptiveStrategy(preset);
        // Learning is best-effort: a ledger write must never stop trading.
        adaptiveStrategy.TradeCompleted += (_, trade) =>
        {
            try { ledger.Append(trade); }
            catch (Exception ex) { Log($"Learning record failed: {ex.Message}"); }
        };
        runtime = new AppRuntime(resolver, brokerConfig, adaptiveStrategy, policyGuard, Log, symbolMap, news);
        Log($"Strategy: adaptive (mode={preset.Mode}, risk={preset.Risk})");
    }
    else
    {
        var minVolume = BrokerProfileCatalog.MinVolume(profile, strategyOptions.Symbol);
        if (minVolume is > 0m && strategyOptions.Quantity < minVolume)
        {
            Log($"Quantity {strategyOptions.Quantity} is below the venue minimum; using {minVolume} for {strategyOptions.Symbol}.");
            strategyOptions.Quantity = minVolume.Value;
        }

        runtime = new AppRuntime(resolver, brokerConfig, strategyOptions, policyGuard, Log, symbolMap, news);
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
                broker = runtime.ActiveBrokerName,
                venueSymbols = symbolMap.Count,
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
                next.BaseQuantity = ApplyVenueMinimum(next.Symbol, next.BaseQuantity);
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
                next.BaseQuantity = ApplyVenueMinimum(next.Symbol, next.BaseQuantity);
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
                options.BaseQuantity = ApplyVenueMinimum(options.Symbol, options.BaseQuantity);
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

        // Local, read-only daily summary for progress updates. Uses the session
        // journal plus a best-effort venue account/positions snapshot.
        app.MapGet("/daily", async (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();

            var now = DateTimeOffset.Now;
            var tradingDayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
            var orders = runtime.Journal.Since(tradingDayStart);
            var account = await runtime.TryGetAccountAsync(ctx.RequestAborted);
            var positions = await runtime.TryGetPositionsAsync(ctx.RequestAborted);

            var report = DailyReportBuilder.Build(
                now,
                profile?.Key ?? brokerConfig.Name,
                runtime.ActiveBrokerName,
                adaptiveStrategy is null ? "SMA crossover" : "adaptive morphic",
                runtime.State,
                runtime.StartedAt,
                runtime.LastError,
                policy.PolicyId,
                license.LicenseId,
                newsProvider.Name,
                news.Enabled,
                account,
                positions,
                orders);

            return Results.Ok(new
            {
                report,
                adaptive = adaptiveStrategy?.GetStatus()
            });
        });

        // Local-only learning view. Summarises real closed trades from this device
        // and returns recent trades without their bulky context bars. Nothing is
        // uploaded and no parameters are auto-tuned from this data.
        app.MapGet("/learning", (HttpContext ctx) =>
        {
            if (!Authorized(ctx)) return Results.Unauthorized();
            var summary = ledger.Summarize();
            var recent = ledger.Recent(10).Select(t => new
            {
                t.Symbol,
                t.Direction,
                t.EntryTime,
                t.ExitTime,
                t.EntryPrice,
                t.ExitPrice,
                t.Quantity,
                t.PnlPercent,
                t.ExitReason,
                t.EntryMode,
                t.EntryRisk,
                t.ExitMode,
                t.ExitRisk,
                t.Regime,
                t.SignalStrength,
                t.SignalReason,
                contextBars = t.EntryContext.Count + t.ExitContext.Count
            }).ToArray();
            return Results.Ok(new { summary, recent });
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
