using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FormUp.Tests.Infrastructure;

/// <summary>
/// Boots a test host one at a time and retries the one boot failure caused by FastEndpoints' process-wide
/// <c>Config.SerOpts</c>: a request served by another collection while a host boots can lock the options
/// instance the boot is still configuring.
/// </summary>
public static class FastEndpointsBootGate
{
    private const int MaxAttempts = 3;

    // UNVERIFIED: the hazard is read from FastEndpoints v8.0.1 source, MapFastEndpoints assigns a fresh
    // Cfg.SerOpts.Options (line 74), runs the configAction (77) and calls ConfigureSerializer (96) —
    // https://github.com/FastEndpoints/FastEndpoints/blob/v8.0.1/Src/Library/Main/MainExtensions.cs
    private static readonly object Gate = new();

    /// <summary>
    /// Starts the factory's server inside the process-wide gate, retrying a boot that lost the race.
    /// A retry works because <c>StartServer</c> builds a fresh host builder each call while no host is set.
    /// </summary>
    public static void Start<TEntryPoint>(WebApplicationFactory<TEntryPoint> factory)
        where TEntryPoint : class
    {
        lock (Gate)
        {
            Exception? lastFailure = null;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    factory.StartServer();
                    return;
                }
                catch (Exception ex) when (IsSerializerOptionsRace(ex))
                {
                    lastFailure = ex;
                }
            }

            throw new InvalidOperationException(
                $"Test host failed to boot after {MaxAttempts} attempts: FastEndpoints serializer options were locked by concurrent traffic.",
                lastFailure);
        }
    }

    /// <summary>Boots a <c>WithWebHostBuilder</c>-derived factory through the gate and returns it.</summary>
    public static WebApplicationFactory<TEntryPoint> Booted<TEntryPoint>(WebApplicationFactory<TEntryPoint> factory)
        where TEntryPoint : class
    {
        Start(factory);
        return factory;
    }

    // The lock surfaces at two sites inside MapFastEndpoints: the configAction's Converters.Add (Program.cs)
    // and ConfigureSerializer's TypeInfoResolver setter. Both pass through MapFastEndpoints.
    private static bool IsSerializerOptionsRace(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException
                && current.Message.Contains(nameof(JsonSerializerOptions), StringComparison.Ordinal)
                && current.Message.Contains("read-only", StringComparison.Ordinal)
                && current.StackTrace?.Contains("FastEndpoints.MainExtensions.MapFastEndpoints", StringComparison.Ordinal) == true)
            {
                return true;
            }
        }

        return false;
    }
}
