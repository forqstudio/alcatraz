using System.Text.Json;
using Alcatraz.Application.Sandboxes.MarkSandboxRunning;
using Alcatraz.Domain.Sandboxes;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alcatraz.Infrastructure.Messaging;

internal sealed class VmReadyConsumer(
    NatsConnectionFactory connectionFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<NatsOptions> natsOptions,
    ILogger<VmReadyConsumer> logger
    ) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly NatsOptions natsOptions = natsOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connection = await connectionFactory.GetConnectionAsync(stoppingToken);

        logger.LogInformation(
            "Subscribing to NATS subject {Subject} (queue group {QueueGroup})",
            natsOptions.ReadySubject,
            natsOptions.ReadyQueueGroup);

        await foreach (var msg in connection.SubscribeAsync<byte[]>(
            natsOptions.ReadySubject,
            queueGroup: natsOptions.ReadyQueueGroup,
            cancellationToken: stoppingToken))
        {
            try
            {
                if (msg.Data is null || msg.Data.Length == 0)
                {
                    logger.LogWarning("vm.ready: empty payload, skipping");
                    continue;
                }

                var payload = JsonSerializer.Deserialize<VmReadyPayload>(msg.Data, JsonOptions);
                if (payload is null ||
                    string.IsNullOrWhiteSpace(payload.Id) ||
                    !Guid.TryParse(payload.Id, out var sandboxId) ||
                    string.IsNullOrWhiteSpace(payload.Host) ||
                    payload.Port <= 0)
                {
                    logger.LogWarning("vm.ready: malformed payload — skipping");
                    continue;
                }

                LogBootTelemetry(sandboxId, payload);

                var runtime = new SandboxRuntimeInfo(
                    payload.Host,
                    payload.Port,
                    payload.ActualVcpus,
                    payload.ActualMemoryMib,
                    payload.BootDurationMs,
                    payload.ReadyAtUtc,
                    payload.VmmVersion,
                    payload.VmmState,
                    payload.FirecrackerPid,
                    payload.SocketPath ?? string.Empty,
                    payload.TapDevice ?? string.Empty,
                    payload.MacAddress ?? string.Empty,
                    payload.VmIp ?? string.Empty,
                    payload.HostGatewayIp ?? string.Empty,
                    payload.NfsPort,
                    payload.WorkerSlotIndex,
                    payload.RootfsPath ?? string.Empty,
                    payload.KernelPath ?? string.Empty);

                using var scope = scopeFactory.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();

                var result = await sender.Send(
                    new MarkSandboxRunningCommand(sandboxId, runtime),
                    stoppingToken);

                if (result.IsFailure)
                {
                    logger.LogWarning(
                        "vm.ready handler failed for sandbox {SandboxId}: {Error}",
                        sandboxId,
                        result.Error);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "vm.ready: unhandled exception while processing message");
            }
        }
    }

    private void LogBootTelemetry(Guid sandboxId, VmReadyPayload payload) =>
        logger.LogInformation(
            "vm.ready boot telemetry — sandbox {SandboxId}: total {TotalMs}ms (overlay {OverlayMs}ms, fc_boot {FcBootMs}ms, sshd_probe {SshdProbeMs}ms)",
            sandboxId,
            payload.BootDurationMs,
            payload.PhaseOverlayPrepMs,
            payload.PhaseFcBootMs,
            payload.PhaseSshdProbeMs);

    private sealed record VmReadyPayload(
        string Id,
        string Host,
        int Port,
        int ActualVcpus,
        int ActualMemoryMib,
        int BootDurationMs,
        DateTime ReadyAtUtc,
        string? VmmVersion,
        string? VmmState,
        int? FirecrackerPid,
        string? SocketPath,
        string? TapDevice,
        string? MacAddress,
        string? VmIp,
        string? HostGatewayIp,
        int NfsPort,
        int WorkerSlotIndex,
        string? RootfsPath,
        string? KernelPath,
        long PhaseOverlayPrepMs,
        long PhaseFcBootMs,
        long PhaseSshdProbeMs);
}
